using System.Text;

namespace LlamaManager.Services;

/// <summary>
/// Mirrors the Strata APEX engine's expert-format support so the launcher can
/// explain, before packing, exactly which layers will run on GPU and which
/// formats the engine will refuse. Single source of truth for these sets is the
/// engine build shipped in the release (STRATA_MMQ_KQUANTS=ON, STRATA_ORCA_Q4KS_MMQ=OFF):
///   STRATA_GU_FMTS   src/kernels/cuda/iq_kernels.cu:611  (gate/up decode)
///   STRATA_D_FMTS    src/kernels/cuda/iq_kernels.cu:612  (down decode)
///   STRATA_MMVQ_FMTS src/kernels/cuda/iq_kernels.cu:613  (dense/native mmvq)
///   supported()      src/prefill/moe_mmq.cu:111          (prefill MMQ; Q5_0 needs ORCA flag, off)
///   is_iq            src/kernels/cuda/iq_kernels.cu:1789
///   embed_type_supported / native_mmvq_supported (iq_kernels.cu:1888 / native_mmvq.cu:1565)
///   native_expert_supported (iq_kernels.cu:1974); generate.cpp:2067 refuses at startup.
/// Update this mirror together with the engine, not independently.
/// </summary>
public static class StrataCompatibility
{
    public enum Grade { Native, Fallback, Unsupported }

    public sealed record Finding(string Role, uint TypeId, string TypeName, Grade Decode, Grade Prefill, int Layers);

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
    // dense mmvq: STRATA_MMVQ_FMTS
    private static readonly HashSet<uint> Mmvq = [16, 17, 18, 20, 21, 22, 23, 29, 42, 12, 13, 11, 7, 6, 8];
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
        if (md.ExpertCount is < 1 or > 512 || md.ExpertUsedCount < 1) structure.Add($"experts={md.ExpertCount}/used={md.ExpertUsedCount}");
        var structureOk = structure.Count == 0;

        // Per-layer gate/up/down formats; group identical (role, type) pairs into findings.
        var gateUp = new Dictionary<uint, List<int>>();   // type -> layers (gate/up must share one type)
        var down = new Dictionary<uint, List<int>>();
        var shared = new Dictionary<uint, List<int>>();
        for (var layer = 0; layer < 40; layer++)
        {
            uint? gate = RoleType(md, layer, "gate", "_exps");
            uint? up = RoleType(md, layer, "up", "_exps");
            uint? d = RoleType(md, layer, "down", "_exps");
            uint? sg = RoleType(md, layer, "gate", "_shexp");
            if (gate is { } g && up is { } u)
            {
                if (g != u)
                    blockers.Add($"blk.{layer} gate/up 格式不一致（{TypeName(g)} / {TypeName(u)}）；打包器要求同层同格式。");
                LayerList(gateUp, g).Add(layer);
            }
            else if (gate is { } gOnly) { LayerList(gateUp, gOnly).Add(layer); blockers.Add($"blk.{layer} 缺少 ffn_up_exps.weight。"); }
            else if (up is { } uOnly) { LayerList(gateUp, uOnly).Add(layer); blockers.Add($"blk.{layer} 缺少 ffn_gate_exps.weight。"); }
            else if (sg is null && RoleType(md, layer, "down", "_shexp") is null && md.IsMoE)
                notes.Add($"blk.{layer} 无专家张量（稠密层或命名异常）。");
            if (d is { } dd) LayerList(down, dd).Add(layer);
            if (sg is { } s) LayerList(shared, s).Add(layer);
        }

        // Routed experts (_exps): the engine refuses at startup when a role format is
        // outside its table (generate.cpp:2067 -> native_expert_supported) — there is no
        // decode fallback. Prefill may still fall back to fp16 dequant for a native
        // decode type missing from the MMQ table (e.g. Q5_0 with ORCA off).
        foreach (var (type, layers) in gateUp)
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
        foreach (var (type, layers) in down)
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
        // Shared experts (_shexp) are packed into dense.bin and run on the dense path,
        // not the routed-expert kernels (iq_pack.is_expert matches only "_exps"): report,
        // but do not block on the expert tables.
        foreach (var (type, layers) in shared)
        {
            var known = IsIq.Contains(type) || type is 0 or 1 or 2 or 30 or 42;
            findings.Add(new Finding("shared", type, TypeName(type), known ? Grade.Native : Grade.Fallback,
                known ? Grade.Native : Grade.Fallback, layers.Count));
            if (!known)
                notes.Add($"共享专家 {TypeName(type)}（层 {Span(layers)}）不在引擎已验证的稠密路径格式中，可能在加载时失败。");
        }

        // Embedding and LM head (embed_type_supported = is_iq ∪ {30}; native_mmvq for the head).
        foreach (var (name, role) in new[] { ("token_embd.weight", "embedding"), ("output.weight", "lm_head") })
        {
            if (!md.TensorFormats.TryGetValue(name, out var type)) continue;
            var supported = role == "embedding" ? IsIq.Contains(type) || type == 30 : Mmvq.Contains(type) || type is 30 or 0 or 1;
            if (!supported)
            {
                var human = role == "embedding" ? "嵌入矩阵" : "LM head";
                if (IsIq.Contains(type)) notes.Add($"{human}为 {TypeName(type)}：走非原生路径，可能偏慢。");
                else blockers.Add($"{human}为 {TypeName(type)}：引擎无该格式的 GPU kernel，启动时会拒绝。");
            }
        }

        if (md.HasMtp) notes.Add("含 MTP 头（blk.40）；MTP 专家格式须与主干一致。");
        return new Report(structureOk,
            structureOk ? $"qwen35moe / {md.LayerCount} 层 / {md.ExpertCount} 专家 top-{md.ExpertUsedCount}" : string.Join("；", structure),
            findings, notes, blockers);
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
        if (!report.StructureOk) return "结构不匹配：" + report.StructureNote;
        if (report.Blockers.Count > 0) return "不支持：" + report.Blockers[0];
        var sb = new StringBuilder("兼容：");
        foreach (var f in report.Findings)
            sb.Append(f.Role).Append(' ').Append(f.TypeName).Append('×').Append(f.Layers)
              .Append(f.Decode == Grade.Native ? "（原生）" : "（回退）").Append('；');
        if (report.Notes.Count > 0) sb.Append(' ').Append(report.Notes[0]);
        return sb.ToString().TrimEnd('；', ' ');
    }
}
