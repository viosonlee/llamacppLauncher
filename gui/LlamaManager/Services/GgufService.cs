using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace LlamaManager.Services;

/// <summary>
/// 只解析 GGUF 文件头部的 KV 元数据（不读取数十 GB 的张量数据），
/// 用于可靠地区分 Dense / MoE 模型。
/// </summary>
public static class GgufService
{
    private static readonly ConcurrentDictionary<string, (bool IsMoE, string Arch)> Cache = new();

    private const uint GgufMagic = 0x46554747; // 'GGUF'
    private const long MaxKeyLen = 4 * 1024;
    private const long MaxStringLen = 64 * 1024 * 1024;

    /// <summary>
    /// 返回 true = MoE, false = Dense, null = 无法解析（调用方应回退到文件名启发式）。
    /// </summary>
    public static bool? TryDetectIsMoe(string fullPath)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists || fi.Length < 24) return null;

            var key = $"{fullPath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            if (Cache.TryGetValue(key, out var hit)) return hit.IsMoE;

            var parsed = ParseHeader(fi);
            if (parsed is null) return null;

            Cache[key] = parsed.Value;
            return parsed.Value.IsMoE;
        }
        catch
        {
            return null;
        }
    }

    public static string? GetArchitecture(string fullPath)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return null;
            var key = $"{fullPath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            if (Cache.TryGetValue(key, out var hit)) return hit.Arch;
            ParseHeader(fi);
            return Cache.TryGetValue(key, out var hit2) ? hit2.Arch : null;
        }
        catch
        {
            return null;
        }
    }

    private static (bool IsMoE, string Arch)? ParseHeader(FileInfo fi)
    {
        using var fs = fi.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

        if (br.ReadUInt32() != GgufMagic) return null;
        var version = br.ReadUInt32();
        if (version < 2 || version > 3) return null;

        _ = br.ReadUInt64(); // tensor count
        var kvCount = br.ReadUInt64();
        if (kvCount == 0 || kvCount > 10_000_000) return null;

        var arch = string.Empty;
        var moe = false;

        for (ulong i = 0; i < kvCount; i++)
        {
            var keyName = ReadGgufString(br);
            if (keyName is null) return null;

            // llama.cpp 写入顺序: general.* → 架构超参(含 expert_count) → tokenizer.*
            // 碰到 tokenizer 即可判定前面没有 MoE 标记。
            if (keyName.StartsWith("tokenizer.", StringComparison.Ordinal)) break;

            var valueType = br.ReadUInt32();
            var value = ReadValue(br, valueType, out var arrayTooBig);
            if (arrayTooBig) return null;

            if (keyName == "general.architecture" && value is string s)
                arch = s;

            if (IsMoeMarker(keyName, value))
            {
                moe = true;
                break;
            }
        }

        return (moe || ArchIndicatesMoe(arch), arch);
    }

    private static bool IsMoeMarker(string key, object? value)
    {
        // 实测键名: llama.expert_count / qwen35moe.expert_count / deepseek2.n_expert 等
        if (key.EndsWith("expert_count", StringComparison.OrdinalIgnoreCase))
            return ToLong(value) > 1;

        // 出现即为 MoE（Dense 模型没有该键）
        if (key.EndsWith("expert_used_count", StringComparison.OrdinalIgnoreCase))
            return true;

        if (key.EndsWith("n_expert", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("n_experts", StringComparison.OrdinalIgnoreCase))
            return ToLong(value) > 1;

        return false;
    }

    private static bool ArchIndicatesMoe(string arch)
    {
        if (string.IsNullOrEmpty(arch)) return false;
        if (arch.Contains("moe", StringComparison.OrdinalIgnoreCase)) return true;
        return arch.Equals("mixtral", StringComparison.OrdinalIgnoreCase)
            || arch.Equals("dbrx", StringComparison.OrdinalIgnoreCase)
            || arch.Equals("jamba", StringComparison.OrdinalIgnoreCase)
            || arch.StartsWith("deepseek2", StringComparison.OrdinalIgnoreCase)
            || arch.StartsWith("deepseek3", StringComparison.OrdinalIgnoreCase);
    }

    private static long ToLong(object? value) => value switch
    {
        byte b => b,
        sbyte sb => sb,
        ushort u => u,
        short s => s,
        uint u => u,
        int i => i,
        ulong u => u > long.MaxValue ? long.MaxValue : (long)u,
        long l => l,
        _ => 0
    };

    private static string? ReadGgufString(BinaryReader br)
    {
        var len = br.ReadUInt64();
        if (len == 0 || len > MaxKeyLen) return null;
        var bytes = br.ReadBytes((int)len);
        if (bytes.Length != (int)len) return null;
        return Encoding.UTF8.GetString(bytes);
    }

    private static object? ReadValue(BinaryReader br, uint type, out bool tooBig)
    {
        tooBig = false;
        switch (type)
        {
            case 0: return br.ReadByte();
            case 1: return br.ReadSByte();
            case 2: return br.ReadUInt16();
            case 3: return br.ReadInt16();
            case 4: return br.ReadUInt32();
            case 5: return br.ReadInt32();
            case 6: return br.ReadSingle();
            case 7: return br.ReadBoolean();
            case 8:
            {
                var len = br.ReadUInt64();
                if (len > MaxStringLen) { tooBig = true; return null; }
                var bytes = br.ReadBytes((int)len);
                if (bytes.Length != (int)len) { tooBig = true; return null; }
                return Encoding.UTF8.GetString(bytes);
            }
            case 9: // array
            {
                var elemType = br.ReadUInt32();
                var count = br.ReadUInt64();
                if (count > 100_000_000) { tooBig = true; return null; }

                if (elemType == 8) // string[]：必须逐项读取
                {
                    for (ulong i = 0; i < count; i++)
                    {
                        var len = br.ReadUInt64();
                        if (len > MaxStringLen) { tooBig = true; return null; }
                        br.BaseStream.Seek((long)len, SeekOrigin.Current);
                    }
                    return null;
                }

                var elemSize = ElementSize(elemType);
                if (elemSize <= 0) { tooBig = true; return null; }
                br.BaseStream.Seek((long)count * elemSize, SeekOrigin.Current);
                return null;
            }
            case 10: return br.ReadUInt64();
            case 11: return br.ReadInt64();
            case 12: return br.ReadDouble();
            default: tooBig = true; return null;
        }
    }

    private static int ElementSize(uint type) => type switch
    {
        0 or 1 or 7 => 1,
        2 or 3 => 2,
        4 or 5 or 6 => 4,
        10 or 11 or 12 => 8,
        _ => -1
    };
}
