using System.Globalization;
using System.IO;
using System.Text.Json;
using LlamaManager.Engines;

namespace LlamaManager.Services;

/// <param name="SizeBytes">Total GGUF bytes, including every shard; null means unknown.</param>
/// <param name="ModelFingerprint">Caller-supplied content/sample signature, not merely the filename.</param>
public sealed record KvmemModelInfo(string ModelPath, string? Architecture, long? SizeBytes, string? ModelFingerprint = null);

public sealed record RecommendationScope
{
    public string HardwareFingerprint { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public string? ModelFingerprint { get; init; }
    public long? ModelSizeBytes { get; init; }
    public string? ModelArchitecture { get; init; }
    public string EngineId { get; init; } = "kvmem";
    public string EngineVersion { get; init; } = "";
    public string CachePath { get; init; } = "";
    public string PolicyVersion { get; init; } = "kvmem-hardware-v1";
    public bool Reusable { get; init; }
    public string Fingerprint => HardwareSnapshot.StableHash(JsonSerializer.Serialize(new
    {
        HardwareFingerprint, ModelPath, ModelFingerprint, ModelSizeBytes, ModelArchitecture,
        EngineId, EngineVersion, CachePath, PolicyVersion
    }));
}

public sealed record KvmemRecommendation
{
    public bool CanApply { get; init; }
    // Hardware/heuristics never become a measurement, even on the original reference machine.
    public bool IsMeasured => false;
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    public RecommendationScope Scope { get; init; } = new();
    public string Fingerprint => Scope.Fingerprint;
}

/// <summary>
/// Pure policy: no files, processes, probes, configuration writes or model reads.
/// Total hardware determines a starting point; transient occupancy is only a warning.
/// Produces only a conservative hardware starting point; never runs benchmarks or persists recommendations.
/// </summary>
public static class KvmemRecommendationService
{
    private const long Mib = 1L << 20;
    private const long Gib = 1L << 30;

    public static KvmemRecommendation Recommend(HardwareSnapshot snapshot, KvmemModelInfo model,
        string engineVersion, string cachePath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(model);
        var warnings = snapshot.Warnings.ToList();
        var reasons = new List<string>();
        var blockers = new List<string>();
        var normalizedCache = NormalizeAbsolute(cachePath);
        var normalizedModel = NormalizeAbsolute(model.ModelPath);
        var gpu = snapshot.Gpus.SingleOrDefaultSafe(g => g.Index == 0);
        var version = (engineVersion ?? "").Trim().TrimStart('v', 'V');
        var scope = new RecommendationScope
        {
            HardwareFingerprint = snapshot.Fingerprint, ModelPath = normalizedModel ?? model.ModelPath,
            ModelFingerprint = model.ModelFingerprint, ModelSizeBytes = model.SizeBytes, ModelArchitecture = model.Architecture,
            EngineVersion = version, CachePath = normalizedCache ?? cachePath,
            Reusable = normalizedModel != null && normalizedCache != null && !string.IsNullOrWhiteSpace(model.ModelFingerprint) &&
                !string.IsNullOrWhiteSpace(snapshot.CpuName) && snapshot.PhysicalCores is > 0 && snapshot.LogicalProcessors is > 0 &&
                snapshot.TotalRamBytes is > 0 && gpu?.TotalMemoryBytes is > 0 && gpu.ComputeCapability is > 0 &&
                snapshot.Gpus.All(g => !string.IsNullOrWhiteSpace(g.Uuid)) && version == "0.19.1"
        };
        warnings.Add("未实测：这是硬件容量推算的保守起点，不代表最优；不会启动性能测试或保存推荐缓存。");
        warnings.Add("硬件读取可在服务运行时进行；保存后下次启动生效，不把当前占用当作永久硬件能力。");
        warnings.Add("KVMem 是块稀疏注意力；KV 配额不是模型权重或整个进程的 RAM 上限，也不保证填满逻辑上下文时不会 OOM。");
        if (!scope.Reusable) warnings.Add("模型或硬件标识不完整，保守推算存在不确定性。");
        if (version != "0.19.1") blockers.Add("仅为独立 KVMem v0.19.1 制定推荐；引擎版本未知或不同，需重新验证。");
        if (!string.Equals(model.Architecture, "qwen35moe", StringComparison.Ordinal))
            blockers.Add("仅支持已确认架构为 qwen35moe 的 GGUF；不能按文件名猜测。");
        if (normalizedModel == null || !string.Equals(Path.GetExtension(model.ModelPath), ".gguf", StringComparison.OrdinalIgnoreCase))
            blockers.Add("需要绝对路径的 GGUF 模型信息（分片需传全部分片总大小）。");
        if (normalizedCache == null || normalizedCache.StartsWith("\\\\", StringComparison.Ordinal))
            blockers.Add("需要绝对路径的本机缓存目录；不自动选择网络盘。");
        if (snapshot.TotalRamBytes is not > 0) blockers.Add("总 RAM 未知，无法为模型和系统留空间；拒绝自动配置。");
        if (model.SizeBytes is not > 0) blockers.Add("模型总大小未知，无法估计主机内存；拒绝自动配置。");
        // Size alone cannot establish tensor geometry for much larger MoEs. Do not stretch 35B evidence indefinitely.
        if (model.SizeBytes > 32 * Gib) blockers.Add("模型超过此保守 35B 量级策略的 32 GiB GGUF 范围；需独立容量与兼容性验证。");
        if (gpu == null) blockers.Add("未能唯一确认 GPU 0；当前后端仅使用 GPU 0，不选择其他更大的 GPU。");
        if (snapshot.Gpus.Count > 1) warnings.Add("检测到多 GPU：仅按 GPU 0 推荐，不合并显存，也不自动改选 GPU 1。");
        if (gpu != null && (gpu.ComputeCapability == null || EngineCatalog.KvmemArchitecture(gpu.ComputeCapability.Value) == null))
            blockers.Add("GPU 0 精确计算能力未知或无对应上游包；不猜测架构。");
        if (gpu != null && (gpu.TotalMemoryBytes is not > 0 || gpu.TotalMemoryBytes < 4 * Gib))
            blockers.Add("GPU 0 总显存未知或小于 4 GiB，超出当前自动推荐范围。");

        var totalMib = Math.Max(0, snapshot.TotalRamBytes.GetValueOrDefault() / Mib);
        var modelMib = model.SizeBytes is > 0 ? 1 + (model.SizeBytes.Value - 1) / Mib : 0;
        var osReserve = Math.Max(4096, totalMib / 8);
        const long workspaceMib = 2048;
        var remaining = totalMib - modelMib - osReserve - workspaceMib;
        if (snapshot.TotalRamBytes is > 0 && model.SizeBytes is > 0 && remaining < 256)
            blockers.Add("总 RAM 不足：需先为完整 GGUF、系统安全余量及至少 2 GiB 工作区留空间，不能盲目分配 KV RAM。");
        if (snapshot.AvailableRamBytes is null) warnings.Add("当前空闲 RAM 未知；启动/测速前需重新检查。");
        else if (snapshot.AvailableRamBytes < Math.Min(snapshot.TotalRamBytes.GetValueOrDefault(), (modelMib + workspaceMib + 256) * Mib))
            warnings.Add("当前可用 RAM 偏低，可能有服务/其他程序占用；永久参数仍按总 RAM 计算，当前不宜直接实测。");
        if (gpu?.FreeMemoryBytes is null || gpu.UsedMemoryBytes is null) warnings.Add("当前 GPU 空闲/占用未知；实测前必须刷新检查。");
        else if (gpu.UsedMemoryBytes > 1024 * Mib || gpu.FreeMemoryBytes < gpu.TotalMemoryBytes - 1024 * Mib)
            warnings.Add("GPU 0 当前有明显显存占用，可能干扰测速；没有据此永久缩小推荐档。");

        var vramMib = gpu?.TotalMemoryBytes.GetValueOrDefault() / Mib ?? 0;
        var hostMib = vramMib <= 8192 ? 256 : (int)Math.Clamp(AlignDown(remaining / 4, 256), 256, 4096);
        var diskMib = DiskQuota(snapshot.CacheDisk, normalizedCache, warnings);
        if (diskMib == 0 && hostMib < 1024)
            blockers.Add("当前无法安全分配 SSD 历史缓存，且保守 RAM 历史配额小于 1 GiB；拒绝将此档自动用于长上下文。");
        if (blockers.Count > 0)
            return new KvmemRecommendation { Scope = scope, Warnings = warnings.Concat(blockers).ToArray(), Reasons = blockers };

        var physical = snapshot.PhysicalCores.GetValueOrDefault();
        var logical = snapshot.LogicalProcessors.GetValueOrDefault();
        var threads = physical > 0 ? Math.Clamp(physical, 1, logical > 0 ? Math.Min(logical, 256) : 256) : 1;
        if (physical <= 0) warnings.Add("物理核心数未知：线程保守设为 1，未验证；不把逻辑线程数当物理核心数。");
        if (logical > 0 && physical > logical) warnings.Add("核心计数不一致；线程数限制为逻辑处理器数。");
        var budget = vramMib < 6144 ? 4096 : vramMib >= 24576 ? 16384 : vramMib >= 12288 ? 12288 : 8192;
        var context = vramMib < 6144 ? 65536 : 131072;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kvmem"] = "1", ["context"] = Number(context), ["kvmemBudget"] = Number(budget),
            ["kvmemGenReserve"] = "512", ["kvmemHostMib"] = Number(hostMib),
            ["kvmemDiskMib"] = Number(diskMib), ["kvmemDiskPath"] = diskMib > 0 ? Path.GetFullPath(cachePath) : "",
            ["kvmemSessions"] = "2", ["kv"] = "q8_0", ["promptBatch"] = "128", ["stageMib"] = "128",
            ["threads"] = Number(threads), ["vramReserveMib"] = "512", ["tailSkip"] = "0", ["cacheBias"] = "0",
            ["tensorCoreMma"] = "0", ["polyFlashAttention"] = gpu!.ComputeCapability >= 8.6 ? "1" : "0"
        };
        reasons.Add($"GPU 0 总显存 {vramMib} MiB；仅单卡，B={budget}、R=512，均 32 对齐。");
        reasons.Add($"CPU 按物理核心设置保守起点（{threads} 线程），不宣称最优线程数。");
        reasons.Add($"总 RAM {totalMib} MiB，先预留模型 {modelMib} + 系统 {osReserve} + 工作区 {workspaceMib} MiB，再给 KV Host {hostMib} MiB；不盲分 16 GiB。");
        reasons.Add($"SSD 配额 {diskMib} MiB，根据所选缓存盘空闲减安全余量后向下对齐；不创建目录，应用/启动前应刷新盘空闲。");
        if (vramMib is >= 6144 and < 8192)
            reasons.Add("6 GiB 档参考 Ryzen 7 5800H / 23.4 GiB RAM / RTX 3060 Laptop sm86 / 约13.7 GiB Cyber-Tiel-Coder-35B-A3B 的局部证据：33k/65k prompt + SSD 通过，峰值4751 MiB；不是其他模型/机器的已通过结果，也未声称实测填满131072。");
        else warnings.Add("非 6 GiB 参考档，仅作保守扩展；更大显存不保证更快或兼容所有 qwen35moe 模型。");
        if (model.SizeBytes < 12 * Gib || model.SizeBytes > 18 * Gib)
            warnings.Add("模型大小偏离约13.7 GiB参考模型；仅字节数不足以确认层数/工作区，需独立试跑。");
        return new KvmemRecommendation { CanApply = true, Scope = scope, Parameters = parameters, Warnings = warnings, Reasons = reasons };
    }

    private static int DiskQuota(DiskHardwareInfo? disk, string? cachePath, ICollection<string> warnings)
    {
        if (disk == null || cachePath == null || NormalizeAbsolute(disk.CachePath) != cachePath || disk.FreeBytes is null or < 0)
        { warnings.Add("目标缓存路径的空闲容量未知/不匹配；不会借用其他盘容量，SSD 配额设 0。"); return 0; }
        if (disk.TotalBytes is > 0 && disk.FreeBytes > disk.TotalBytes)
        { warnings.Add("缓存盘空闲大于总容量，数据不可信；SSD 配额设 0。"); return 0; }
        if (string.IsNullOrWhiteSpace(disk.MediaType) || disk.MediaType.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            warnings.Add("缓存盘介质未确认是 SSD/NVMe；磁盘容量不等于分页速度保证。");
        else if (!disk.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) && !disk.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            warnings.Add("缓存盘可能为机械盘/其他介质；建议选择 SSD，当前分页性能未验证。");
        var freeMib = disk.FreeBytes.Value / Mib;
        var margin = Math.Max(2048, freeMib / 10);
        var quota = (int)Math.Clamp(AlignDown(freeMib - margin, 256), 0, 8192);
        if (quota < 8192) warnings.Add($"缓存盘空闲受限，扣除至少 2 GiB / 10% 余量后仅推荐 {quota} MiB；长历史容量受限。");
        return quota;
    }

    private static long AlignDown(long value, int alignment) => value <= 0 ? 0 : value / alignment * alignment;
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string? NormalizeAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToLowerInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static T? SingleOrDefaultSafe<T>(this IEnumerable<T> source, Func<T, bool> predicate) where T : class
    {
        var matches = source.Where(predicate).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
