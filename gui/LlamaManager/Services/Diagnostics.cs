using System;
using System.IO;
using System.Text;

namespace LlamaManager.Services;

/// <summary>
/// Writes unhandled exceptions to a dedicated crash log so failures that only
/// reproduce on a real machine (real config, models, GPU probes) can be read back.
/// </summary>
public static class Diagnostics
{
    public static void WriteCrash(Exception? exception)
    {
        if (exception == null) return;
        try
        {
            var root = ConfigService.GetManagerRoot();
            var dir = Path.Combine(root, "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "crash.log");
            var text = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception.GetType().FullName}: {exception.Message}")
                .AppendLine(exception.StackTrace ?? "(no stack)")
                .AppendLine()
                .ToString();
            File.AppendAllText(file, text, Encoding.UTF8);
        }
        catch
        {
            // Never let diagnostics take the app down.
        }
    }
}
