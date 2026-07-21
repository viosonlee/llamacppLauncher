namespace LlamaManager.Models;

public class ModelInfo
{
    public string FullName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public string SizeFormatted => FormatBytes(SizeBytes);

    public bool IsMoE =>
        FileName.Contains("A3B", StringComparison.OrdinalIgnoreCase) ||
        FileName.Contains("moe", StringComparison.OrdinalIgnoreCase) ||
        FileName.Contains("mixture.of.experts", StringComparison.OrdinalIgnoreCase);

    public string ModelType => IsMoE ? "MoE" : "Standard";

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30)
            return $"{bytes / (1L << 30):N2} GB";
        if (bytes >= 1L << 20)
            return $"{bytes / (1L << 20):N1} MB";
        return $"{bytes:N0} B";
    }
}
