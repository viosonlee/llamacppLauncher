using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace LlamaManager.Services;

/// <summary>
/// Downloads engine archives over HTTP with progress reporting, verifies their SHA-256 when
/// the caller knows it, and extracts them with a zip-slip guard. Kept separate from the
/// engine-specific acquisition logic so the network/extract mechanics are testable alone.
/// </summary>
public static class EngineDownloadService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Thrown when a downloaded file's SHA-256 does not match the expected digest.</summary>
    public sealed class ChecksumMismatchException(string expected, string actual)
        : Exception($"校验失败：期望 {expected}，实际 {actual}。") { }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/>, reporting bytes and
    /// percentage. When <paramref name="sha256"/> is given, the file is verified and deleted on
    /// mismatch. Returns the local path.
    /// </summary>
    public static Task<string> DownloadAsync(string url, string destination, string? sha256,
        Action<string> log, CancellationToken ct, Action<long, long?>? progress = null) =>
        DownloadCoreAsync(url, destination, sha256, log, ct, progress,
            (source, target, token) => PublishVerifiedAsync(source, target, token));

    // Per-call publication injection exercises real HTTP/hash/cleanup with deterministic lock gates.
    // No mutable global hooks, and the public GUI/CLI API remains unchanged.
    internal static async Task<string> DownloadCoreAsync(string url, string destination, string? sha256,
        Action<string> log, CancellationToken ct, Action<long, long?>? progress,
        Func<string, string, CancellationToken, Task> publish)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        // Each transfer owns its staging file. Shared .part names collide during writes and allow
        // another attempt's cleanup to interfere after the original writer has closed.
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            log($"下载：{url}");
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    progress?.Invoke(received, total);
                }
            }
            if (!string.IsNullOrEmpty(sha256))
            {
                var actual = await Sha256Async(temp, ct).ConfigureAwait(false);
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(temp);
                    throw new ChecksumMismatchException(sha256, actual);
                }
                log("校验通过。");
            }
            await publish(temp, destination, ct).ConfigureAwait(false);
            return destination;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Atomically publishes already-verified bytes. Windows replacement can fail transiently while
    /// another process replaces or reads the same target. Retry only the rename, not HTTP or hash;
    /// genuine permissions failures still surface after six attempts (750ms total async delay).
    /// </summary>
    internal static async Task PublishVerifiedAsync(string source, string destination, CancellationToken ct,
        Action<string, string>? move = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        move ??= (from, to) => File.Move(from, to, overwrite: true);
        delay ??= (duration, token) => Task.Delay(duration, token);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested(); // Cancellation before commit must preserve the old file.
            try { move(source, destination); return; }
            catch (Exception ex) when (attempt < 5 && IsPublicationContention(ex))
            {
                await delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsPublicationContention(Exception ex) =>
        ex is IOException or UnauthorizedAccessException &&
        ex.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021);

    /// <summary>Streams the file through SHA-256 (lower-case hex) without loading it into memory.</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Extracts a zip into <paramref name="destination"/>. Entries whose resolved path escapes
    /// the destination (zip-slip) are rejected. Returns the extracted file count.
    /// </summary>
    public static int ExtractZip(string archive, string destination)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(archive);
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue; // directory entry
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !target.Equals(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("压缩包包含越界路径：" + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }
        return count;
    }

    /// <summary>Human-readable byte size for log lines.</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KiB",
        _ => $"{bytes} B",
    };
}
