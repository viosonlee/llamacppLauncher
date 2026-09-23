using System.IO;
using System.Text;

namespace LlamaManager.Services;

public static class LogService
{
    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
            File.AppendAllText(ConfigService.GetLogPath(), line + Environment.NewLine);
        }
        catch
        {
            // Silently ignore log write failures
        }
    }

    public static List<string> ReadRecent(int count = 500)
    {
        var result = new List<string>();
        AppendTail(result, ConfigService.GetLogPath(), count, "管理器");
        AppendTail(result, ConfigService.GetMainStdoutPath(), count, "主服务输出");
        AppendTail(result, ConfigService.GetMainStderrPath(), count, "主服务日志");
        AppendTail(result, ConfigService.GetEmbeddingStdoutPath(), count, "嵌入服务输出");
        AppendTail(result, ConfigService.GetEmbeddingStderrPath(), count, "嵌入服务日志");
        return result;
    }

    private static void AppendTail(List<string> result, string path, int maxLines, string title)
    {
        if (!File.Exists(path))
            return;

        try
        {
            var lines = ReadTailLines(path, maxLines);
            if (lines.Count == 0)
                return;

            if (result.Count > 0)
                result.Add($"──── {title} ────");
            result.AddRange(lines);
        }
        catch
        {
        }
    }

    private static List<string> ReadTailLines(string path, int maxLines)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;
        if (length == 0)
            return lines;

        const int maxBytes = 1024 * 1024;
        var bytesToRead = Math.Min(length, (long)maxBytes);
        fs.Seek(length - bytesToRead, SeekOrigin.Begin);

        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: true);
        var content = reader.ReadToEnd();
        var rawLines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        var start = bytesToRead < length ? 1 : 0;
        for (int i = start; i < rawLines.Length; i++)
        {
            var cleaned = StripAnsi(rawLines[i]);
            if (!IsNoiseLog(cleaned))
                lines.Add(cleaned);
        }

        return lines.Skip(Math.Max(0, lines.Count - maxLines)).ToList();
    }

    private static bool IsNoiseLog(string line)
    {
        if (line.Contains("CUDA Graph id") && line.Contains("reused")) return true;
        if (line.Contains("set_adapters_lora")) return true;
        if (line.Contains("adapters_lora_are_same")) return true;
        if (line.Contains("set_embeddings:")) return true;
        if (line.Contains("res          send: task id")) return true;
        if (line.Contains("res          send: sending result")) return true;
        if (line.Contains("que    start_loop:")) return true;
        if (line.Contains("que          post: new task")) return true;
        if (line.Contains("srv  update_slots: posting NEXT_RESPONSE")) return true;
        if (line.Contains("slot handle_last_:")) return true;
        if (line.Contains("streamed chunk: data: {\"choices\"")) return true;
        return false;
    }

    public static string GetSpeedSummary()
    {
        try
        {
            var path = ConfigService.GetMainStderrPath();
            if (!File.Exists(path)) return "";

            var lines = ReadTailLines(path, 150);
            if (lines.Count == 0) return "";

            // 1. Check for recent completion timing
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i];
                var mEval = System.Text.RegularExpressions.Regex.Match(line, @"eval time\s*=\s*[\d\.]+\s*ms\s*/\s*\d+\s*tokens\s*\([^\)]*?([\d\.]+)\s*tokens per second\)");
                if (mEval.Success)
                    return $" | ⚡ 上次生成速度: {mEval.Groups[1].Value} t/s";

                var mPrefill = System.Text.RegularExpressions.Regex.Match(line, @"prompt processing.*?([\d\.]+)\s*tokens per second");
                if (mPrefill.Success)
                    return $" | ⚡ 提示词处理中: {mPrefill.Groups[1].Value} t/s";

                var mDecoded = System.Text.RegularExpressions.Regex.Match(line, @"slot process_toke.*?n_decoded\s*=\s*(\d+)");
                if (mDecoded.Success)
                    return $" | ⚡ 正在生成 Token: 已生成 {mDecoded.Groups[1].Value} tok";
            }
        }
        catch { }
        return "";
    }

    private static string StripAnsi(string line)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;?]*[A-Za-z]", "");
        }
        catch
        {
            return line;
        }
    }
}
