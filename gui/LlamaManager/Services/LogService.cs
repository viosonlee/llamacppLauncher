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

    public static List<string> ReadRecent(int count = 200)
    {
        var result = new List<string>();
        AppendTail(result, ConfigService.GetLogPath(), count, "管理器");
        AppendTail(result, ConfigService.GetMainStdoutPath(), count, "主服务输出");
        AppendTail(result, ConfigService.GetMainStderrPath(), count, "主服务错误");
        AppendTail(result, ConfigService.GetEmbeddingStdoutPath(), count, "嵌入服务输出");
        AppendTail(result, ConfigService.GetEmbeddingStderrPath(), count, "嵌入服务错误");
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

        const int maxBytes = 256 * 1024;
        var bytesToRead = Math.Min(length, (long)maxBytes);
        fs.Seek(length - bytesToRead, SeekOrigin.Begin);

        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: true);
        var content = reader.ReadToEnd();
        var rawLines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        var start = bytesToRead < length ? 1 : 0;
        for (int i = start; i < rawLines.Length; i++)
            lines.Add(StripAnsi(rawLines[i]));

        return lines.Skip(Math.Max(0, lines.Count - maxLines)).ToList();
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
