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
    public static async Task<string> DownloadAsync(string url, string destination, string? sha256,
        Action<string> log, CancellationToken ct, Action<long, long?>? progress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        // A unique temp name per attempt: the GUI, a second window and the CLI can download the
        // same asset at once, and a shared ".part" made them fight over one FileShare.None handle
        // (and let a failing attempt delete the other attempt's work). The final move stays atomic.
        var temp = $"{destination}.{Environment.ProcessId}.{Guid.NewGuid():N}.part";
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
            await PublishAsync(temp, destination, ct).ConfigureAwait(false);
            return destination;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Publishes a verified download. Two sessions can finish the same asset at once, and Windows'
    /// replace-existing move refuses the loser with a sharing/access-denied error even though both
    /// payloads are identical and verified, so retry briefly instead of failing the download.
    /// </summary>
    private static async Task PublishAsync(string temp, string destination, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temp, destination, overwrite: true); return; }
            catch (Exception ex) when (attempt < 8 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(40 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
    }

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

    /// <summary>
    /// Publishes extracted files with same-volume renames. Existing targets are moved into the
    /// caller-owned backup directory first; any failure restores every file already published.
    /// This keeps an engine update from leaving a mixed old/new installation behind.
    /// </summary>
    public static int PublishStagedDirectories(
        IReadOnlyList<(string StagingRoot, string DestinationRoot)> roots,
        string backupRoot)
    {
        if (roots.Count == 0) return 0;
        var files = new List<(string Staged, string Target, string Backup)>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            var staging = Path.GetFullPath(roots[rootIndex].StagingRoot);
            var destination = Path.GetFullPath(roots[rootIndex].DestinationRoot);
            if (!Directory.Exists(staging)) continue;
            foreach (var staged in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(staging, staged);
                if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    Path.IsPathRooted(relative))
                    throw new IOException("暂存文件越界：" + staged);
                var target = Path.GetFullPath(Path.Combine(destination, relative));
                if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("安装目标越界：" + target);
                if (Directory.Exists(target))
                    throw new IOException("安装目标与本应替换的文件同名：" + target);
                if (!targets.Add(target))
                    throw new IOException("安装包包含重复目标：" + target);
                var backup = Path.Combine(backupRoot, rootIndex.ToString(), relative);
                files.Add((Path.GetFullPath(staged), target, Path.GetFullPath(backup)));
            }
        }

        var published = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
                string? backup = null;
                if (File.Exists(file.Target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Backup)!);
                    File.Move(file.Target, file.Backup);
                    backup = file.Backup;
                }
                published.Add((file.Target, backup));
                File.Move(file.Staged, file.Target);
            }
        }
        catch (Exception failure)
        {
            var rollbackErrors = new List<Exception>();
            for (var i = published.Count - 1; i >= 0; i--)
            {
                var (target, backup) = published[i];
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (backup != null && File.Exists(backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Move(backup, target);
                    }
                }
                catch (Exception rollback) { rollbackErrors.Add(rollback); }
            }
            if (rollbackErrors.Count > 0)
                throw new IOException(
                    "引擎文件替换失败，且自动回滚未能完整恢复；请重新运行初始化环境。",
                    new AggregateException([failure, .. rollbackErrors]));
            throw;
        }
        return published.Count;
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
