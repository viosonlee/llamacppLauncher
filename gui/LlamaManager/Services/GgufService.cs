using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LlamaManager.Services;

public sealed record GgufMetadata
{
    public string Architecture { get; init; } = "";
    public int LayerCount { get; init; }
    public int ContextLength { get; init; }
    public int EmbeddingLength { get; init; }
    public int ExpertCount { get; init; }
    public int ExpertUsedCount { get; init; }
    public int AttentionHeads { get; init; }
    public int KvHeads { get; init; }
    public int KeyLength { get; init; }
    public int ValueLength { get; init; }
    public bool IsMoE { get; init; }
    public bool HasHadamard { get; init; }
    public bool HasMtp { get; init; }
    public IReadOnlyDictionary<uint, int> TensorTypes { get; init; } = new Dictionary<uint, int>();
    /// <summary>Types of the tensors the Strata packer and engine inspect by name (roles, embedding, head, router).</summary>
    public IReadOnlyDictionary<string, uint> TensorFormats { get; init; } = new Dictionary<string, uint>();
    public bool RequiresPrism => HasHadamard || TensorTypes.ContainsKey(142) || TensorTypes.ContainsKey(143);
    public bool HasTq1 => TensorTypes.ContainsKey(34);
    public string CompatibilityNote
    {
        get
        {
            var notes = new List<string>();
            if (HasTq1) notes.Add("含 TQ1_0 张量；部分引擎的 CUDA 后端不支持此类型，可能回退 CPU。请以加载日志中的 GPU 权重为准。");
            else if (RequiresPrism) notes.Add("含 Prism 专用张量或 Hadamard 元数据，需要 Prism 引擎。");
            if (HasMtp) notes.Add("含内置 MTP 头，已在常用参数中提供投机解码开关。");
            return string.Join("\n", notes);
        }
    }
}

/// <summary>Reads bounded metadata and tensor descriptors only, never model weights.</summary>
public static class GgufService
{
    private sealed record Entry(long Length, long Modified, GgufMetadata? Metadata);
    private static readonly ConcurrentDictionary<string, Entry> Cache = new();
    [ThreadStatic] private static byte[]? _skipScratch;
    [ThreadStatic] private static byte[]? _bulkScratch;

    public static GgufMetadata? TryRead(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var key = PathIdentity.Normalize(path);
            if (Cache.TryGetValue(key, out var hit) && hit.Length == file.Length && hit.Modified == file.LastWriteTimeUtc.Ticks)
                return hit.Metadata;
            GgufMetadata? result;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                result = Read(stream);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException) { result = null; }
            Cache[key] = new Entry(file.Length, file.LastWriteTimeUtc.Ticks, result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    public static bool? TryDetectIsMoe(string path) => TryRead(path)?.IsMoE;
    public static string? GetArchitecture(string path) => TryRead(path)?.Architecture;

    /// <summary>
    /// Reads metadata from every shard of a split GGUF. Scalar metadata lives in the
    /// first shard; tensor descriptors are distributed, so role formats need all shards.
    /// </summary>
    public static GgufMetadata? TryReadShards(string path)
    {
        var single = TryRead(path);
        if (single == null) return null;
        var match = Regex.Match(Path.GetFullPath(path), @"^(.*)-(?<part>\d{5})-of-(?<total>\d{5})\.gguf$", RegexOptions.IgnoreCase);
        if (!match.Success) return single;
        var count = int.Parse(match.Groups["total"].Value);
        if (match.Groups["part"].Value != "00001" || count is < 2 or > 1024) return single;
        var types = new Dictionary<uint, int>(single.TensorTypes);
        var formats = new Dictionary<string, uint>(single.TensorFormats);
        for (var i = 2; i <= count; i++)
        {
            var shard = TryRead($"{match.Groups[1].Value}-{i:D5}-of-{count:D5}.gguf");
            if (shard == null) return null;
            foreach (var (t, n) in shard.TensorTypes) types[t] = types.GetValueOrDefault(t) + n;
            foreach (var (name, t) in shard.TensorFormats) formats.TryAdd(name, t);
        }
        return single with { TensorTypes = types, TensorFormats = formats };
    }

    public static GgufMetadata Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x46554747 || reader.ReadUInt32() is not (2 or 3))
            throw new InvalidDataException("不支持的 GGUF 文件头。");
        var tensors = reader.ReadUInt64();
        var count = reader.ReadUInt64();
        if (tensors > 1_000_000 || count > 1_000_000) throw new InvalidDataException("GGUF 项目过多。");
        var architecture = "";
        var hadamard = false;
        var moe = false;
        var nextn = false;
        var scalars = new Dictionary<string, long>();
        for (ulong i = 0; i < count; i++)
        {
            var key = ReadString(reader, 4096);
            var type = reader.ReadUInt32();
            if (key == "general.architecture" && type == 8)
            {
                architecture = ReadString(reader, 4096);
                continue;
            }
            hadamard |= key.StartsWith("prism.hadamard.", StringComparison.Ordinal);
            var scalar = ReadValue(reader, type);
            if (scalar.HasValue)
            {
                scalars[key] = scalar.Value;
                if (key.EndsWith(".expert_count") || key.EndsWith(".n_expert") || key.EndsWith(".n_experts"))
                    moe |= scalar.Value > 1;
                if (key.EndsWith(".expert_used_count")) moe |= scalar.Value > 0;
                if (key.EndsWith(".nextn_predict_layers")) nextn |= scalar.Value > 0;
            }
        }
        var types = new Dictionary<uint, int>();
        var formats = new Dictionary<string, uint>();
        for (ulong i = 0; i < tensors; i++)
        {
            var tensorName = ReadString(reader, 4096);
            nextn |= tensorName.Contains(".nextn.", StringComparison.Ordinal);
            var dimensions = reader.ReadUInt32();
            if (dimensions is < 1 or > 4) throw new InvalidDataException("GGUF 张量维度无效。");
            for (var d = 0; d < dimensions; d++)
                if (reader.ReadUInt64() == 0) throw new InvalidDataException("GGUF 张量长度为零。");
            var type = reader.ReadUInt32();
            _ = reader.ReadUInt64();
            types[type] = types.GetValueOrDefault(type) + 1;
            if (RoleTensor.IsMatch(tensorName)) formats[tensorName] = type;
        }
        int Number(string suffix) => (int)Math.Clamp(scalars.GetValueOrDefault(architecture + "." + suffix), 0, int.MaxValue);
        return new GgufMetadata
        {
            Architecture = architecture, LayerCount = Number("block_count"), ContextLength = Number("context_length"),
            EmbeddingLength = Number("embedding_length"), AttentionHeads = Number("attention.head_count"),
            ExpertCount = Number("expert_count"), ExpertUsedCount = Number("expert_used_count"),
            KvHeads = Number("attention.head_count_kv"), KeyLength = Number("attention.key_length"),
            ValueLength = Number("attention.value_length"), IsMoE = moe || architecture.Contains("moe", StringComparison.OrdinalIgnoreCase),
            HasHadamard = hadamard, HasMtp = nextn, TensorTypes = types, TensorFormats = formats
        };
    }

    /// <summary>Tensor names the Strata engine and packer inspect by role (experts, shared experts, embedding, head, router).</summary>
    private static readonly Regex RoleTensor =
        new(@"^(token_embd|output)\.weight$|^blk\.\d+\.ffn_(gate|up|down)(_exps|_shexp)?(_w\d+)?\.weight$|^blk\.\d+\.ffn_gate_inp(_shexp)?\.weight$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string ReadString(BinaryReader reader, int maximum)
    {
        var length = reader.ReadUInt64();
        if (length > (ulong)maximum || length > (ulong)(reader.BaseStream.Length - reader.BaseStream.Position))
            throw new InvalidDataException("GGUF 字符串长度无效。");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    /// <summary>
    /// Advances past an array of strings without materialising it. Tokenizer
    /// vocabularies hold hundreds of thousands of short strings, so walking the
    /// lengths through a 64 KiB buffer turns a per-string virtual call into a
    /// per-buffer one.
    /// </summary>
    private static void SkipStringArray(BinaryReader reader, ulong count)
    {
        var buffer = _bulkScratch ??= new byte[1 << 16];
        var streamLength = reader.BaseStream.Length;
        var start = 0;
        var end = 0;
        for (ulong i = 0; i < count; i++)
        {
            if (end - start < 8)
            {
                if (start > 0) { Buffer.BlockCopy(buffer, start, buffer, 0, end - start); end -= start; start = 0; }
                while (end < 8)
                {
                    var read = reader.Read(buffer, end, buffer.Length - end);
                    if (read <= 0) throw new EndOfStreamException("GGUF 文件被截断。");
                    end += read;
                }
            }
            var stringLength = BitConverter.ToUInt64(buffer, start);
            start += 8;
            if (stringLength > (ulong)streamLength) throw new InvalidDataException("GGUF 字符串长度无效。");
            var remaining = (long)stringLength;
            while (remaining > 0)
            {
                if (start == end)
                {
                    start = 0; end = 0;
                    var read = reader.Read(buffer, 0, buffer.Length);
                    if (read <= 0) throw new EndOfStreamException("GGUF 文件被截断。");
                    end = read;
                }
                var take = (int)Math.Min(end - start, remaining);
                start += take;
                remaining -= take;
            }
        }
        // The last refill read past the array into the next metadata entry; hand
        // those bytes back so the caller keeps parsing from the right offset.
        if (end > start) reader.BaseStream.Seek(-(end - start), SeekOrigin.Current);
    }

    private static void Skip(BinaryReader reader, ulong length)
    {
        if (length > (ulong)(reader.BaseStream.Length - reader.BaseStream.Position))
            throw new EndOfStreamException("GGUF 文件被截断。");
        // Read forward instead of Seek: tokenizer metadata holds arrays with
        // hundreds of thousands of short strings, and every Seek discards the
        // stream buffer, so seeking once per string made reads ~100x slower.
        // The scratch buffer is reused per thread to avoid pool traffic per string.
        var remaining = (long)length;
        var scratch = _skipScratch ??= new byte[8192];
        while (remaining > 0)
        {
            var read = reader.Read(scratch, 0, (int)Math.Min(scratch.Length, remaining));
            if (read <= 0) throw new EndOfStreamException("GGUF 文件被截断。");
            remaining -= read;
        }
    }

    private static long? ReadValue(BinaryReader reader, uint type, int depth = 0)
    {
        switch (type)
        {
            case 0: return reader.ReadByte();
            case 1: return reader.ReadSByte();
            case 2: return reader.ReadUInt16();
            case 3: return reader.ReadInt16();
            case 4: return reader.ReadUInt32();
            case 5: return reader.ReadInt32();
            case 6: reader.ReadSingle(); return null;
            case 7: return reader.ReadBoolean() ? 1 : 0;
            case 8: Skip(reader, reader.ReadUInt64()); return null;
            case 9:
                if (depth >= 4) throw new InvalidDataException("GGUF 数组嵌套过深。");
                var element = reader.ReadUInt32();
                var count = reader.ReadUInt64();
                if (count > 10_000_000) throw new InvalidDataException("GGUF 数组过大。");
                var size = element switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => 0 };
                if (size > 0) Skip(reader, checked(count * (ulong)size));
                else if (element == 8) SkipStringArray(reader, count);
                else for (ulong i = 0; i < count; i++) ReadValue(reader, element, depth + 1);
                return null;
            case 10: return (long)Math.Min(reader.ReadUInt64(), long.MaxValue);
            case 11: return reader.ReadInt64();
            case 12: reader.ReadDouble(); return null;
            default: throw new InvalidDataException("未知 GGUF 元数据类型。");
        }
    }
}
