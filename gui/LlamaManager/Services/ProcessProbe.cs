using System.Diagnostics;
using System.IO;

namespace LlamaManager.Services;

public static class ProcessProbe
{
    public static async Task<string> RunAsync(string executable, string arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        if (!process.Start()) throw new IOException("无法启动设备检测进程。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var results = await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException(string.Join("\n", results));
            return string.Join("\n", results);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
            throw;
        }
    }
}
