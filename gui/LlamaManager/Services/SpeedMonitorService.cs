using System.Net.Http;
using System.Text.Json;

namespace LlamaManager.Services;

/// <summary>Uses server-reported timings; no estimation from stale logs or unrelated embedding requests.</summary>
public sealed class SpeedMonitorService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(800) };

    public async Task<string> QuerySpeedAsync(int port, CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://127.0.0.1:{port}/slots", ct);
            return Parse(json);
        }
        catch (HttpRequestException) { return ""; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ""; }
        catch (JsonException) { return ""; }
    }

    public static string Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return "";
        var statuses = new List<string>();
        foreach (var slot in doc.RootElement.EnumerateArray())
        {
            if (!slot.TryGetProperty("is_processing", out var processing) || processing.ValueKind != JsonValueKind.True) continue;
            var decoded = 0;
            if (slot.TryGetProperty("n_decoded", out var count) && count.TryGetInt32(out var n)) decoded = n;
            if (slot.TryGetProperty("next_token", out var next))
            {
                if (next.ValueKind == JsonValueKind.Array && next.GetArrayLength() > 0) next = next[0];
                if (next.ValueKind == JsonValueKind.Object && next.TryGetProperty("n_decoded", out count) && count.TryGetInt32(out n)) decoded = n;
            }
            var status = decoded > 0 ? $"生成中：{decoded} tokens" : "处理请求中";
            if (slot.TryGetProperty("timings", out var timing) && timing.ValueKind == JsonValueKind.Object &&
                timing.TryGetProperty("predicted_per_second", out var rate) && rate.TryGetDouble(out var speed) && speed > 0)
                status += $" · {speed:F1} tok/s";
            statuses.Add(status);
        }
        return string.Join(" | ", statuses);
    }
}
