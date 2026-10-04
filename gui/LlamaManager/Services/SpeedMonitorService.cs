using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>
/// Live per-slot token sampling (the UI polls every 2s) plus completed decode timings from the main server log.
/// The server-reported rate (<c>timings.predicted_per_second</c>) is used when present, but this llama-server
/// build omits it, so sampling is the primary source and sampled rates are marked with "约".
/// A few missed snapshots are tolerated without discarding history; the sampler re-baselines afterwards
/// instead of averaging across the gap. The stderr log is truncated on every managed start, so a reused
/// file never attributes a previous run's timings to the new session.
/// </summary>
public enum SpeedKind { Idle, Prompt, Generating, Completed }

public sealed record SpeedStatus(string Text, SpeedKind Kind)
{
    public static readonly SpeedStatus Empty = new("", SpeedKind.Idle);
    public bool IsActive => Kind is SpeedKind.Prompt or SpeedKind.Generating;
}

public sealed class SpeedMonitorService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(800) };
    private static readonly Regex Timing = new(@"\beval time\s*=\s*[\d.]+\s*ms\s*/\s*(?<tokens>\d+)\s*tokens?\s*\([^)]*?(?<rate>[\d.]+)\s*tokens per second\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Missed snapshots (~2s poll cadence): tolerate a few before discarding sampling history.
    private const int MaxMissesBeforeReset = 3;
    private sealed record Sample(string Task, int Count, TimeSpan At, double? Rate);
    private sealed record Completed(double Rate, int Tokens, bool Estimated);
    private readonly Dictionary<int, Sample> _samples = new();
    private ServerSession? _session;
    private IncrementalLogReader _timingReader = new();
    private Completed? _completed;
    private string? _lastTimingLine;
    private int _misses;

    public void Reset(ServerSession session)
    {
        if (_session == session) return;
        _session = session;
        _samples.Clear();
        _completed = null;
        _lastTimingLine = null;
        _misses = 0;
        _timingReader = new IncrementalLogReader();
    }

    public async Task<SpeedStatus> QuerySpeedAsync(ServerSession session, CancellationToken ct = default)
    {
        Reset(session);
        string? json = null;
        try { json = await Http.GetStringAsync($"http://127.0.0.1:{session.Port}/slots", ct); }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        IReadOnlyList<string> timings = [];
        var path = ConfigService.GetMainStderrPath();
        try
        {
            // A log from an earlier process must not be attached to the new session.
            // Managed starts truncate this file (DetachedProcess uses CREATE_ALWAYS), so a fresh
            // reader starting at offset 0 only ever sees the current session; no priming to EOF,
            // which would discard a still-running session's own history on GUI reattach.
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path).Ticks >= session.StartTimeUtcTicks)
                timings = _timingReader.Read(path, 20, line => ParseDecodeTiming(line) != null);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        ct.ThrowIfCancellationRequested();
        return Observe(session, json, timings, TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency));
    }

    // Keeping the clock and log input explicit makes request resets and sample rates testable.
    public SpeedStatus Observe(ServerSession session, string? json, IEnumerable<string> logLines, TimeSpan now)
    {
        Reset(session);
        var newTiming = false;
        foreach (var line in logLines.Reverse())
        {
            var decoded = ParseDecodeTiming(line);
            if (decoded == null) continue;
            if (line != _lastTimingLine)
            {
                _completed = new Completed(decoded.Value.Rate, decoded.Value.Tokens, false);
                _lastTimingLine = line;
                newTiming = true;
            }
            break;
        }

        var active = new HashSet<int>();
        var statuses = new List<string>();
        var kind = SpeedKind.Idle;
        var validSlots = false;
        if (json != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    validSlots = true;
                    var index = 0;
                    foreach (var slot in doc.RootElement.EnumerateArray())
                    {
                        var id = index++;
                        if (slot.ValueKind == JsonValueKind.Object && slot.TryGetProperty("id", out var idProperty) &&
                            idProperty.ValueKind == JsonValueKind.Number && idProperty.TryGetInt32(out var parsedId)) id = parsedId;
                        if (slot.ValueKind != JsonValueKind.Object || !slot.TryGetProperty("is_processing", out var processing) ||
                            processing.ValueKind != JsonValueKind.True) continue;
                        active.Add(id);
                        var decoded = Integer(slot, "n_decoded");
                        if (slot.TryGetProperty("next_token", out var next))
                        {
                            if (next.ValueKind == JsonValueKind.Array && next.GetArrayLength() > 0) next = next[0];
                            decoded = Integer(next, "n_decoded", decoded);
                        }
                        var task = slot.TryGetProperty("id_task", out var taskId) ? taskId.ToString() : "";
                        if (decoded <= 0)
                        {
                            _samples.Remove(id);
                            var total = Integer(slot, "n_prompt_tokens");
                            var done = Integer(slot, "n_prompt_tokens_processed") + Integer(slot, "n_prompt_tokens_cache");
                            if (kind == SpeedKind.Idle) kind = SpeedKind.Prompt;
                            statuses.Add(total > 0 ? $"提示词处理：{Math.Min(done, total):N0}/{total:N0} tokens" : "提示词处理中…");
                            continue;
                        }

                        double? rate = null;
                        var reported = slot.TryGetProperty("timings", out var timing) ? Number(timing, "predicted_per_second") : null;
                        if (reported > 0) rate = reported;
                        else if (_misses == 0 && _samples.TryGetValue(id, out var previous) && previous.Task == task && decoded >= previous.Count)
                        {
                            var seconds = (now - previous.At).TotalSeconds;
                            if (seconds >= 0.05) rate = (decoded - previous.Count) / seconds;
                        }
                        _samples[id] = new Sample(task, decoded, now, rate);
                        kind = SpeedKind.Generating;
                        var rateText = rate.HasValue
                            ? $"{(reported.HasValue ? "" : "约 ")}{rate.Value:F2} tok/s"
                            : "速度采样中…";
                        statuses.Add($"生成中：{rateText} · {decoded:N0} tokens");
                    }
                }
            }
            catch (JsonException) { }
        }

        if (validSlots)
        {
            foreach (var id in _samples.Keys.Where(id => !active.Contains(id)).ToArray())
            {
                var last = _samples[id];
                if (!newTiming && last.Rate > 0) _completed = new Completed(last.Rate.Value, last.Count, true);
                _samples.Remove(id);
            }
            _misses = 0;
        }
        // Tolerate a few missed snapshots (slow/timeout reads) without discarding history;
        // the next valid snapshot re-baselines instead of averaging across the gap.
        else if (++_misses >= MaxMissesBeforeReset) _samples.Clear();

        if (statuses.Count > 0) return new SpeedStatus(string.Join(" | ", statuses), kind);
        // Idle with no history stays empty (regression contract: Parse(idle) == "").
        return _completed is { } completed
            ? new SpeedStatus($"上次速度：{(completed.Estimated ? "约 " : "")}{completed.Rate:F2} tok/s · {completed.Tokens:N0} tokens", SpeedKind.Completed)
            : SpeedStatus.Empty;
    }

    public static string Parse(string json) => new SpeedMonitorService().Observe(new ServerSession(), json, [], TimeSpan.Zero).Text;

    public static (double Rate, int Tokens)? ParseDecodeTiming(string line)
    {
        var start = line.IndexOf("eval time", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || Regex.IsMatch(line[..start], @"\bprompt\s*$", RegexOptions.IgnoreCase)) return null;
        var match = Timing.Match(line[start..]);
        if (!match.Success || !double.TryParse(match.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ||
            !double.IsFinite(rate) || rate <= 0 || !int.TryParse(match.Groups["tokens"].Value, out var tokens) || tokens <= 0) return null;
        return (rate, tokens);
    }

    private static int Integer(JsonElement element, string name, int fallback = 0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
