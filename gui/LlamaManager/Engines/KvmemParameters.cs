using System.Globalization;
using System.IO;
using LlamaManager.Models;

namespace LlamaManager.Engines;

/// <summary>The v0.19.1 PolyStrata contract, not flags accepted by stock llama.cpp.</summary>
public static class KvmemParameters
{
    private const string Group = "超长上下文 (KVMem)";
    public static IReadOnlyList<ParameterSpec> Specs { get; } =
    [
        new("kvmem", "启用 KVMem 分层 KV", Group, ParamType.Boolean, "1",
            "仅此 KVMem 后端支持。固定活跃 KV 槽位，历史存放 RAM/SSD；使用块稀疏注意力，非全量稠密注意力。",
            Help: "【KVMem 分层 KV】\n• 独立的 KVMem PolyStrata 引擎，不是给原版 llama.cpp 加开关。\n• 当前仅支持 qwen35moe GGUF、单张 NVIDIA GPU、fp16/q8_0 KV。\n• Budget + Reserve 固定活跃 KV 容量；历史可放 RAM/SSD，但模型权重、工作区及配额仍可能耗尽。\n• 不保证杜绝 OOM，也不保证稀疏检索与全量注意力答案一致。\n• 关闭后不传任何 --kvmem-*，仍使用此包的普通推理。"),
        new("kvmemBudget", "显存活跃预算 B", Group, ParamType.Integer, "8192",
            "活跃槽位数，须为 32 的倍数；B + R 不得超过上下文。6GB 从 8192 起试。", 2048, 131072,
            VisibleWhen: "param.kvmem", Unit: "tokens", RangeHint: "2048–131072，32 对齐", MultipleOf: 32, Advanced: true),
        new("kvmemGenReserve", "追加工作区 R", Group, ParamType.Integer, "512",
            "追加/交换工作空间，须为 32 的倍数；不是总输出 token 硬上限。", 32, 16384,
            VisibleWhen: "param.kvmem", Unit: "tokens", RangeHint: "32–16384，32 对齐", MultipleOf: 32, Advanced: true),
        new("kvmemHostMib", "KV 系统内存配额", Group, ParamType.Integer, "1024",
            "仅 KV 历史配额，不含模型权重和系统占用；本机 24GB RAM 不应默认再分配 16GB。", 1, 131072,
            VisibleWhen: "param.kvmem", Unit: "MiB", RangeHint: "1–131072；不足时需 SSD 或减少历史", Advanced: true),
        new("kvmemDiskMib", "SSD 冷缓存上限", Group, ParamType.Integer, "0",
            "0 禁用磁盘；正数必须同时指定目录。缓存为进程内临时状态，不跨重启保留。", 0, 524288,
            VisibleWhen: "param.kvmem", Unit: "MiB"),
        new("kvmemDiskPath", "SSD 存储目录", Group, ParamType.FolderPath, "",
            "NVMe SSD 的独立缓存目录；不得用模型目录。只有磁盘配额大于 0 时传给引擎。",
            VisibleWhen: "param.kvmem && param.kvmemDiskMib > 0"),
        new("kvmemSessions", "保留历史数量", Group, ParamType.Integer, "2",
            "包含当前 GPU 会话；自动开启 history-reuse。不是同时在 GPU 上运行的并发槽位。", 1, 16,
            VisibleWhen: "param.kvmem", RangeHint: "1–16；更多历史需要更多 RAM/SSD", Advanced: true),
        new("promptBatch", "预填充分块", Group, ParamType.Integer, "128",
            "--prompt-batch，必须 <= R；6GB 建议 128，减少预填充工作区峰值。", 1, 4096,
            Unit: "tokens", Advanced: true),
        new("stageMib", "专家预填充搬运区", Group, ParamType.Integer, "128",
            "--stage-mib，6GB 建议从 128 MiB 起试；更大可能提速但增加显存峰值。", 1, 8192,
            Unit: "MiB", Advanced: true),
    ];

    public static bool Enabled(ManagerConfig config, string backendId) =>
        Value(config, backendId, "kvmem", "1") is "1" or "true" or "True";

    /// <summary>
    /// Returns a copy with explicit integer-multiple adjustments for GUI persistence or CLI logs.
    /// Missing/empty values retain their defaults; disabled KVMem leaves even stale invalid values
    /// untouched. This only normalizes multiples: Append still validates other fields and relations.
    /// </summary>
    public static ParameterValuesNormalizationResult NormalizeValues(IReadOnlyDictionary<string, string>? values)
    {
        var copy = values is Dictionary<string, string> dictionary
            ? new Dictionary<string, string>(dictionary, dictionary.Comparer)
            : values is null ? new Dictionary<string, string>() : new Dictionary<string, string>(values);
        var adjustments = new List<ParameterAdjustment>();
        var errors = new List<ParameterNormalizationError>();
        var enabled = copy.GetValueOrDefault("kvmem") is { Length: > 0 } setting ? setting : "1";
        if (enabled is not ("1" or "true" or "True")) return new(copy, adjustments, errors);
        foreach (var spec in Specs.Where(spec => spec.MultipleOf.HasValue))
        {
            if (!copy.TryGetValue(spec.Key, out var value)) continue;
            var result = ParameterNormalizer.Normalize(spec, value);
            if (result.Error is { } error) errors.Add(new(spec.Key, error));
            else if (result.Changed)
            {
                copy[spec.Key] = result.NormalizedValue!;
                adjustments.Add(new(spec.Key, value, result.NormalizedValue!, result.Message!));
            }
        }
        return new(copy, adjustments, errors);
    }

    private static string Value(ManagerConfig config, string id, string key, string fallback) =>
        config.Engines.GetValueOrDefault(id)?.Parameters.GetValueOrDefault(key) is { Length: > 0 } value
            ? value : fallback;

    private static int Number(ManagerConfig config, string id, string key, int fallback, int min, int max) =>
        Number(config.Engines.GetValueOrDefault(id)?.Parameters, key, fallback, min, max);

    private static int Number(IReadOnlyDictionary<string, string>? values, string key, int fallback, int min, int max)
    {
        var value = values?.GetValueOrDefault(key) is { Length: > 0 } setting
            ? setting : fallback.ToString(CultureInfo.InvariantCulture);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
            throw new ArgumentException($"KVMem {key} 必须为 {min}–{max} 的整数。");
        return number;
    }

    public static void Append(ManagerConfig config, string id, int context, List<string> args)
    {
        void Add(string flag, int number) { args.Add(flag); args.Add(number.ToString(CultureInfo.InvariantCulture)); }
        var prefill = Number(config, id, "promptBatch", 128, 1, 4096);
        Add("--prompt-batch", prefill);
        Add("--stage-mib", Number(config, id, "stageMib", 128, 1, 8192));
        if (!Enabled(config, id)) return;
        var normalized = NormalizeValues(config.Engines.GetValueOrDefault(id)?.Parameters);
        if (!normalized.IsValid)
            throw new ArgumentException(string.Join(Environment.NewLine, normalized.Errors.Select(error => error.Error)));
        var budget = Number(normalized.Values, "kvmemBudget", 8192, 2048, 131072);
        var reserve = Number(normalized.Values, "kvmemGenReserve", 512, 32, 16384);
        if ((long)budget + reserve > context)
            throw new ArgumentException("KVMem 上下文必须 >= Budget + Reserve。");
        if (prefill > reserve)
            throw new ArgumentException("KVMem 预填充分块必须 <= Reserve。");
        var kv = Value(config, id, "kv", "q8_0");
        if (kv is not ("q8_0" or "fp16"))
            throw new ArgumentException("KVMem PolyStrata 仅支持 fp16 或 q8_0 KV。");
        var disk = Number(config, id, "kvmemDiskMib", 0, 0, 524288);
        var path = Value(config, id, "kvmemDiskPath", "");
        if (disk > 0 && string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("KVMem SSD 配额大于 0 时必须指定 SSD 存储目录。");
        if (!string.IsNullOrWhiteSpace(Value(config, id, "draftModel", "")))
            throw new ArgumentException("KVMem 不支持外部草稿模型；清空 draftModel，先使用普通解码验证。");
        if (!string.IsNullOrWhiteSpace(Value(config, id, "conversationCache", "")) &&
            Value(config, id, "conversationCache", "") != "0")
            throw new ArgumentException("KVMem 使用自己的历史池，不能同时启用普通对话驻留缓存。");
        // Authoritative wrapper: tools/polystrata/server.py, v0.19.1. No bare --kvmem exists.
        Add("--window", 1); args.Add("--no-draft"); Add("--lookup", 0);
        Add("--prompt-cache", 0); Add("--conversation-cache-mib", 0); Add("--conversation-cache-slots", 0);
        Add("--kvmem-budget", budget); Add("--kvmem-gen-reserve", reserve);
        Add("--kvmem-block-tokens", 32);
        Add("--kvmem-host-mib", Number(config, id, "kvmemHostMib", 1024, 1, 131072));
        Add("--kvmem-sink-tokens", 128); Add("--kvmem-recent-tokens", 1024);
        args.AddRange(["--kvmem-query-scorer", "page-softmax", "--kvmem-score-agg", "mean",
            "--kvmem-graph-alpha", "0.2", "--no-kvmem-lrfu"]);
        Add("--kvmem-query-max-tokens", 1024); Add("--kvmem-query-replay-max-tokens", 8192);
        args.Add("--kvmem-history-reuse");
        Add("--kvmem-sessions", Number(config, id, "kvmemSessions", 2, 1, 16));
        if (disk > 0) { Add("--kvmem-disk-mib", disk); args.Add("--kvmem-disk-path"); args.Add(Path.GetFullPath(path)); }
    }
}
