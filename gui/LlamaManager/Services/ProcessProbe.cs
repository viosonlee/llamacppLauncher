using System.Diagnostics;
using System.IO;

namespace LlamaManager.Services;

public static class ProcessProbe
{
    public static Task<string> RunAsync(string executable, string arguments, TimeSpan timeout, CancellationToken ct = default) =>
        RunAsync(new ProcessStartInfo(executable, arguments)
        {
            WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }, timeout, ct);

    /// <summary>Allows existing probe PATH/encoding/working-directory contracts to share bounded execution.</summary>
    internal static async Task<string> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct = default,
        int maxOutputCharacters = 1 << 20)
    {
        ct.ThrowIfCancellationRequested();
        if (maxOutputCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxOutputCharacters));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("无法启动设备检测进程。");
        if (start.RedirectStandardInput) process.StandardInput.Close();
        var output = ReadBoundedAsync(process.StandardOutput, maxOutputCharacters, deadline.Token);
        var error = ReadBoundedAsync(process.StandardError, maxOutputCharacters, deadline.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).ConfigureAwait(false);
            var results = await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException(string.Join("\n", results));
            return string.Join("\n", results);
        }
        catch
        {
            deadline.Cancel();
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            if (text.Length > limit - read) throw new IOException($"探测输出超过 {limit} 字符上限。");
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
}
