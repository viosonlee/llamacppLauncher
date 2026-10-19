using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaManager.Services;

/// <summary>
/// 通过 /v1/chat/completions 流式请求实测推理速度：
/// 首 Token 延迟 (TTFT) 与解码吞吐 (tok/s)。
/// </summary>
public static class SpeedTestService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(600) };

    private const string TestPrompt =
        "Explain how a C# async producer-consumer pipeline using System.Threading.Channels " +
        "works. Include practical implementation details and potential performance pitfalls.";

    public sealed class Result
    {
        public string Model { get; set; } = "";
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        public int StreamEvents { get; set; }
        public bool HasUsage { get; set; }
        public double? TtftSeconds { get; set; }
        /// <summary>Engine-reported decode rate when the server includes it in the final SSE chunk.</summary>
        public double? ServerDecodeTps { get; set; }
        public double? DecodeTps { get; set; }
        /// <summary>Engine-reported prompt (prefill) rate from the same <c>timings</c> block, or null.</summary>
        public double? PrefillTps { get; set; }
        /// <summary>Speculative drafts offered/accepted across the request (0 when not speculating).</summary>
        public int DraftOffered { get; set; }
        public int DraftAccepted { get; set; }
        public double TotalSeconds { get; set; }
        public string? Error { get; set; }
        public bool Success => Error == null;

        /// <summary>Share of the drafts the engine accepted, or null when it drafted none.</summary>
        public double? DraftAcceptance => DraftOffered > 0 ? (double)DraftAccepted / DraftOffered : null;
    }

    /// <summary>The engine's own <c>timings</c> block from one response: the authoritative rates.</summary>
    public sealed record TimingsSample(double? DecodeTps, double? PrefillTps, int DraftOffered, int DraftAccepted)
    {
        /// <summary>Share of the speculation drafts the engine accepted, or null when it drafted none.</summary>
        public double? DraftAcceptance => DraftOffered > 0 ? (double)DraftAccepted / DraftOffered : null;
    }

    /// <summary>Parses a server JSON payload's <c>timings</c> block; null when absent or all-empty.</summary>
    public static TimingsSample? ParseTimings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseTimingsElement(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private static TimingsSample? ParseTimingsElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("timings", out var timings) || timings.ValueKind != JsonValueKind.Object)
            return null;
        var decode = Sanitize(GetDouble(timings, "predicted_per_second"));
        var prefill = Sanitize(GetDouble(timings, "prompt_per_second"));
        var offered = GetInt(timings, "draft_n");
        var accepted = GetInt(timings, "draft_n_accepted");
        if (decode == null && prefill == null && offered <= 0) return null;
        return new TimingsSample(decode, prefill, offered, Math.Max(0, accepted));
    }

    private static double? Sanitize(double? value) =>
        value is > 0 && double.IsFinite(value.Value) ? value : null;

    public static async Task<Result> RunAsync(int port, CancellationToken ct = default)
    {
        var result = new Result();
        var stopwatch = new Stopwatch();

        try
        {
            // 1) 取当前服务的模型 id
            using (var resp = await Http.GetAsync($"http://127.0.0.1:{port}/v1/models", ct))
            {
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                {
                    result.Model = data[0].GetProperty("id").GetString() ?? "";
                }
            }

            if (string.IsNullOrEmpty(result.Model))
            {
                result.Error = "服务未返回模型 ID";
                return result;
            }

            // 2) 流式生成固定长度的回复
            var payload = JsonSerializer.Serialize(new
            {
                model = result.Model,
                messages = new[] { new { role = "user", content = TestPrompt } },
                max_tokens = 256,
                temperature = 0,
                stream = true,
                stream_options = new { include_usage = true },
                ignore_eos = true,
                chat_template_kwargs = new { enable_thinking = false }
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            stopwatch.Start();
            using var chatResp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            chatResp.EnsureSuccessStatusCode();

            await using var stream = await chatResp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            double? firstTokenAt = null;
            double lastTokenAt = 0;
            string? line;

            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") break;

                var now = stopwatch.Elapsed.TotalSeconds;

                using var chunk = JsonDocument.Parse(data);
                var root = chunk.RootElement;

                if (ParseTimingsElement(root) is { } sample)
                {
                    if (sample.DecodeTps is { } rate) result.ServerDecodeTps = rate;
                    if (sample.PrefillTps is { } prefill) result.PrefillTps = prefill;
                    if (sample.DraftOffered > 0) { result.DraftOffered = sample.DraftOffered; result.DraftAccepted = sample.DraftAccepted; }
                }

                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    result.HasUsage = usage.TryGetProperty("completion_tokens", out _);
                    result.PromptTokens = GetInt(usage, "prompt_tokens");
                    result.CompletionTokens = GetInt(usage, "completion_tokens");
                    result.TotalTokens = GetInt(usage, "total_tokens");
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var choice in choices.EnumerateArray())
                {
                    if (!choice.TryGetProperty("delta", out var delta)) continue;
                    var text = (GetString(delta, "content") ?? "") + (GetString(delta, "reasoning_content") ?? "");
                    if (text.Length == 0) continue;

                    firstTokenAt ??= now;
                    lastTokenAt = now;
                    result.StreamEvents++;
                }
            }

            result.TotalSeconds = stopwatch.Elapsed.TotalSeconds;
            result.TtftSeconds = firstTokenAt;

            // Strata reports the engine's own decode timing. Prefer it because one SSE event can
            // contain several speculative tokens; event timestamps would understate MTP throughput.
            result.DecodeTps = result.ServerDecodeTps;
            if (!result.DecodeTps.HasValue && result.HasUsage && firstTokenAt.HasValue &&
                lastTokenAt > firstTokenAt.Value && result.CompletionTokens > 1)
                result.DecodeTps = (result.CompletionTokens - 1) / (lastTokenAt - firstTokenAt.Value);
            if (!firstTokenAt.HasValue) result.Error = "服务未返回生成内容。";

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "测速已取消";
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

    private static int GetInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double? GetDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
