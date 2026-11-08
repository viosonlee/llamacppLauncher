using System.IO;
using System.Security.Cryptography;

namespace LlamaManager.Services;

/// <summary>
/// 便宜的文件签名：长度 + 头/尾各 1MB 的 SHA256。
/// 用于快速匹配移动后的文件；不是全文件完整性校验。
/// </summary>
public static class FileSignature
{
    private static readonly FileSignatureComputer Computer = new();

    /// <summary>返回形如 "14658690080:abcd...ef01..." 的签名；失败返回 null。</summary>
    public static string? Compute(string path) => Computer.Compute(path);
}

/// <summary>One observed version per normalized path, bounded by LRU; sampling never holds the cache lock.</summary>
internal sealed class FileSignatureComputer
{
    private const int Chunk = 1024 * 1024;
    private readonly int _capacity;
    private readonly Action<string>? _beforeRead;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _recent = new();
    private readonly record struct FileState(long Length, long Modified, long Created);
    private sealed record Entry(string Path, FileState State, Lazy<string?> Value);

    internal FileSignatureComputer(int capacity = 256, Action<string>? beforeRead = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _beforeRead = beforeRead;
    }

    internal int CacheCount { get { lock (_gate) return _entries.Count; } }

    public string? Compute(string path)
    {
        try
        {
            path = PathIdentity.Normalize(path);
            var state = Observe(path);
            if (state == null) return null;
            LinkedListNode<Entry> selected;
            lock (_gate)
            {
                if (_entries.TryGetValue(path, out var cached) && cached.Value.State == state.Value)
                {
                    selected = cached;
                    _recent.Remove(cached);
                    _recent.AddLast(cached);
                }
                else
                {
                    if (cached != null) { _recent.Remove(cached); _entries.Remove(path); }
                    var file = path;
                    var expected = state.Value;
                    selected = _recent.AddLast(new Entry(file, expected, new Lazy<string?>(() => ReadStable(file, expected))));
                    _entries[path] = selected;
                    while (_entries.Count > _capacity)
                    {
                        var oldest = _recent.First!;
                        _entries.Remove(oldest.Value.Path);
                        _recent.RemoveFirst();
                    }
                }
            }
            try
            {
                var signature = selected.Value.Value.Value; // Shared same-version work; outside _gate.
                if (signature == null) RemoveIfCurrent(selected);
                return signature;
            }
            catch { RemoveIfCurrent(selected); throw; }
        }
        catch { return null; } // A sampling failure must not block model selection or poison a future retry.
    }

    private void RemoveIfCurrent(LinkedListNode<Entry> selected)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(selected.Value.Path, out var current) && ReferenceEquals(current, selected))
            {
                _entries.Remove(selected.Value.Path);
                _recent.Remove(selected);
            }
        }
    }

    private static FileState? Observe(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists && fi.Length > 0 ? new FileState(fi.Length, fi.LastWriteTimeUtc.Ticks, fi.CreationTimeUtc.Ticks) : null;
    }

    private string? ReadStable(string path, FileState expected)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length != expected.Length) return null;
        _beforeRead?.Invoke(path); // Per-instance test gate, never shared global state.
        string signature;
        if (expected.Length <= Chunk * 2L)
            signature = $"{expected.Length}:{HashRange(fs, 0, expected.Length)}";
        else
        {
            var head = HashRange(fs, 0, Chunk);
            var tail = HashRange(fs, expected.Length - Chunk, Chunk);
            signature = $"{expected.Length}:{head[..32]}{tail[..32]}";
        }
        // Replacement/truncation while sampling is not a reusable observation. This remains a
        // sampled identity, not a full-file integrity guarantee (unchanged metadata can hide edits).
        return fs.Length == expected.Length && Observe(path) == expected ? signature : null;
    }

    private static string HashRange(FileStream fs, long offset, long count)
    {
        fs.Seek(offset, SeekOrigin.Begin);
        using var sha = SHA256.Create();
        var buf = new byte[(int)Math.Min(Chunk, count)];
        long remaining = count;
        while (remaining > 0)
        {
            var read = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
            if (read <= 0) throw new EndOfStreamException("签名计算过程中模型文件被截断。");
            sha.TransformBlock(buf, 0, read, null, 0);
            remaining -= read;
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}
