using System.IO;
using System.Net.Http;

namespace LlamaManager.Services;

/// <summary>
/// Downloads a curated GGUF into the models directory. Reuses the engine download mechanics
/// (progress, streaming, .part temp file) and adds the two checks a model needs that an engine
/// archive does not: free disk space before starting, and a GGUF magic check afterwards so a
/// truncated or wrong-file download is caught at once instead of at first model load.
/// </summary>
public static class ModelDownloadService
{
    private const string GgufMagic = "GGUF";

    public sealed record Result(string Path, long Bytes);

    /// <summary>
    /// Where curated downloads land: the user's configured models directory when it exists, so a
    /// downloaded model shows up in the scan immediately; the launcher's own models folder otherwise.
    /// </summary>
    public static string TargetDirectory
    {
        get
        {
            var configured = ConfigService.ReadConfig().ModelsDirectory;
            return string.IsNullOrWhiteSpace(configured) ? Path.Combine(ConfigService.GetRoot(), "models") : configured;
        }
    }

    public static async Task<Result> DownloadAsync(ModelDownload model, Action<string> log,
        CancellationToken ct, Action<long, long?>? progress = null)
    {
        var destination = Path.Combine(TargetDirectory, model.FileName);
        if (File.Exists(destination))
        {
            var existing = new FileInfo(destination);
            if (existing.Length == model.Bytes)
            {
                log($"已存在同名且同大小的文件，跳过下载：{destination}");
                return new Result(destination, existing.Length);
            }
            log($"已存在同名文件但大小不同（本地 {existing.Length}，期望 {model.Bytes}），将覆盖。");
        }
        CheckFreeSpace(model);
        var path = await EngineDownloadService.DownloadAsync(model.Url, destination, sha256: null, log, ct, progress)
            .ConfigureAwait(false);
        try
        {
            VerifyGguf(path);
        }
        catch
        {
            RemoveInvalid(path);
            throw;
        }
        return new Result(path, new FileInfo(path).Length);
    }

    private static void CheckFreeSpace(ModelDownload model)
    {
        Directory.CreateDirectory(TargetDirectory);
        var root = Path.GetPathRoot(Path.GetFullPath(TargetDirectory));
        if (string.IsNullOrEmpty(root)) return;
        var drive = new DriveInfo(root);
        var free = drive.AvailableFreeSpace;
        // Fail before a multi-gigabyte download, not after: keep 2 GiB of headroom for the system.
        if (free < model.Bytes + (2L << 30))
            throw new IOException($"磁盘空间不足：{root} 仅剩 {EngineDownloadService.FormatBytes(free)}，" +
                $"下载 {model.DisplayName} 需要约 {EngineDownloadService.FormatBytes(model.Bytes)} 加 2 GiB 余量。");
    }

    /// <summary>
    /// A GGUF always starts with the 4-byte magic; anything else means the download is junk
    /// (truncated, an HTML error page, a login redirect). Public so the regression suite can
    /// check the guard with crafted files instead of a real download.
    /// </summary>
    public static void VerifyGguf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[4];
        var read = stream.Read(magic);
        if (read != 4 || System.Text.Encoding.ASCII.GetString(magic) != GgufMagic)
            throw new IOException($"下载的文件不是有效的 GGUF（缺少文件头），已删除：{Path.GetFileName(path)}。" +
                "链接可能已失效，请到模型页面手动下载。");
    }

    /// <summary>Deletes a file that failed verification, mirroring what a failed download does.</summary>
    public static void RemoveInvalid(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
