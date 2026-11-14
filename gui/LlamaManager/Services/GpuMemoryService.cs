using System.Diagnostics;
using System.IO;
using System.Text;

namespace LlamaManager.Services;

/// <summary>
/// Reads the GPU's used VRAM from <c>nvidia-smi</c>. The parameter sweep samples this while a
/// variant runs so the report can show the peak: MoE offload tuning is exactly about how close a
/// configuration sits to the card's limit, which the finished tok/s alone does not reveal.
/// </summary>
public static class GpuMemoryService
{
    /// <summary>Query shared by the live reader and the doctor/probe paths.</summary>
    public const string Query = "--query-gpu=memory.used --format=csv,noheader,nounits";

    public static Task<int> ReadUsedMibAsync(CancellationToken ct = default)
        => ReadUsedMibAsync(new ProcessStartInfo("nvidia-smi", Query)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }, TimeSpan.FromSeconds(3), ct);

    // Per-call process/deadline seam keeps regression probes away from real devices and PATH.
    internal static async Task<int> ReadUsedMibAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var process = Process.Start(start);
            if (process == null) return 0;
            var output = ReadPrefixAsync(process.StandardOutput, deadline.Token);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                return process.ExitCode == 0 ? ParseUsedMib(await output.ConfigureAwait(false)) : 0;
            }
            catch
            {
                deadline.Cancel();
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
                throw;
            }
        }
        // A missing, failed or timed-out probe is unavailable, not a failed benchmark. Caller
        // cancellation is deliberately different and must still propagate to the sampler.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            return 0;
        }
    }

    private static async Task<string> ReadPrefixAsync(StreamReader reader, CancellationToken ct)
    {
        const int limit = 4096;
        var text = new StringBuilder();
        var buffer = new char[limit];
        int count;
        // Retain only a small prefix (we use the first device), but drain the remainder so a
        // noisy/broken probe cannot fill either the pipe or the manager's managed heap.
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
            if (text.Length < limit) text.Append(buffer, 0, Math.Min(count, limit - text.Length));
        return text.ToString();
    }

    /// <summary>Parses the first line of an nvidia-smi "memory.used" reply; 0 when unreadable.</summary>
    public static int ParseUsedMib(string? text)
    {
        var first = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return int.TryParse(first, out var used) && used >= 0 ? used : 0;
    }
}
