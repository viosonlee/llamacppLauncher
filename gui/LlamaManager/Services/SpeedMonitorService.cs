using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LlamaManager.Services;

public static class SpeedMonitorService
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMilliseconds(700) };
    private static DateTime _lastSampleTime = DateTime.MinValue;
    private static int _lastDecodedTokens = 0;
    private static double _smoothedSpeed = 0;
    private static double _lastCompletedSpeed = 0;
    private static int _lastCompletedTokens = 0;
    private static bool _wasProcessing = false;
    private static DateTime _generationStartTime = DateTime.MinValue;
    private static string _lastStatusText = "";

    public static async Task<string> QuerySpeedAsync(int port)
    {
        try
        {
            var url = $"http://127.0.0.1:{port}/slots";
            var json = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var slot = root[0];
                var isProcessing = slot.TryGetProperty("is_processing", out var ipProp) && ipProp.GetBoolean();

                var promptTokens = slot.TryGetProperty("n_prompt_tokens", out var ptProp) ? ptProp.GetInt32() : 0;
                var promptProcessed = slot.TryGetProperty("n_prompt_tokens_processed", out var ppProp) ? ppProp.GetInt32() : 0;

                var promptCache = slot.TryGetProperty("n_prompt_tokens_cache", out var pcProp) ? pcProp.GetInt32() : 0;

                int nDecoded = 0;
                if (slot.TryGetProperty("next_token", out var ntProp) && ntProp.ValueKind == JsonValueKind.Array && ntProp.GetArrayLength() > 0)
                {
                    var nt = ntProp[0];
                    if (nt.TryGetProperty("n_decoded", out var ndProp))
                        nDecoded = ndProp.GetInt32();
                }

                var now = DateTime.Now;

                if (isProcessing)
                {
                    // 1. If tokens are actively decoding, we are in generation phase
                    if (nDecoded > 0)
                    {
                        if (!_wasProcessing)
                        {
                            _generationStartTime = now;
                            _lastSampleTime = now;
                            _lastDecodedTokens = nDecoded;
                            _smoothedSpeed = 0;
                            _wasProcessing = true;
                        }
                        else
                        {
                            var dt = (now - _lastSampleTime).TotalSeconds;
                            if (dt >= 0.25 && nDecoded >= _lastDecodedTokens)
                            {
                                var dTokens = nDecoded - _lastDecodedTokens;
                                if (dTokens > 0)
                                {
                                    var instantSpeed = dTokens / dt;
                                    _smoothedSpeed = _smoothedSpeed <= 0 ? instantSpeed : (_smoothedSpeed * 0.25 + instantSpeed * 0.75);
                                    _lastSampleTime = now;
                                    _lastDecodedTokens = nDecoded;
                                }
                            }
                        }

                        var totalGenTime = (now - _generationStartTime).TotalSeconds;
                        var avgSpeed = (totalGenTime > 0.5 && nDecoded > 0) ? (nDecoded / totalGenTime) : 0;
                        var displaySpeed = _smoothedSpeed > 0 ? _smoothedSpeed : avgSpeed;
                        var speedStr = displaySpeed > 0 ? $"{displaySpeed:F1} t/s" : "计算中…";

                        _lastStatusText = $"⚡ [生成中] {speedStr} (已生成 {nDecoded:N0} tok)";
                        return _lastStatusText;
                    }
                    else
                    {
                        // 2. nDecoded == 0: Still processing prompt (prefill)
                        var totalProcessed = promptProcessed + promptCache;
                        var pct = promptTokens > 0 ? Math.Min(100.0, (double)totalProcessed / promptTokens * 100) : 100;
                        _lastStatusText = $"⚡ [读取提示词] {totalProcessed:N0}/{promptTokens:N0} ({pct:N0}%)";
                        return _lastStatusText;
                    }
                }
                else
                {
                    if (_wasProcessing)
                    {
                        _lastCompletedTokens = nDecoded > 0 ? nDecoded : _lastDecodedTokens;
                        var totalTime = (now - _generationStartTime).TotalSeconds;
                        if (totalTime > 0.5 && _lastCompletedTokens > 0)
                            _lastCompletedSpeed = _lastCompletedTokens / totalTime;
                        _wasProcessing = false;
                    }

                    // Fallback to check stderr for timing logs if available
                    if (_lastCompletedSpeed <= 0)
                    {
                        var stderrSpeed = ParseStderrSpeed();
                        if (stderrSpeed > 0)
                            _lastCompletedSpeed = stderrSpeed;
                    }

                    if (_lastCompletedSpeed > 0)
                    {
                        var tokStr = _lastCompletedTokens > 0 ? $" (共 {_lastCompletedTokens} tok)" : "";
                        _lastStatusText = $"⚡ 上次速度: {_lastCompletedSpeed:F1} t/s{tokStr}";
                        return _lastStatusText;
                    }

                    _lastStatusText = "";
                    return "";
                }
            }
        }
        catch
        {
            var stderrSpeed = ParseStderrSpeed();
            if (stderrSpeed > 0)
                return $"⚡ 上次速度: {stderrSpeed:F1} t/s";
        }

        return _lastStatusText;
    }

    private static double ParseStderrSpeed()
    {
        try
        {
            var path = ConfigService.GetMainStderrPath();
            if (!File.Exists(path)) return 0;

            var lines = LogService.ReadRecent(100);
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var m = Regex.Match(lines[i], @"eval time\s*=\s*[\d\.]+\s*ms\s*/\s*\d+\s*tokens\s*\([^\)]*?([\d\.]+)\s*tokens per second\)");
                if (m.Success && double.TryParse(m.Groups[1].Value, out var speed))
                    return speed;
            }
        }
        catch { }
        return 0;
    }
}
