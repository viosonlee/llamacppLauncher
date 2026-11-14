using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using LlamaManager.Engines;

namespace LlamaManager.Services;

/// <summary>Opt-in new parameters require the selected executable's actual capabilities, not a folder/version guess.</summary>
internal static class LlamaCppFeatureGate
{
    private static readonly FeatureHelpCache Cache = new(async executable =>
    {
        // Modern --help is already >60 KiB; version probes' smaller limits are not suitable.
        // Hide CUDA devices: reading options must never load the selected model.
        try
        {
            return await ProcessProbe.RunAsync(BuiltInInstallation.CreateProbeStart(executable, "--help"),
                TimeSpan.FromSeconds(5), CancellationToken.None, maxOutputCharacters: 256 * 1024).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) { throw new TimeoutException("所选引擎的参数能力探测超过5秒，未启动模型。", ex); }
    });

    public static async Task EnsureAsync(string executable, string arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parsed = new LauncherArguments(arguments);
        if (!RequiresProbe(parsed)) return;
        ValidateRanges(parsed);
        var file = new FileInfo(executable);
        if (!file.Exists) throw new FileNotFoundException("未找到待验证参数能力的引擎。", executable);
        var key = PathIdentity.Normalize(executable) + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        var help = await Cache.GetAsync(key, executable, ct).ConfigureAwait(false);
        ValidateCapabilities(arguments, help);
    }

    private static bool RequiresProbe(LauncherArguments args) =>
        args.Has("--moe-cache-mib") || args.Has("--moe-cache-cpu-share") ||
        args.Get("-ctk") == "f8_e4m3" || args.Get("-ctv") == "f8_e4m3";

    internal static void ValidateCapabilities(string arguments, string help)
    {
        var args = new LauncherArguments(arguments);
        ValidateRanges(args);
        foreach (var flag in new[] { "--moe-cache-mib", "--moe-cache-cpu-share" })
            if (args.Has(flag) && !Regex.IsMatch(help, $@"(?<![\w-]){Regex.Escape(flag)}(?![\w-])"))
                throw new NotSupportedException($"所选引擎未报告支持 {flag}；请更新该引擎或移除此选项，不能沿用其它引擎的能力。");
        if ((args.Get("-ctk") == "f8_e4m3" || args.Get("-ctv") == "f8_e4m3") &&
            !Regex.IsMatch(help, @"\bf8_e4m3\b"))
            throw new NotSupportedException("所选引擎未报告支持 f8_e4m3 KV；请保留 q8_0/f16 或更新完整引擎包。");
    }

    private static void ValidateRanges(LauncherArguments args)
    {
        if (args.Has("--moe-cache-mib"))
        {
            if (!int.TryParse(args.Get("--moe-cache-mib"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) || mib < 0)
                throw new ArgumentException("MoE GPU专家缓存必须为非负整数MiB，0表示关闭。");
            if (mib > 0 && args.Get("--split-mode") == "tensor")
                throw new ArgumentException("MoE专家缓存不能与 tensor split 模式组合。");
        }
        if (args.Has("--moe-cache-cpu-share") &&
            (!double.TryParse(args.Get("--moe-cache-cpu-share"), NumberStyles.Float, CultureInfo.InvariantCulture, out var share) ||
             !double.IsFinite(share) || share < 0 || share > 1))
            throw new ArgumentException("MoE缓存未命中的CPU计算比例必须为0–1的有限小数。");
    }
}

/// <summary>Bounded executable-version keyed help cache. One cancelled waiter cannot cancel another's probe.</summary>
internal sealed class FeatureHelpCache(Func<string, Task<string>> probe)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Lazy<Task<string>>> _entries = new(StringComparer.OrdinalIgnoreCase);
    public async Task<string> GetAsync(string key, string executable, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Lazy<Task<string>> entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!) ||
                (entry.IsValueCreated && (entry.Value.IsFaulted || entry.Value.IsCanceled)))
            {
                if (_entries.Count >= 32) _entries.Remove(_entries.Keys.First());
                _entries[key] = entry = new Lazy<Task<string>>(() => Task.Run(() => probe(executable)));
            }
        }
        var task = entry.Value;
        try { return await task.WaitAsync(ct).ConfigureAwait(false); }
        catch
        {
            if (task.IsFaulted || task.IsCanceled)
                lock (_gate)
                    if (_entries.GetValueOrDefault(key) == entry) _entries.Remove(key);
            throw;
        }
    }
}
