using System.IO;
using System.Text.RegularExpressions;
using LlamaManager.Models;

namespace LlamaManager.Services;

public static class LogService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IncrementalLogReader> Readers = new(StringComparer.OrdinalIgnoreCase);
    public static void Write(string message)
    {
        lock (Gate)
        {
            try { File.AppendAllText(ConfigService.GetLogPath(), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
            catch (IOException) { /* A log error must not hide the original operation error. */ }
        }
    }

    public static List<string> ReadFileTail(string path, int count = 500)
    {
        lock (Gate)
        {
            if (!Readers.TryGetValue(path, out var reader)) Readers[path] = reader = new IncrementalLogReader();
            try { return reader.Read(path).TakeLast(count).ToList(); }
            catch (IOException) { return []; }
        }
    }

    public static string GetPrimaryLogPath(string? backendId = null)
    {
        var id = string.IsNullOrWhiteSpace(backendId) ? ConfigService.ReadConfig().ActiveBackendId : backendId;
        if (!string.IsNullOrWhiteSpace(id) && id != "llamacpp")
            return Path.Combine(ConfigService.GetManagerRoot(), "logs", $"engine-{id}.log");
        return ConfigService.GetMainStderrPath();
    }

    public static List<string> ReadRecent(int count = 500) => ReadRecent(null, count);

    public static List<string> ReadRecent(string? backendId, int count = 500)
    {
        var id = string.IsNullOrWhiteSpace(backendId) ? ConfigService.ReadConfig().ActiveBackendId : backendId;
        var result = new List<string>();
        var root = ConfigService.GetManagerRoot();
        (string Path, string Title)[] files;

        if (id == "llamacpp")
        {
            files = new[]
            {
                (ConfigService.GetLogPath(), "管理器"),
                (ConfigService.GetMainStdoutPath(), "主服务输出"),
                (ConfigService.GetMainStderrPath(), "主服务日志"),
                (ConfigService.GetEmbeddingStdoutPath(), "嵌入服务输出"),
                (ConfigService.GetEmbeddingStderrPath(), "嵌入服务日志")
            };
        }
        else
        {
            var displayName = id == "polystrata" ? "PolyStrata" : id == "strata-upstream" ? "Strata" : id;
            files = new[]
            {
                (ConfigService.GetLogPath(), "管理器"),
                (Path.Combine(root, "logs", $"engine-{id}.log"), $"{displayName} 服务日志"),
                (Path.Combine(root, "logs", $"engine-{id}.stdout.log"), $"{displayName} 输出"),
                (Path.Combine(root, "logs", $"engine-{id}.stderr.log"), $"{displayName} 错误")
            };
        }

        foreach (var (path, title) in files)
        {
            var lines = ReadFileTail(path, Math.Max(1, count / files.Length));
            if (lines.Count == 0) continue;
            result.Add($"──── {title} ────");
            result.AddRange(lines);
        }
        return result;
    }

    private static ServerSession? _gpuSession;
    private static string _gpuSummary = "";

    public static string GetGpuSummary(ServerSession session) => GetGpuSummary(session, "llamacpp");

    public static string GetGpuSummary(ServerSession session, string backendId)
    {
        lock (Gate)
        {
            if (_gpuSession != session) { _gpuSession = session; _gpuSummary = ""; }
            var isLlama = string.IsNullOrWhiteSpace(backendId) || backendId == "llamacpp";
            var lines = isLlama
                ? ReadFileTail(ConfigService.GetMainStdoutPath()).Concat(ReadFileTail(ConfigService.GetMainStderrPath()))
                : ReadFileTail(Path.Combine(ConfigService.GetManagerRoot(), "logs", $"engine-{backendId}.log"));
            var parsed = isLlama ? ParseGpuSummary(lines) : ParseStrataGpuSummary(lines);
            if (parsed.Length > 0) _gpuSummary = parsed;
            return _gpuSummary;
        }
    }

    public static string ParseStrataGpuSummary(IEnumerable<string> lines)
    {
        string experts = "";
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"(?:expert cache\s+\d+\s*->\s*|loaded in [\d.]+ s;\s*)(?<vram>\d+)\s+of\s+(?<total>\d+)\s+routed experts in VRAM\s*\((?<gib>[\d.]+)\s*GiB\)");
            if (match.Success)
            {
                experts = $"{match.Groups["vram"].Value}/{match.Groups["total"].Value} 专家在显存 ({match.Groups["gib"].Value} GiB)";
                continue;
            }
            var upstreamMatch = Regex.Match(line, @"hybrid experts:\s*(?<vram>\d+)\s+of\s+(?<total>\d+)\s+per layer in VRAM\s*\((?<gib>[\d.]+)\s*GiB\)");
            if (upstreamMatch.Success)
            {
                experts = $"{upstreamMatch.Groups["vram"].Value}/{upstreamMatch.Groups["total"].Value} 专家/层在显存 ({upstreamMatch.Groups["gib"].Value} GiB)";
                continue;
            }
            var cacheMatch = Regex.Match(line, @"the expert cache\s+\d+\s*->\s*(?<vram>\d+)\s+experts\s*\((?<gib>[\d.]+)\s*GiB\)");
            if (cacheMatch.Success)
            {
                experts = $"{cacheMatch.Groups["vram"].Value} 专家在显存 ({cacheMatch.Groups["gib"].Value} GiB)";
            }
        }
        return experts;
    }

    public static string ParseGpuSummary(IEnumerable<string> lines)
    {
        string offload = "", gpu = "", cpu = "";
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"offloaded\s+(\d+/\d+)\s+layers");
            if (match.Success) offload = $"卸载 {match.Groups[1].Value} 层";
            match = Regex.Match(line, @"CUDA\d+\s+model buffer size\s*=\s*([\d.]+)\s*MiB");
            if (match.Success) gpu = $"GPU 权重 {match.Groups[1].Value} MiB";
            match = Regex.Match(line, @"CPU(?:_Mapped)?\s+model buffer size\s*=\s*([\d.]+)\s*MiB");
            if (match.Success) cpu = $"CPU 权重 {match.Groups[1].Value} MiB";
        }
        return string.Join(" · ", new[] { offload, gpu, cpu }.Where(s => s.Length > 0));
    }
}
