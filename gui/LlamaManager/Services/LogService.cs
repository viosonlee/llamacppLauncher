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

    public static List<string> ReadRecent(int count = 500)
    {
        var result = new List<string>();
        var files = new[]
        {
            (ConfigService.GetLogPath(), "管理器"),
            (ConfigService.GetMainStdoutPath(), "主服务输出"),
            (ConfigService.GetMainStderrPath(), "主服务日志"),
            (ConfigService.GetEmbeddingStdoutPath(), "嵌入服务输出"),
            (ConfigService.GetEmbeddingStderrPath(), "嵌入服务日志")
        };
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

    public static string GetGpuSummary(ServerSession session)
    {
        lock (Gate)
        {
            if (_gpuSession != session) { _gpuSession = session; _gpuSummary = ""; }
            var parsed = ParseGpuSummary(ReadFileTail(ConfigService.GetMainStdoutPath()).Concat(ReadFileTail(ConfigService.GetMainStderrPath())));
            if (parsed.Length > 0) _gpuSummary = parsed;
            return _gpuSummary;
        }
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
