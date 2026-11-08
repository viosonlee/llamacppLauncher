using System.IO;

namespace LlamaManager.Services;

/// <summary>
/// Discovers and pairs auxiliary model files (multimodal projector and MTP/draft models)
/// residing in the same directory as the primary GGUF model.
/// </summary>
public static class ModelPairingService
{
    /// <summary>
    /// True for files that only ever support a primary model (projector or draft/MTP weights).
    /// The model list and the pairing rules must agree: listing a bare draft file as a startable
    /// model lets a user select a file that cannot answer on its own, while pairing still needs it.
    /// </summary>
    public static bool IsAuxiliaryWeight(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var name = Path.GetFileName(fileName);
        if (!name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return false;
        return name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("mtp-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("draft-", StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>
    /// Searches for a matching multimodal projector file (e.g., mmproj-Q8_0.gguf) in the model's directory.
    /// Prefers Q8_0, then BF16, then F16, then the first found.
    /// </summary>
    public static string? FindPairedMmproj(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return null;
        var dir = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        var candidates = Directory.EnumerateFiles(dir, "*.gguf")
            .Where(f => Path.GetFileName(f).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
            // Deterministic order: an unordered enumeration made the chosen projector vary.
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        return candidates.FirstOrDefault(f => f.Contains("Q8_0", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(f => f.Contains("BF16", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(f => f.Contains("F16", StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];
    }

    /// <summary>
    /// Searches for a matching external MTP or speculative draft model (e.g., mtp-*.gguf) in the model's directory.
    /// </summary>
    public static string? FindPairedDraftModel(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return null;
        var dir = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        var candidates = Directory.EnumerateFiles(dir, "*.gguf")
            .Where(f => Path.GetFileName(f).StartsWith("mtp-", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(f).StartsWith("draft-", StringComparison.OrdinalIgnoreCase))
            // Directory order is not stable, so two drafts used to pair unpredictably.
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An MTP block is the stronger pairing when both kinds sit in the directory.
        return candidates.FirstOrDefault(f => Path.GetFileName(f).StartsWith("mtp-", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }
}
