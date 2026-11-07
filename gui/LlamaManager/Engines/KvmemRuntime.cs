using System.IO;
using System.Net.Http;
using System.Text.Json;
using LlamaManager.Services;

namespace LlamaManager.Engines;

public static class KvmemRuntime
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task<string> EnsureTokenizerAsync(string bundle, string source, string python,
        string model, string cache, ILogSink log, CancellationToken ct)
    {
        // Use the release's own same-GGUF, exporter-hashed five-file cache. No pack/gguf/pip dependency.
        const string export = "import sys,json; from pathlib import Path; sys.path.insert(0,sys.argv[1]); from assets import tokenizer_path; print(json.dumps(str(tokenizer_path(Path(sys.argv[2]),Path(sys.argv[3]),prepare=True,cache=Path(sys.argv[4])))))";
        var args = new[] { "-X", "utf8", "-u", "-c", export, Path.Combine(bundle, "tools", "polystrata"), model, source, cache };
        var output = await ProcessProbe.RunAsync(python, string.Join(" ", args.Select(LauncherArguments.Quote)),
            TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        var line = output.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith('"'))
            ?? throw new IOException("KVMem 分词表导出未返回完整路径：" + output);
        var tokenizer = JsonSerializer.Deserialize<string>(line.Trim()) ?? throw new IOException("分词表路径为空。");
        foreach (var file in new[] { "vocab.json", "merges.txt", "tokenizer.json", "token_type.json", "chat_template.jinja" })
            if (!File.Exists(Path.Combine(tokenizer, file))) throw new IOException("KVMem 分词表不完整：" + file);
        log.Write("已验证同 GGUF 五件套分词表（无联网安装）。");
        return tokenizer;
    }

    public static async Task<bool> ReadyAsync(EngineSession session, CancellationToken ct)
    {
        try
        {
            using var health = JsonDocument.Parse(await Http.GetStringAsync($"http://127.0.0.1:{session.Port}/health", ct).ConfigureAwait(false));
            using var props = JsonDocument.Parse(await Http.GetStringAsync($"http://127.0.0.1:{session.Port}/props", ct).ConfigureAwait(false));
            if (!health.RootElement.TryGetProperty("loaded", out var loaded) || loaded.ValueKind != JsonValueKind.True) return false;
            if (!props.RootElement.TryGetProperty("model_path", out var model) || !PathIdentity.Equals(model.GetString(), session.ModelPath)) return false;
            var expected = new Dictionary<string, string>(session.Extra);
            if (!expected.ContainsKey("kvmem"))
            {
                var configPath = new LauncherArguments(session.Arguments).Get("--config");
                if (configPath == null || !File.Exists(configPath)) return false;
                using var config = JsonDocument.Parse(await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false));
                var args = config.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList();
                expected["kvmem"] = args.Contains("--kvmem-budget") ? "1" : "0";
                foreach (var (flag, field) in new[] { ("--kvmem-budget", "budget_tokens"), ("--kvmem-gen-reserve", "reserve_tokens"),
                    ("--kvmem-host-mib", "host_mib"), ("--kvmem-block-tokens", "block_tokens"), ("--kvmem-sessions", "retained_sessions"),
                    ("--kvmem-disk-mib", "disk_mib") })
                {
                    var index = args.IndexOf(flag);
                    expected[field] = index >= 0 ? args[index + 1] : "0";
                }
            }
            if (expected["kvmem"] != "1") return true;
            if (!props.RootElement.TryGetProperty("backend", out var backend) || backend.GetString() != "polystrata") return false;
            if (!props.RootElement.TryGetProperty("kvmem", out var kv) || !kv.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True) return false;
            foreach (var field in new[] { "budget_tokens", "reserve_tokens", "host_mib", "block_tokens", "retained_sessions", "disk_mib" })
            {
                if (!expected.TryGetValue(field, out var value)) continue;
                if (!kv.TryGetProperty(field, out var actual) || actual.ToString() != value) return false;
            }
            return kv.TryGetProperty("history_reuse", out var reuse) && reuse.ValueKind == JsonValueKind.True;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException) { return false; }
    }
}
