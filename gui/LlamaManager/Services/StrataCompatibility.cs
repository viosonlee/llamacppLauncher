using System.Text;

namespace LlamaManager.Services;

/// <summary>
/// Mirrors the Strata APEX engine's expert-format support so the launcher can
/// explain, before packing, exactly which layers will run on GPU and which
/// formats the engine will refuse. Single source of truth for these sets is the
/// engine build shipped in the release (STRATA_MMQ_KQUANTS=ON, STRATA_ORCA_Q4KS_MMQ=OFF):
///   STRATA_GU_FMTS   src/kernels/cuda/iq_kernels.cu:611  (gate/up decode)
///   STRATA_D_FMTS    src/kernels/cuda/iq_kernels.cu:612  (down decode)
///   native_mmvq_supported src/kernels/cuda/native_mmvq.cu:1565 (dense/head; not STRATA_MMVQ_FMTS)
///   supported()      src/prefill/moe_mmq.cu:111          (prefill MMQ; Q5_0 needs ORCA flag, off)
///   is_iq            src/kernels/cuda/iq_kernels.cu:1789
///   embed_type_supported / native_mmvq_supported (iq_kernels.cu:1888 / native_mmvq.cu:1565)
///   native_expert_supported (iq_kernels.cu:1974); generate.cpp:2067 refuses at startup.
/// Update this mirror together with the engine, not independently.
/// </summary>
public static class StrataCompatibility
{
    public enum Grade { Native, Fallback, Unsupported }

    public sealed record Finding(string Role, uint TypeId, string TypeName, Grade Decode, Grade? Prefill, int Layers);

    public sealed record Report(
        bool StructureOk,
        string StructureNote,
        IReadOnlyList<Finding> Findings,
        IReadOnlyList<string> Notes,
        IReadOnlyList<string> Blockers)
    {
        public bool IsPackable => StructureOk && Blockers.Count == 0;
    }

    // gate/up decode: iq_kernels.cu STRATA_GU_FMTS — 16,17,18,21,22,23,29,42,12,13,11,6,8
    private static readonly HashSet<uint> GuDecode = [16, 17, 18, 21, 22, 23, 29, 42, 12, 13, 11, 6, 8];
    // down decode: STRATA_D_FMTS — 20,23,42,7,6,8,14,12,13,11,22,18
    private static readonly HashSet<uint> DownDecode = [20, 23, 42, 7, 6, 8, 14, 12, 13, 11, 22, 18];
    // NativeHead::load and NativeDense use native_mmvq_supported, which also includes Q4_0 and Q6_K.
    private static readonly HashSet<uint> NativeMmvq = [2, 6, 7, 8, 11, 12, 13, 14, 20, 23, 42, 16, 17, 18, 21, 22, 29];
    // prefill MMQ (release build: STRATA_MMQ_KQUANTS=ON, STRATA_ORCA_Q4KS_MMQ=OFF → no Q5_0)
    private static readonly HashSet<uint> PrefillMmq = [42, 16, 17, 22, 18, 21, 20, 23, 8, 12, 13, 7, 14, 11];
    // is_iq: the dequant dispatch set (iq_kernels.cu:1789)
    private static readonly HashSet<uint> IsIq = [16, 17, 18, 20, 21, 22, 23, 29, 42, 11, 12, 13, 14, 7, 6, 8];

    private static readonly Dictionary<uint, string> Names = new()
    {
        [0] = "F32", [1] = "F16", [2] = "Q4_0", [3] = "Q4_1", [6] = "Q5_0", [7] = "Q5_1", [8] = "Q8_0",
        [10] = "Q2_K", [11] = "Q3_K", [12] = "Q4_K", [13] = "Q5_K", [14] = "Q6_K",
        [16] = "IQ2_XXS", [17] = "IQ2_XS", [18] = "IQ3_XXS", [20] = "IQ4_NL", [21] = "IQ3_S",
        [22] = "IQ2_S", [23] = "IQ4_XS", [29] = "IQ1_M", [30] = "BF16", [42] = "Q2_0",
    };

    public static string TypeName(uint id) => Names.GetValueOrDefault(id, "type" + id);

    public static Report Analyze(GgufMetadata md)
    {
        var blockers = new List<string>();
        var notes = new List<string>();
        var findings = new List<Finding>();
        var structure = new List<string>();
        if (md.Architecture != "qwen35moe") structure.Add($"架构为 {md.Architecture}（需要 qwen35moe）");
        if (md.LayerCount is not (40 or 41)) structure.Add($"层数为 {md.LayerCount}（需要 40 层主干，含 MTP 时 41）");
        if (md.EmbeddingLength != 2048) structure.Add($"hidden={md.EmbeddingLength}（需要 2048）");
        if (md.AttentionHeads != 16 || md.KvHeads != 2) structure.Add($"heads={md.AttentionHeads}/kv={md.KvHeads}（需要 16/2）");
        if (md.ExpertCount is < 1 or > 512 || md.ExpertUsedCount < 1 || md.ExpertUsedCount > md.ExpertCount)
            structure.Add($"experts={md.ExpertCount}/used={md.ExpertUsedCount}（需要 1–512 个专家，used 在 1–experts 之间）");

        // The engine requires the real MTP marker, not just block_count=41. The packer
        // subtracts nextn_predict_layers from the tensor-derived layer count.
        var mtpMarker = md.TensorFormats.ContainsKey("blk.40.nextn.eh_proj.weight");
        if (md.LayerCount == 41 && (!mtpMarker || md.MtpLayerCount != 1))
            structure.Add("41 层需要 blk.40.nextn.eh_proj.weight 且 nextn_predict_layers=1（40 层主干 + MTP 块）");
        if (md.LayerCount == 40 && (md.HasMtp || md.MtpLayerCount is not (null or 0)))
            structure.Add("40 层主干不能同时声明或携带 MTP 块");
        var extra = md.TensorFormats.Keys.FirstOrDefault(n => LayerNumber(n) >= md.LayerCount);
        if (extra != null) structure.Add($"张量 {extra} 超出声明的 {md.LayerCount} 层");
        var structureNote = $"{md.Architecture} / {md.LayerCount} 层 / hidden {md.EmbeddingLength} / heads {md.AttentionHeads}:{md.KvHeads} / {md.ExpertCount} 专家 top-{md.ExpertUsedCount}";
        if (structure.Count > 0) return new Report(false, structureNote, findings, notes, structure);
        if (md.RequiresPrism) blockers.Add("含 Prism 专用张量或 Hadamard 元数据，当前 Strata APEX 路径不支持此变体。");
        if (md.TensorFormats.Count == 0)
        {
            blockers.Add("未找到 GGUF 张量描述符，无法生成 Pack。");
            return new Report(true, structureNote, findings, notes, blockers);
        }

        // Every trunk layer has routed experts AND a shared expert. Missing down or
        // a whole layer must not turn into a successful report with fewer findings.
        var gateUp = new Dictionary<uint, List<int>>();   // type -> layers (gate/up must share one type)
        var down = new Dictionary<uint, List<int>>();
        var shared = new Dictionary<(string Role, uint Type), List<int>>();
        for (var layer = 0; layer < 40; layer++)
        {
            var missing = new List<string>();
            foreach (var role in new[] { "gate", "up", "down" })
            {
                foreach (var suffix in new[] { "_exps", "_shexp" })
                    if (RoleType(md, layer, role, suffix) == null) missing.Add($"ffn_{role}{suffix}.weight");
                if (RoleType(md, layer, role, "_shexp") is { } type)
                {
                    var key = (role, type);
                    if (!shared.TryGetValue(key, out var layers)) shared[key] = layers = [];
                    layers.Add(layer);
                }
            }
            foreach (var router in new[] { "ffn_gate_inp.weight", "ffn_gate_inp_shexp.weight" })
                if (!md.TensorFormats.ContainsKey($"blk.{layer}.{router}")) missing.Add(router);
            if (missing.Count > 0) blockers.Add($"blk.{layer} 缺少必需张量：{string.Join("、", missing)}。");
            uint? gate = RoleType(md, layer, "gate", "_exps");
            uint? up = RoleType(md, layer, "up", "_exps");
            uint? d = RoleType(md, layer, "down", "_exps");
            if (gate is { } g && up is { } u)
            {
                if (g != u)
                    blockers.Add($"blk.{layer} gate/up 格式不一致（{TypeName(g)} / {TypeName(u)}）；打包器要求同层同格式。");
                LayerList(gateUp, g).Add(layer);
            }
            if (d is { } dd) LayerList(down, dd).Add(layer);
        }

        if (md.LayerCount == 41)
        {
            foreach (var role in new[] { "gate", "up", "down" })
                if (RoleType(md, 40, role, "_exps") == null)
                    blockers.Add($"blk.40 缺少 ffn_{role}_exps.weight，打包器无法正确排除 MTP 层。");
            notes.Add("含内置 MTP 块；Pack 排除 blk.40。草稿 runtime 需单独提取，其量化格式不要求与主干相同。");
        }

        // Routed experts (_exps): the engine refuses at startup when a role format is
        // outside its table (generate.cpp:2067 -> native_expert_supported) — there is no
        // decode fallback. Prefill may still fall back to fp16 dequant for a native
        // decode type missing from the MMQ table (e.g. Q5_0 with ORCA off).
        foreach (var (type, layers) in gateUp.OrderBy(p => p.Key))
        {
            var decode = GuDecode.Contains(type) ? Grade.Native : Grade.Unsupported;
            var prefill = decode == Grade.Unsupported ? Grade.Unsupported
                : PrefillMmq.Contains(type) ? Grade.Native : Grade.Fallback;
            findings.Add(new Finding("gate/up", type, TypeName(type), decode, prefill, layers.Count));
            if (decode == Grade.Unsupported)
                blockers.Add($"gate/up 使用 {TypeName(type)}（层 {Span(layers)}）：引擎无该格式的 GPU kernel，启动时会拒绝并指明层号。");
            else if (prefill == Grade.Fallback)
                notes.Add($"gate/up {TypeName(type)} 的 prompt 走 fp16 去量化回退（decode 原生），长 prompt 会偏慢。");
        }
        foreach (var (type, layers) in down.OrderBy(p => p.Key))
        {
            var decode = DownDecode.Contains(type) ? Grade.Native : Grade.Unsupported;
            var prefill = decode == Grade.Unsupported ? Grade.Unsupported
                : PrefillMmq.Contains(type) ? Grade.Native : Grade.Fallback;
            findings.Add(new Finding("down", type, TypeName(type), decode, prefill, layers.Count));
            if (decode == Grade.Unsupported)
                blockers.Add($"down 使用 {TypeName(type)}（层 {Span(layers)}）：引擎无该格式的 GPU kernel，启动时会拒绝并指明层号。");
            else if (prefill == Grade.Fallback)
                notes.Add($"down {TypeName(type)} 的 prompt 走 fp16 去量化回退（decode 原生），长 prompt 会偏慢。");
        }
        // Quantized shared weights are served from the GGUF by NativeDense; the pack
        // contains index entries, not a generic dequant fallback for unknown formats.
        foreach (var (key, layers) in shared.OrderBy(p => p.Key.Role).ThenBy(p => p.Key.Type))
        {
            var supported = NativeMmvq.Contains(key.Type);
            findings.Add(new Finding("shared " + key.Role, key.Type, TypeName(key.Type),
                supported ? Grade.Native : Grade.Unsupported, null, layers.Count));
            if (!supported)
                blockers.Add($"共享专家 {key.Role} 使用 {TypeName(key.Type)}（层 {Span(layers)}）：NativeDense 不支持该格式。");
        }

        // With --native, NativeHead::load is mandatory: F32/F16/BF16 are NOT a head
        // fallback. Embeddings have a separate gather set (is_iq plus BF16).
        foreach (var (name, role) in new[] { ("token_embd.weight", "embedding"), ("output.weight", "lm_head") })
        {
            if (!md.TensorFormats.TryGetValue(name, out var type))
            {
                blockers.Add("缺少必需张量：" + name + "。");
                continue;
            }
            var supported = role == "embedding" ? IsIq.Contains(type) || type == 30 : NativeMmvq.Contains(type);
            findings.Add(new Finding(role, type, TypeName(type), supported ? Grade.Native : Grade.Unsupported, null, 1));
            if (!supported)
                blockers.Add($"{name} 为 {TypeName(type)}：引擎的 {(role == "embedding" ? "embedding gather" : "native LM head")} 不支持该格式。");
        }

        return new Report(true, structureNote, findings, notes, blockers);
    }

    private static int LayerNumber(string name)
    {
        if (!name.StartsWith("blk.", StringComparison.Ordinal)) return -1;
        var end = name.IndexOf('.', 4);
        return end > 4 && int.TryParse(name.AsSpan(4, end - 4), out var layer) ? layer : -1;
    }

    private static uint? RoleType(GgufMetadata md, int layer, string role, string suffix)
        => md.TensorFormats.TryGetValue($"blk.{layer}.ffn_{role}{suffix}.weight", out var t) ? t : null;

    private static string Span(List<int> layers)
        => layers.Count == 1 ? layers[0].ToString()
            : layers.Count <= 8 ? string.Join(",", layers)
            : $"{layers[0]}–{layers[^1]}（共 {layers.Count} 层）";

    private static List<int> LayerList(Dictionary<uint, List<int>> map, uint type)
    {
        if (!map.TryGetValue(type, out var list)) map[type] = list = new List<int>();
        return list;
    }

    /// <summary>One-line summary for the GUI status row.</summary>
    public static string Summary(Report report)
    {
        if (!report.IsPackable)
            return "不支持：" + (report.Blockers.FirstOrDefault() ?? report.StructureNote) +
                (report.Blockers.Count > 1 ? $"（共 {report.Blockers.Count} 项）" : "");
        var experts = report.Findings.Where(f => f.Role is "gate/up" or "down").ToArray();
        var types = string.Join(" / ", experts.Select(f => f.TypeName).Distinct());
        var fallback = experts.Where(f => f.Prefill == Grade.Fallback).Select(f => f.TypeName).Distinct().ToArray();
        var head = report.Findings.Single(f => f.Role == "lm_head");
        return $"兼容性预检通过：专家 {types} decode 原生；" +
            (fallback.Length == 0 ? "prefill MMQ" : $"prefill fp16 回退（{string.Join(" / ", fallback)}）") +
            $"；LM head {head.TypeName} 原生。";
    }

    /// <summary>Complete header verdict for the pack log and the status row's tooltip.</summary>
    public static string Describe(Report report)
    {
        var sb = new StringBuilder("Strata APEX 兼容性预检\n结构：").AppendLine(report.StructureNote);
        foreach (var f in report.Findings)
        {
            sb.Append(f.Role).Append(' ').Append(f.TypeName).Append(" ×").Append(f.Layers)
                .Append(f.Decode == Grade.Native ? "：原生" : "：不支持");
            if (f.Prefill is { } prefill)
                sb.Append("；prefill ").Append(prefill switch { Grade.Native => "MMQ", Grade.Fallback => "fp16 去量化回退", _ => "不支持" });
            sb.AppendLine();
        }
        foreach (var note in report.Notes) sb.Append("说明：").AppendLine(note);
        foreach (var blocker in report.Blockers) sb.Append("阻断：").AppendLine(blocker);
        return sb.Append("结论：").Append(report.IsPackable ? "预检通过" : "预检未通过").ToString();
    }
}
