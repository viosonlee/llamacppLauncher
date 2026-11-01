using System.Collections.Concurrent;
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
    private sealed class Entry
    {
        public Lazy<Task<CudaInfo>> Probe { get; }
        private DateTime _completedUtc = DateTime.MinValue;

        public Entry(string engine)
        {
            Probe = new Lazy<Task<CudaInfo>>(async () =>
            {
                var info = await ProbeAsync(engine).ConfigureAwait(false);
                // Age is measured from completion: a probe that takes longer than the TTL must not
                // look stale the moment it lands and immediately trigger a second one.
                _completedUtc = DateTime.UtcNow;
                return info;
            });
        }

        /// <summary>An unfinished probe is shared; a finished one is reused until the TTL lapses.</summary>
        public bool IsFresh(DateTime now, TimeSpan ttl)
        {
            if (!Probe.IsValueCreated || !Probe.Value.IsCompleted) return true;
            return now - _completedUtc < ttl;
        }
    }

    private static readonly ConcurrentDictionary<string, Entry> Cache = new();

    /// <summary>
    /// How long a device probe stays valid. VRAM and driver state change while the launcher runs,
    /// so results are refreshed on the next poll after this window instead of only on an explicit
    /// refresh. Callers can shorten it (tests do) or clear it with <see cref="Invalidate"/>.
    /// </summary>
    public static TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(30);

    public static void Invalidate() => Cache.Clear();

    public static Task<CudaInfo> DetectAsync(string? modelPath = null)
    {
        var engine = ConfigService.GetLlamaServerPath(modelPath);
        var file = new FileInfo(engine);
        var key = PathIdentity.Normalize(engine) + "|" + (file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
        var existing = Cache.GetOrAdd(key, _ => new Entry(engine));
        if (existing.IsFresh(DateTime.UtcNow, CacheTtl)) return existing.Probe.Value;
        // Expired: swap in a fresh entry. If another caller refreshed first, use theirs so the
        // engine is still only probed once per period.
        var fresh = new Entry(engine);
        var chosen = Cache.TryUpdate(key, fresh, existing) ? fresh : Cache.GetOrAdd(key, fresh);
        return chosen.Probe.Value;
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
        return new CudaInfo
        {
            Available = Regex.IsMatch(text, @"CUDA\d+:"),
            Text = string.IsNullOrWhiteSpace(text) ? "无 CUDA 设备信息。" : text.Trim(),
            TotalVramMb = match.Success ? ToMiB(match.Groups["total"].Value) : 0,
            FreeVramMb = match.Success ? ToMiB(match.Groups["free"].Value) : 0
        };
    }

    /// <summary>
    /// Engine-report MiB to int. A bad driver line can print more digits than fit; that used to
    /// throw OverflowException and turn a readable device list into "检测失败", so clamp instead.
    /// </summary>
    private static int ToMiB(string value) =>
        long.TryParse(value, out var mib) ? (int)Math.Clamp(mib, 0L, int.MaxValue) : 0;
}
