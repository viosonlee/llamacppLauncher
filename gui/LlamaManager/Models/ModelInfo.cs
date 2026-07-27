namespace LlamaManager.Models;

public class ModelInfo
{
    public string FullName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public string SizeFormatted => FormatBytes(SizeBytes);

    public bool IsMoE =>
        FileName.Contains("A3B", StringComparison.OrdinalIgnoreCase) ||
        FileName.Contains("A4B", StringComparison.OrdinalIgnoreCase) ||
        FileName.Contains("moe", StringComparison.OrdinalIgnoreCase) ||
        FileName.Contains("mixture.of.experts", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Whether full GPU offload is feasible.</summary>
    public bool CanFullOffload
    {
        get
        {
            if (IsMoE) return false;
            return SizeBytes <= 6L << 30;
        }
    }

    /// <summary>Recommended -ngl value.</summary>
    public string RecommendedNgl => CanFullOffload ? "all" : "auto";

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30)
            return $"{bytes / (1L << 30):N2} GB";
        if (bytes >= 1L << 20)
            return $"{bytes / (1L << 20):N1} MB";
        return $"{bytes:N0} B";
    }
}
