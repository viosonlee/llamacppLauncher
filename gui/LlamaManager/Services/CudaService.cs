using System.IO;
using System.Text.RegularExpressions;

namespace LlamaManager.Services;

public sealed record CudaInfo
{
    public bool Available { get; init; }
    public string Text { get; init; } = "";
    public int TotalVramMb { get; init; }
    public int FreeVramMb { get; init; }
    public bool SupportsFit { get; init; }
    public bool SupportsFlash { get; init; }
    public DateTime DetectedAtUtc { get; init; } = DateTime.UtcNow;
}

public static class CudaService
{
    // A five-minute lifetime avoids spawning two probes on every two-second status poll, but
    // also lets GPU/driver changes recover without replacing the executable or manual refresh.
    private static readonly CudaProbeCache Cache = new(ProbeAsync, () => DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    public static void Invalidate() => Cache.Clear();

    public static Task<CudaInfo> DetectAsync(string? modelPath = null)
    {
        var engine = ConfigService.GetLlamaServerPath(modelPath);
        var file = new FileInfo(engine);
        var key = PathIdentity.Normalize(engine) + "|" + (file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
        return Cache.GetAsync(key, engine);
    }

    // Used only in the background preset builder and command-line path.
    public static CudaInfo Detect(string? modelPath = null) => DetectAsync(modelPath).GetAwaiter().GetResult();

    private static async Task<CudaInfo> ProbeAsync(string engine)
    {
        try
        {
            var output = await ProcessProbe.RunAsync(engine, "--list-devices", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var help = await ProcessProbe.RunAsync(engine, "--help", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return Parse(output) with { SupportsFit = help.Contains("--fit "), SupportsFlash = help.Contains("--flash-attn") };
        }
        catch (Exception ex) { return new CudaInfo { Text = "检测失败：" + ex.Message }; }
    }

    public static CudaInfo Parse(string text)
    {
        var match = Regex.Match(text, @"CUDA\d+:\s*[^\r\n(]+\((?<total>\d+)\s*MiB,\s*(?<free>\d+)\s*MiB\s+free\)", RegexOptions.IgnoreCase);
        // The device list comes from an external process: a huge or malformed number must not
        // throw out of a probe. Unknown stays 0, which callers already treat as "not reported".
        return new CudaInfo
        {
            Available = Regex.IsMatch(text, @"CUDA\d+:"),
            Text = string.IsNullOrWhiteSpace(text) ? "无 CUDA 设备信息。" : text.Trim(),
            TotalVramMb = MiB(match, "total"),
            FreeVramMb = MiB(match, "free")
        };
    }

    private static int MiB(Match match, string group) =>
        match.Success && int.TryParse(match.Groups[group].Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 0;
}

/// <summary>Shared, expiring observations. Clock/probe injection keeps expiry tests deterministic.</summary>
internal sealed class CudaProbeCache(Func<string, Task<CudaInfo>> probe, Func<DateTimeOffset> utcNow, TimeSpan ttl)
{
    private sealed record Entry(DateTimeOffset At, Lazy<Task<CudaInfo>> Probe);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public Task<CudaInfo> GetAsync(string key, string engine)
    {
        Entry selected;
        lock (_gate)
        {
            var now = utcNow();
            if (_entries.TryGetValue(key, out var cached) &&
                (!cached.Probe.IsValueCreated || !cached.Probe.Value.IsCompleted || now - cached.At < ttl))
                selected = cached; // An in-flight probe must remain shared even after the TTL.
            else
            {
                selected = new Entry(now, new Lazy<Task<CudaInfo>>(() => Task.Run(() => probe(engine))));
                _entries[key] = selected;
            }
        }
        // The first subprocess startup must happen on a worker, never on the UI or under _gate.
        return selected.Probe.Value;
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }
}
