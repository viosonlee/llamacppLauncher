using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace LlamaManager.Services;

/// <summary>One A/B variant's result: a label, the args used, and the measured rates.</summary>
public sealed record BenchmarkVariant(string Label, string Args, double?[] DecodeTps, double?[] TtftSeconds)
{
    /// <summary>Engine-reported prompt (prefill) rate per run; empty when the engine reported none.</summary>
    public double?[] PrefillTps { get; init; } = [];
    /// <summary>Speculative drafts offered/accepted across all runs of this variant.</summary>
    public int DraftOffered { get; init; }
    public int DraftAccepted { get; init; }
    /// <summary>Peak VRAM in MiB sampled while the variant ran; 0 when unreadable.</summary>
    public int PeakVramMb { get; init; }

    /// <summary>Mean engine-reported prefill rate, or null when the engine never reported one.</summary>
    public double? MeanPrefillTps
    {
        get
        {
            var values = PrefillTps.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            return values.Length == 0 ? null : values.Average();
        }
    }

    /// <summary>Share of the drafts the engine accepted, or null when it drafted none.</summary>
    public double? DraftAcceptance => DraftOffered > 0 ? (double)DraftAccepted / DraftOffered : null;

    /// <summary>Mean of the successful runs, or null when every run failed.</summary>
    public double? MeanDecodeTps
    {
        get
        {
            var values = DecodeTps.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            return values.Length == 0 ? null : values.Average();
        }
    }

    /// <summary>The slowest successful run, which is what a user will actually feel.</summary>
    public double? WorstDecodeTps
    {
        get
        {
            var values = DecodeTps.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            return values.Length == 0 ? null : values.Min();
        }
    }

    public double? MeanTtft
    {
        get
        {
            var values = TtftSeconds.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            return values.Length == 0 ? null : values.Average();
        }
    }

    public int SuccessfulRuns => DecodeTps.Count(v => v.HasValue);
}

/// <summary>Outcome of the whole sweep: variants in their input order plus a skip reason per failure.</summary>
public sealed record BenchmarkReport(
    IReadOnlyList<BenchmarkVariant> Variants,
    IReadOnlyList<string> Errors,
    string VramNote)
{
    /// <summary>The fastest variant label, or null when nothing measured.</summary>
    public string? FastestLabel => Variants.Where(v => v.MeanDecodeTps.HasValue)
        .OrderByDescending(v => v.MeanDecodeTps).FirstOrDefault()?.Label;
}

/// <summary>
/// Runs the same model with different llama.cpp arguments and measures each variant with the
/// same fixed prompt, so the numbers are comparable. Every variant gets its own process on its
/// own port and its own log files, and the user's running service is never touched: the sweep
/// refuses to start while a managed or foreign server holds the test port.
/// </summary>
public static class ParameterBenchmarkService
{
    /// <summary>The port range the sweep uses; deliberately outside the configured service ports.</summary>
    public const int BasePort = 8095;

    /// <summary>Parses the sweep plan out of a raw multi-line benchmark description.</summary>
    public static IReadOnlyList<(string Label, string Args)> ParsePlan(string? description)
    {
        var plan = new List<(string, string)>();
        foreach (var raw in (description ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var sep = line.IndexOf('|');
            var label = sep < 0 ? $"变体 {plan.Count + 1}" : line[..sep].Trim();
            var args = (sep < 0 ? line : line[(sep + 1)..]).Trim();
            if (args.Length == 0) continue;
            if (new LauncherArguments(args).GetPort(0) > 0)
                throw new ArgumentException("变体参数里不能带端口，端口由实测器统一分配。");
            plan.Add((label, args));
        }
        return plan;
    }

    /// <summary>
    /// Runs every variant with the same prompt, <paramref name="runs"/> times each, and returns
    /// the measured rates. A variant that fails to load is skipped with a reason; the sweep
    /// never leaves a process behind, even when cancelled mid-way.
    /// </summary>
    public static async Task<BenchmarkReport> RunAsync(string modelPath, string baseArgs,
        IReadOnlyList<(string Label, string Args)> plan, int runs, Action<string> log,
        CancellationToken ct, Func<CancellationToken, Task<int>>? usedMemoryReader = null)
    {
        if (runs < 1) throw new ArgumentOutOfRangeException(nameof(runs));
        if (plan.Count == 0) throw new ArgumentException("实测计划为空。");
        var engine = ConfigService.GetLlamaServerPath(modelPath);
        if (!File.Exists(engine)) throw new FileNotFoundException("未找到 llama-server.exe。", engine);

        // A foreign listener on our ports would poison the measurements or fail to bind.
        for (var i = 0; i < plan.Count; i++)
            if (ServerService.TestPortInUse(BasePort + i))
                throw new InvalidOperationException($"端口 {BasePort + i} 已被占用，实测需要独占它。");

        // VRAM shared with another engine changes every number: record it so a result set that
        // was measured next to a busy GPU is not mistaken for a clean one.
        var device = await CudaService.DetectAsync(modelPath).ConfigureAwait(false);
        var vramNote = device.Available && device.FreeVramMb > 0
            ? $"开始时显存空闲 {device.FreeVramMb:N0} / {device.TotalVramMb:N0} MiB" +
              (device.FreeVramMb < device.TotalVramMb * 0.6 ? "（有其它程序占用显存，数字会偏低且波动）" : "")
            : "未检测到 CUDA 设备";
        log(vramNote);

        var measured = new List<BenchmarkVariant>();
        var errors = new List<string>();
        for (var index = 0; index < plan.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (label, args) = plan[index];
            var port = BasePort + index;
            var parsed = new LauncherArguments(args);
            var fullArgs = parsed.Set("-m", modelPath);
            fullArgs = new LauncherArguments(fullArgs).Set("--port", port.ToString());
            fullArgs = new LauncherArguments(fullArgs).Set("--host", "127.0.0.1");
            var stdout = Path.Combine(ConfigService.GetManagerRoot(), "logs", $"benchmark-{index}.stdout.log");
            var stderr = Path.Combine(ConfigService.GetManagerRoot(), "logs", $"benchmark-{index}.stderr.log");
            var decode = new double?[runs];
            var ttft = new double?[runs];
            var prefill = new double?[runs];
            var draftOffered = 0;
            var draftAccepted = 0;
            var peakVram = 0;
            log($"[{label}] 启动：{fullArgs}");
            Process? process = null;
            using var sampleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<int>? sampler = null;
            try
            {
                process = DetachedProcess.Start(engine, fullArgs, stdout, stderr);
                await WaitReadyAsync(process, index, port, label, log, ct).ConfigureAwait(false);
                // Sample VRAM in the background while the variant answers, so the report shows how
                // close the configuration sits to the card's limit (MoE offload tuning lives there).
                sampler = SamplePeakAsync(usedMemoryReader ?? GpuMemoryService.ReadUsedMibAsync, sampleCts.Token);
                for (var run = 0; run < runs; run++)
                {
                    ct.ThrowIfCancellationRequested();
                    // One warm-up request per process is implied by the first measured run of a
                    // fresh load; per-run variance comes from the repeats themselves.
                    var result = await SpeedTestService.RunAsync(port, ct).ConfigureAwait(false);
                    if (!result.Success) throw new InvalidOperationException(result.Error ?? "测速失败");
                    decode[run] = result.DecodeTps;
                    ttft[run] = result.TtftSeconds;
                    prefill[run] = result.PrefillTps;
                    draftOffered += result.DraftOffered;
                    draftAccepted += result.DraftAccepted;
                    log($"[{label}] 第 {run + 1}/{runs} 次：{result.DecodeTps:F2} tok/s · TTFT {result.TtftSeconds:F3} s{TelemetrySuffix(result)}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{label}: {ex.Message}");
                log($"[{label}] 失败，跳过：{ex.Message}");
            }
            finally
            {
                sampleCts.Cancel();
                if (sampler != null) peakVram = await sampler.ConfigureAwait(false);
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    process.Dispose();
                }
            }
            measured.Add(new BenchmarkVariant(label, args, decode, ttft)
            {
                PrefillTps = prefill, DraftOffered = draftOffered, DraftAccepted = draftAccepted, PeakVramMb = peakVram
            });
        }
        return new BenchmarkReport(measured, errors, vramNote);
    }

    /// <summary>Polls the GPU's used VRAM until cancelled and returns the highest value seen.</summary>
    private static async Task<int> SamplePeakAsync(Func<CancellationToken, Task<int>> read, CancellationToken ct)
    {
        var peak = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var used = await read(ct).ConfigureAwait(false);
                if (used > peak) peak = used;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        return peak;
    }

    /// <summary>One line's worth of process telemetry; empty when the engine reported none.</summary>
    private static string TelemetrySuffix(SpeedTestService.Result result)
    {
        var parts = new List<string>();
        if (result.PrefillTps is { } prefill) parts.Add($"预填 {prefill:F0} tok/s");
        if (result.DraftAcceptance is { } acceptance) parts.Add($"草稿 {acceptance:P0}");
        return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
    }

    private static async Task WaitReadyAsync(Process process, int index, int port, string label,
        Action<string> log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var tail = LogService.ReadFileTail(
                    Path.Combine(ConfigService.GetManagerRoot(), "logs", $"benchmark-{index}.stderr.log"), 8);
                throw new InvalidOperationException($"变体进程提前退出（{process.ExitCode}）。{string.Join("\n", tail)}");
            }
            if (await ServerService.TestApiReadyAsync(port, ct).ConfigureAwait(false)) return;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"{label}：120 秒内模型未加载完成。");
    }

    /// <summary>One comparable line for the log page; the sweep writes one per variant.</summary>
    public static string Describe(BenchmarkReport report)
    {
        var lines = new List<string>
        {
            "—— 参数实测结果（同模型、同提示、同轮次）——",
            $"  {report.VramNote}。",
        };
        foreach (var variant in report.Variants)
        {
            if (variant.MeanDecodeTps is not { } mean)
            {
                lines.Add($"  {variant.Label}：全部失败");
                continue;
            }
            var worst = variant.WorstDecodeTps is { } w ? $"（最差 {w:F2}）" : "";
            var ttft = variant.MeanTtft is { } t ? $" · TTFT {t:F3} s" : "";
            var extras = new List<string>();
            if (variant.MeanPrefillTps is { } prefill) extras.Add($"预填 {prefill:F0} tok/s");
            if (variant.DraftAcceptance is { } acceptance) extras.Add($"草稿 {acceptance:P0}（{variant.DraftAccepted}/{variant.DraftOffered}）");
            if (variant.PeakVramMb > 0) extras.Add($"显存峰 {variant.PeakVramMb:N0} MiB");
            var telemetry = extras.Count > 0 ? " · " + string.Join(" · ", extras) : "";
            var mark = variant.Label == report.FastestLabel ? " ★" : "";
            lines.Add($"  {variant.Label}：{mean:F2} tok/s {worst}{ttft}{telemetry}{mark}");
        }
        lines.AddRange(report.Errors.Select(e => $"  失败：{e}"));
        return string.Join("\n", lines);
    }
}
