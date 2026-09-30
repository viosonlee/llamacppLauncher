using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace LlamaManager.Services;

/// <summary>
/// 便宜的文件签名：长度 + 头/尾各 1MB 的 SHA256。
/// 比全文件 hash 快几个数量级（大文件约 &lt;50ms），足以在移动、改名、换盘符后识别同一个文件。
/// </summary>
public static class FileSignature
{
    private const int Chunk = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>返回形如 "14658690080:abcd...ef01..." 的签名；失败返回 null。</summary>
    public static string? Compute(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= 0) return null;

            var cacheKey = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            if (Cache.TryGetValue(cacheKey, out var hit)) return hit;

            string sig;
            using (var fs = fi.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fi.Length <= Chunk * 2L)
                {
                    sig = $"{fi.Length}:{HashRange(fs, 0, fi.Length)}";
                }
                else
                {
                    var head = HashRange(fs, 0, Chunk);
                    var tail = HashRange(fs, fi.Length - Chunk, Chunk);
                    sig = $"{fi.Length}:{head[..32]}{tail[..32]}";
                }
            }

            Cache[cacheKey] = sig;
            return sig;
        }
        catch
        {
            return null;
        }
    }

    private static string HashRange(FileStream fs, long offset, long count)
    {
        fs.Seek(offset, SeekOrigin.Begin);
        using var sha = SHA256.Create();
        var buf = new byte[Chunk];
        long remaining = count;
        while (remaining > 0)
        {
            var read = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
            if (read <= 0) break;
            sha.TransformBlock(buf, 0, read, null, 0);
            remaining -= read;
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}
