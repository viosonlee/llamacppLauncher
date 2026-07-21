using System.IO;

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

    public static List<string> ReadRecent(int count = 50)
    {
        var logPath = ConfigService.GetLogPath();
        if (!File.Exists(logPath))
            return [];

        try
        {
            var lines = File.ReadAllLines(logPath);
            return lines.Skip(Math.Max(0, lines.Length - count)).ToList();
        }
        catch
        {
            return [];
        }
    }
}
