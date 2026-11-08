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
    private static readonly ConcurrentDictionary<string, Lazy<Task<CudaInfo>>> Cache = new();

    public static void Invalidate() => Cache.Clear();

    public static Task<CudaInfo> DetectAsync(string? modelPath = null)
    {
        var engine = ConfigService.GetLlamaServerPath(modelPath);
        var file = new FileInfo(engine);
        var key = PathIdentity.Normalize(engine) + "|" + (file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
        return Cache.GetOrAdd(key, _ => new Lazy<Task<CudaInfo>>(() => ProbeAsync(engine))).Value;
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
