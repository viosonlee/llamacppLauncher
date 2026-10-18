using System.Net.Http;
using System.Text.Json;

namespace LlamaManager.Services;

/// <summary>What the PolyStrata service is doing right now (<c>/metrics</c> → <c>live</c>).</summary>
public sealed record PolyLiveState(
    string State,
    string? Phase,
    int Queued,
    int PromptTokens,
    int Generated,
    int MaxTokens,
    double ElapsedSeconds,
    double? TokensPerSecond,
    double? MeanTokensPerSecond,
    double? PrefillTokensPerSecond)
{
    /// <summary>Tokens are being written; the engine's own windowed rate is authoritative.</summary>
    public bool IsGenerating => string.Equals(State, "generating", StringComparison.OrdinalIgnoreCase) && Generated > 0;

    /// <summary>The prompt is being read before the first token (<c>reading</c> / <c>prefill</c>).</summary>
    public bool IsReading => string.Equals(State, "reading", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(State, "prefill", StringComparison.OrdinalIgnoreCase);

    /// <summary>A request is waiting for the engine (single-slot service, so a queue means waiting).</summary>
    public bool IsQueued => Queued > 0 && !IsGenerating && !IsReading;

    /// <summary>The engine reports itself free; <c>generated</c> and the rates are null.</summary>
    public bool IsIdle => !IsGenerating && !IsReading && !IsQueued;
}

/// <summary>One finished request from <c>/metrics</c> → <c>requests[]</c>.</summary>
public sealed record PolyRequestRecord(
    double Time,
    double DurationSeconds,
    int PromptTokens,
    int OutputTokens,
    string Finish,
    double DecodeTokensPerSecond,
    double PromptMilliseconds,
    int DraftsOffered,
    int DraftsAccepted)
{
    /// <summary>Share of the speculation drafts the engine accepted, or null when it drafted none.</summary>
    public double? DraftAcceptance => DraftsOffered > 0 ? (double)DraftsAccepted / DraftsOffered : null;
}

/// <summary>The engine block: the expert cache the service settled on, and the VRAM it left free.</summary>
public sealed record PolyEngineInfo(int ExpertSlots, int ExpertCacheMib, int VramFreeMib, int Workers, int MaxContext);

/// <summary>A snapshot of the card and the machine, as the engine sees them.</summary>
public sealed record PolyHardware(int GpuUtilization, long GpuMemoryUsed, long GpuMemoryTotal, int GpuTemperature, double GpuPower, double CpuPercent, long RamUsed, long RamTotal);

/// <summary>Everything the launcher reads from <c>/metrics</c>.</summary>
public sealed record PolyMetrics(PolyLiveState Live, PolyRequestRecord? LastRequest, PolyEngineInfo? Engine, PolyHardware? Hardware);

/// <summary>
/// The PolyStrata service publishes its own telemetry on <c>/metrics</c>: the live state and windowed
/// decode rate, the finished requests with their draft acceptance, and the expert cache it settled on.
/// The engine writes no per-request log line, so this endpoint — not the log file — is the only live
/// source for the speed readout and the activity line the log page shows.
/// </summary>
public static class PolyMetricsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1200) };

    public static async Task<PolyMetrics?> QueryAsync(int port, CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://127.0.0.1:{port}/metrics", ct).ConfigureAwait(false);
            return Parse(json);
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (JsonException) { return null; }
    }

    public static PolyMetrics? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("live", out var liveElement) || liveElement.ValueKind != JsonValueKind.Object) return null;

            var live = new PolyLiveState(
                Text(liveElement, "state") ?? "",
                Text(liveElement, "phase"),
                Integer(liveElement, "queued"),
                Integer(liveElement, "prompt_tokens"),
                Integer(liveElement, "generated"),
                Integer(liveElement, "max_tokens"),
                Number(liveElement, "elapsed_s") ?? 0,
                Positive(liveElement, "tok_s"),
                Positive(liveElement, "tok_s_mean"),
                Positive(liveElement, "prefill_tok_s_mean"));

            return new PolyMetrics(live, LastRequest(root), Engine(root), Hardware(root));
        }
        catch (JsonException) { return null; }
    }

    private static PolyRequestRecord? LastRequest(JsonElement root)
    {
        if (!root.TryGetProperty("requests", out var requests) || requests.ValueKind != JsonValueKind.Array) return null;
        PolyRequestRecord? latest = null;
        foreach (var item in requests.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var time = Number(item, "time") ?? 0;
            if (latest != null && time <= latest.Time) continue;
            latest = new PolyRequestRecord(
                time,
                Number(item, "duration_s") ?? 0,
                Integer(item, "prompt_tokens"),
                Integer(item, "output_tokens"),
                Text(item, "finish") ?? "",
                Number(item, "decode_tok_s") ?? 0,
                Number(item, "prompt_ms") ?? 0,
                Integer(item, "drafts_offered"),
                Integer(item, "drafts_accepted"));
        }
        return latest;
    }

    private static PolyEngineInfo? Engine(JsonElement root)
    {
        if (!root.TryGetProperty("engine", out var engine) || engine.ValueKind != JsonValueKind.Object) return null;
        return new PolyEngineInfo(
            Integer(engine, "expert_slots"),
            Integer(engine, "expert_cache_mib"),
            Integer(engine, "vram_free_mib"),
            Integer(engine, "pool_workers"),
            Integer(engine, "max_context"));
    }

    private static PolyHardware? Hardware(JsonElement root)
    {
        if (!root.TryGetProperty("hardware", out var hardware) || hardware.ValueKind != JsonValueKind.Object) return null;
        var total = Long(hardware, "gpu_mem_total");
        if (total <= 0) return null;
        return new PolyHardware(
            Integer(hardware, "gpu_util"),
            Long(hardware, "gpu_mem_used"),
            total,
            Integer(hardware, "gpu_temp"),
            Number(hardware, "gpu_power") ?? 0,
            Number(hardware, "cpu") ?? 0,
            Long(hardware, "ram_used"),
            Long(hardware, "ram_total"));
    }

    /// <summary>
    /// One line for the log page's header: the expert cache, the card and the machine. Empty when the
    /// service reported nothing (an older build, or the endpoint is gone).
    /// </summary>
    public static string Describe(PolyMetrics? metrics)
    {
        if (metrics == null) return "";
        var parts = new List<string>();
        if (metrics.Engine is { } engine)
        {
            if (engine.ExpertSlots > 0)
                parts.Add($"{engine.ExpertSlots:N0} 专家" + (engine.ExpertCacheMib > 0 ? $" ({engine.ExpertCacheMib:N0} MiB)" : ""));
            if (engine.VramFreeMib > 0) parts.Add($"显存余 {engine.VramFreeMib:N0} MiB");
        }
        if (metrics.Hardware is { } hardware)
        {
            var used = hardware.GpuMemoryUsed / 1073741824.0;
            var total = hardware.GpuMemoryTotal / 1073741824.0;
            parts.Add($"显存 {used:F1}/{total:F1} GiB");
            if (hardware.GpuUtilization > 0) parts.Add($"GPU {hardware.GpuUtilization}%");
            if (hardware.GpuTemperature > 0) parts.Add($"{hardware.GpuTemperature}°C");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>A short phase name for the live line ("thinking" → 思考中).</summary>
    public static string DescribePhase(string? phase) => phase switch
    {
        null or "" => "",
        "thinking" => "思考中",
        "writing" => "作答中",
        "reading the prompt" => "读取提示词",
        _ => phase,
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    private static double? Positive(JsonElement element, string name) => Number(element, name) is { } value && value > 0 ? value : null;
}
