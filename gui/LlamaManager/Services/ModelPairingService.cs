using System.IO;

namespace LlamaManager.Services;

/// <summary>
/// Discovers and pairs auxiliary model files (multimodal projector and MTP/draft models)
/// residing in the same directory as the primary GGUF model.
/// </summary>
public static class ModelPairingService
{
    /// <summary>
    /// Auxiliary weights are never valid primary models. The list scan and pairing logic share
    /// this predicate so a renamed helper cannot appear as a selectable model by accident.
    /// </summary>
    public static bool IsAuxiliaryModel(string? path)
    {
        var name = Path.GetFileName(path ?? "");
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
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
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
            .Where(IsAuxiliaryModel)
            .Where(f => !Path.GetFileName(f).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.FirstOrDefault();
    }

    /// <summary>
    /// Adds speculative defaults only when the GGUF metadata distinguishes an external draft from
    /// an MTP head. Explicit --spec-type or -md values are left untouched; unreadable weights are
    /// not guessed, because draft-mtp against an ordinary draft can fail to start the server.
    /// </summary>
    public static string ApplySpeculativeDefaults(string modelPath, string args)
    {
        var parsed = new LauncherArguments(args);
        if (parsed.Has("--spec-type")) return args;

        var explicitDraft = parsed.Get("-md");
        if (!string.IsNullOrWhiteSpace(explicitDraft))
            return ApplyDraftType(args, parsed, explicitDraft, addDraftPath: false);

        if (GgufService.TryRead(modelPath) is { HasMtp: true })
            return parsed.Set("--spec-type", "draft-mtp");

        var pairedDraft = FindPairedDraftModel(modelPath);
        return string.IsNullOrWhiteSpace(pairedDraft)
            ? args
            : ApplyDraftType(args, parsed, pairedDraft, addDraftPath: true);
    }

    private static string ApplyDraftType(
        string original,
        LauncherArguments parsed,
        string draftPath,
        bool addDraftPath)
    {
        var type = GgufService.TryRead(draftPath) switch
        {
            { HasMtp: true } => "draft-mtp",
            { } => "draft-simple",
            _ => null,
        };
        if (type == null) return original;
        var updated = parsed.Set("--spec-type", type);
        return addDraftPath ? new LauncherArguments(updated).Set("-md", draftPath) : updated;
    }
}
