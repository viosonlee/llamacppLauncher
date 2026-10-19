using System.Diagnostics;

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

    public static async Task<int> ReadUsedMibAsync(CancellationToken ct = default)
    {
        try
        {
            var start = new ProcessStartInfo("nvidia-smi", Query)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var process = Process.Start(start);
            if (process == null) return 0;
            var text = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return ParseUsedMib(text);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>Parses the first line of an nvidia-smi "memory.used" reply; 0 when unreadable.</summary>
    public static int ParseUsedMib(string? text)
    {
        var first = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return int.TryParse(first, out var used) && used >= 0 ? used : 0;
    }
}
