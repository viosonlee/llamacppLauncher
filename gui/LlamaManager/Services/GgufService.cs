using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace LlamaManager.Services;

public sealed record GgufMetadata
{
    public string Architecture { get; init; } = "";
    public int LayerCount { get; init; }
    public int ContextLength { get; init; }
    public int EmbeddingLength { get; init; }
    public int AttentionHeads { get; init; }
    public int KvHeads { get; init; }
    public int KeyLength { get; init; }
    public int ValueLength { get; init; }
    public bool IsMoE { get; init; }
    public bool HasHadamard { get; init; }
    public bool HasMtp { get; init; }
    public IReadOnlyDictionary<uint, int> TensorTypes { get; init; } = new Dictionary<uint, int>();
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
                using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
        }
        int Number(string suffix) => (int)Math.Clamp(scalars.GetValueOrDefault(architecture + "." + suffix), 0, int.MaxValue);
        return new GgufMetadata
        {
            Architecture = architecture, LayerCount = Number("block_count"), ContextLength = Number("context_length"),
            EmbeddingLength = Number("embedding_length"), AttentionHeads = Number("attention.head_count"),
            KvHeads = Number("attention.head_count_kv"), KeyLength = Number("attention.key_length"),
            ValueLength = Number("attention.value_length"), IsMoE = moe || architecture.Contains("moe", StringComparison.OrdinalIgnoreCase),
            HasHadamard = hadamard, HasMtp = nextn, TensorTypes = types
        };
    }

    private static string ReadString(BinaryReader reader, int maximum)
    {
        var length = reader.ReadUInt64();
        if (length > (ulong)maximum || length > (ulong)(reader.BaseStream.Length - reader.BaseStream.Position))
            throw new InvalidDataException("GGUF 字符串长度无效。");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static void Skip(BinaryReader reader, ulong length)
    {
        if (length > (ulong)(reader.BaseStream.Length - reader.BaseStream.Position))
            throw new EndOfStreamException("GGUF 文件被截断。");
        reader.BaseStream.Seek((long)length, SeekOrigin.Current);
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
                else for (ulong i = 0; i < count; i++) ReadValue(reader, element, depth + 1);
                return null;
            case 10: return (long)Math.Min(reader.ReadUInt64(), long.MaxValue);
            case 11: return reader.ReadInt64();
            case 12: reader.ReadDouble(); return null;
            default: throw new InvalidDataException("未知 GGUF 元数据类型。");
        }
    }
}
