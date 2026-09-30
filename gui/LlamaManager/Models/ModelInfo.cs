namespace LlamaManager.Models;

public class ModelInfo
{
    public string FullName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string DisplayName => string.IsNullOrEmpty(RelativePath) ? FileName : RelativePath;
    public long SizeBytes { get; set; }

    public string SizeFormatted => FormatBytes(SizeBytes);

    public bool IsMoE => DetectIsMoE(string.IsNullOrEmpty(FullName) ? FileName : FullName);

    /// <summary>
    /// Dense/MoE 判定：优先读取 GGUF 元数据（general.architecture + expert_count），
    /// 读取失败时回退到文件名启发式。
    /// </summary>
    public static bool DetectIsMoE(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (System.IO.Path.HasExtension(path) && System.IO.File.Exists(path))
        {
            var meta = Services.GgufService.TryDetectIsMoe(path);
            if (meta.HasValue) return meta.Value;
        }

        var fn = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(fn)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(
            fn,
            @"(?i)(\bmoe\b|[\-_]moe[\-_.]|mixture[\-_.]of[\-_.]experts|mixtral|dbrx|jamba|deepseek[\-_.]v[23]|deepseek[\-_.]r1(?![\-_.]distill)|ling[\-_.]3|bailing[\-_.]?moe|\bA\d+(\.\d+)?B\b|\b\d+x\d+B\b|glm[\-_.]?4[\-_.]moe|minicpm[\-_.]?moe|gemma[\-_.]?4[\-_.]\d+b[\-_.]a\d+b)");
    }

    public string ModelTypeDisplay => IsMoE ? "MoE" : "Dense";

    /// <summary>Category for grouping in model list.</summary>
    public string Category => SizeBytes switch
    {
        < 1L << 31 => "Small (<2GB)",
        < 3L << 31 => "Medium (2-6GB)",
        < 12L << 30 => "Large (6-12GB)",
        _ => "XL (>12GB)"
    };

    /// <summary>Recommended context length based on model size.</summary>
    public int RecommendedContext => SizeBytes switch
    {
        < 2L << 30 => 40960,
        < 6L << 30 => 20480,
        < 12L << 30 => 10240,
        _ => 8192
    };

    /// <summary>Recommended batch size.</summary>
    public int RecommendedBatch => SizeBytes switch
    {
        < 2L << 30 => 2048,
        < 6L << 30 => 1536,
        _ => 1024
    };

    /// <summary>Recommended ubatch size (always 512).</summary>
    public int RecommendedUBatch => 512;

    /// <summary>Whether full GPU offload is feasible (within ~5GB VRAM limit on typical 6GB card).</summary>
    public bool CanFullOffload => SizeBytes <= (long)(5.0 * 1024 * 1024 * 1024);

    /// <summary>Recommended -ngl value.</summary>
    public string RecommendedNgl => CanFullOffload ? "all" : "auto";

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30)
            return $"{(double)bytes / (1L << 30):N2} GB";
        if (bytes >= 1L << 20)
            return $"{(double)bytes / (1L << 20):N1} MB";
        return $"{bytes:N0} B";
    }
}
