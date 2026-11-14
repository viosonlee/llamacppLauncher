using System.IO;

namespace LlamaManager.Services;

/// <summary>
/// Downloads a curated GGUF using the engine downloader's per-transfer staging and atomic
/// publication. Exact catalog length and bounded header/table checks precede publication and
/// cache reuse. These are structural checks, not a digest or verification of tensor weight bytes.
/// </summary>
public static class ModelDownloadService
{
    // Tokenizer metadata can be large; bound parser work without reading multi-GB model weights.
    private const long MaximumTableBytes = 128L << 20;

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

    public static Task<Result> DownloadAsync(ModelDownload model, Action<string> log,
        CancellationToken ct, Action<long, long?>? progress = null) =>
        DownloadCoreAsync(model, model.Url, Path.Combine(TargetDirectory, model.FileName), log, ct, progress);

    // Explicit URL/destination enable loopback tests without changing the catalog or global config.
    internal static async Task<Result> DownloadCoreAsync(ModelDownload model, string url, string destination,
        Action<string> log, CancellationToken ct, Action<long, long?>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        if (model.Bytes <= 0) throw new ArgumentOutOfRangeException(nameof(model), "模型期望字节数必须为正数。");
        if (File.Exists(destination))
        {
            var reusable = false;
            try { VerifyGguf(destination, model.Bytes, ct); reusable = true; }
            catch (InvalidDataException) { } // Only bad data triggers a replacement attempt, not IO failures.
            ct.ThrowIfCancellationRequested();
            if (reusable)
            {
                log($"已存在同大小且 GGUF 文件头/描述表通过检查的文件，跳过下载：{destination}");
                return new Result(destination, model.Bytes);
            }
            log($"已存在同名文件但长度或 GGUF 文件头/描述表检查失败，将在下载验证通过后替换：{destination}");
        }
        CheckFreeSpace(model, Path.GetDirectoryName(Path.GetFullPath(destination))!);
        // EngineDownloadService owns the unique .part file and cleans only that file on failure.
        // Validate inside its publication callback, never after it has replaced the user's target.
        await EngineDownloadService.DownloadCoreAsync(url, destination, sha256: null, log, ct,
            (received, total) =>
            {
                if (received > model.Bytes || (total.HasValue && total.Value != model.Bytes))
                    throw new InvalidDataException($"模型下载长度不匹配：期望 {model.Bytes} 字节。");
                progress?.Invoke(received, total);
            },
            async (staged, target, token) =>
            {
                VerifyGguf(staged, model.Bytes, token);
                await EngineDownloadService.PublishVerifiedAsync(staged, target, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return new Result(destination, model.Bytes);
    }

    private static void CheckFreeSpace(ModelDownload model, string directory)
    {
        Directory.CreateDirectory(directory);
        var root = Path.GetPathRoot(directory);
        if (string.IsNullOrEmpty(root)) return;
        var drive = new DriveInfo(root);
        var free = drive.AvailableFreeSpace;
        // Fail before a multi-gigabyte download, not after: keep 2 GiB of headroom for the system.
        if (free - (2L << 30) < model.Bytes)
            throw new IOException($"磁盘空间不足：{root} 仅剩 {EngineDownloadService.FormatBytes(free)}，" +
                $"下载 {model.DisplayName} 需要约 {EngineDownloadService.FormatBytes(model.Bytes)} 加 2 GiB 余量。");
    }

    /// <summary>
    /// Checks GGUF v2/v3 metadata and tensor descriptors with the shared bounded parser, without
    /// the metadata cache. Does not validate tensor weight bytes, tensor offsets or a digest.
    /// This header-only API cannot detect a truncated weight payload; downloads additionally
    /// require the catalog's exact byte count. Failure never deletes the inspected file.
    /// </summary>
    public static void VerifyGguf(string path) => VerifyGguf(path, expectedBytes: null, CancellationToken.None);

    private static void VerifyGguf(string path, long? expectedBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        if (expectedBytes.HasValue && stream.Length != expectedBytes.Value)
            throw new InvalidDataException($"模型文件长度不匹配：期望 {expectedBytes}，实际 {stream.Length} 字节。");
        try
        {
            using var bounded = new TableReadStream(stream, ct);
            var metadata = GgufService.Read(bounded);
            if (metadata.TensorCount == 0) throw new InvalidDataException("GGUF 模型未包含张量描述表。");
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is EndOfStreamException or OverflowException)
        {
            throw new InvalidDataException($"GGUF 文件头或描述表无效或被截断：{Path.GetFileName(path)}。", ex);
        }
    }

    // A read/seek ceiling also bounds nested arrays and bulk string-array read-ahead in the shared
    // parser. Preserve genuine filesystem IO exceptions; only format/EOF errors mean bad data.
    private sealed class TableReadStream(Stream source, CancellationToken ct) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => Math.Min(source.Length, MaximumTableBytes);
        public override long Position { get => source.Position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ct.ThrowIfCancellationRequested();
            if (buffer.Length == 0) return 0;
            var remaining = Length - Position;
            if (remaining <= 0 && source.Length > Length)
                throw new InvalidDataException("GGUF 文件头/描述表超出 128 MiB 检查上限。");
            return source.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            ct.ThrowIfCancellationRequested();
            var position = checked(origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            });
            if (position < 0 || position > Length) throw new InvalidDataException("GGUF 描述表偏移越界。");
            return source.Seek(position, SeekOrigin.Begin);
        }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Explicit caller-requested deletion only; download failures clean their owned staging, not the target.</summary>
    public static void RemoveInvalid(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
