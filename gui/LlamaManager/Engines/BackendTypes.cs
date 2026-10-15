using System.IO;

namespace LlamaManager.Engines;

/// <summary>Every engine is reachable as a local HTTP service (OpenAI/Anthropic compatible).</summary>
public sealed record BackendDescriptor(
    string Id,
    string DisplayName,
    string Description,
    bool BuiltIn,
    bool GpuHeavy);

/// <summary>Result of probing a backend's executable and version.</summary>
public sealed record BackendStatus(
    bool Available,
    string? ExecutablePath,
    string? Version,
    string? Problem)
{
    public static BackendStatus Unavailable(string problem) => new(false, null, null, problem);
}

/// <summary>Coarse resource estimate for a model on a backend; a warning, never a hard block.</summary>
public sealed record ResourceEstimate(long VramBytes, long HostRamBytes, string Note);

/// <summary>Backend-specific verdict on whether a GGUF can run, and why not.</summary>
public sealed record CompatibilityReport(
    bool Runnable,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Notes,
    string Summary,
    ResourceEstimate? Estimate)
{
    public static CompatibilityReport Unknown(string summary) =>
        new(true, [], [], summary, null);
}

/// <summary>Coarse model-size estimate used for a 6GB-card warning; never a hard block.</summary>
public static class ResourceEstimator
{
    /// <summary>Estimates VRAM/RAM from the GGUF size and layer count. Deliberately conservative.</summary>
    public static ResourceEstimate ForGguf(string modelPath, int layerCount)
    {
        var size = new FileInfo(modelPath).Length;
        // Dense weights + KV cache + working buffers roughly a quarter of the file on a q36 hybrid;
        // host RAM holds a copy of every expert, so it tracks the file size.
        var vram = Math.Max(size / 4, 4L * 1024 * 1024 * 1024);
        var note = $"模型 {size / (1024.0 * 1024 * 1024):F1} GiB / {layerCount} 层；" +
            "估算仅供参考，6GB 卡上专家会在 CPU 计算，速度受内存带宽限制。";
        return new ResourceEstimate(vram, size, note);
    }
}

/// <summary>Live status of a running (or last) session.</summary>
public sealed record EngineStatus(
    bool Running,
    bool Ready,
    int? Port,
    string? Summary);
