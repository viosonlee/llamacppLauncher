using System.IO;
using System.Text.Json;
using LlamaManager.Models;

namespace LlamaManager.Services;

public static class ConfigService
{
    private static readonly string ManagerRoot;
    private static readonly string LlamaCppRoot;
    private static readonly string LogsDir;
    private static readonly string ConfigPath;

    static ConfigService()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        ManagerRoot = FindParentWithFile(exeDir, "llama-manager.ps1", maxDepth: 8) ?? exeDir;

        LlamaCppRoot = FindParentWithFile(ManagerRoot, "llama-server.exe", maxDepth: 4)
                    ?? FindParentWithFile(exeDir, "llama-server.exe", maxDepth: 4)
                    ?? ManagerRoot;

        LogsDir = Path.Combine(ManagerRoot, "logs");
        Directory.CreateDirectory(LogsDir);

        ConfigPath = Path.Combine(ManagerRoot, "llama-manager.config.json");
    }

    private static string? FindParentWithFile(string startDir, string targetFile, int maxDepth)
    {
        var dir = startDir;
        for (int i = 0; i < maxDepth; i++)
        {
            if (File.Exists(Path.Combine(dir, targetFile)))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir)
                break;
            dir = parent;
        }
        return null;
    }

    public static string GetManagerRoot() => ManagerRoot;
    public static string GetLlamaCppRoot() => LlamaCppRoot;
    public static string GetRoot() => LlamaCppRoot;
    public static string GetPrismServerPath() => Path.Combine(LlamaCppRoot, "engines", "prism", "llama-server.exe");
    public static bool HasPrismServer() => File.Exists(GetPrismServerPath());

    public static bool IsPrismModel(string? modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return false;
        var name = Path.GetFileName(modelPath);
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"bonsai|ptq|ternary", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static string GetLlamaServerPath(string? modelPath = null)
    {
        if (IsPrismModel(modelPath))
        {
            var prismPath = GetPrismServerPath();
            if (File.Exists(prismPath))
                return prismPath;
        }
        return Path.Combine(LlamaCppRoot, "llama-server.exe");
    }
    public static string GetLogPath() => Path.Combine(LogsDir, "llama-manager.log");
    public static string GetMainStdoutPath() => Path.Combine(LogsDir, "llama-server-main.stdout.log");
    public static string GetMainStderrPath() => Path.Combine(LogsDir, "llama-server-main.stderr.log");
    public static string GetEmbeddingStdoutPath() => Path.Combine(LogsDir, "llama-server-embedding.stdout.log");
    public static string GetEmbeddingStderrPath() => Path.Combine(LogsDir, "llama-server-embedding.stderr.log");

    public static ManagerConfig ReadConfig()
    {
        if (!File.Exists(ConfigPath))
            return new ManagerConfig { CreatedAt = DateTime.Now.ToString("o") };

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<ManagerConfig>(json);
            config ??= new ManagerConfig();
            config.ModelPresets ??= [];
            return config;
        }
        catch
        {
            return new ManagerConfig();
        }
    }

    public static void SaveConfig(ManagerConfig config)
    {
        try
        {
            if (string.IsNullOrEmpty(config.CreatedAt))
                config.CreatedAt = DateTime.Now.ToString("o");
            config.UpdatedAt = DateTime.Now.ToString("o");

            // Save current model's preset
            SaveModelPreset(config, config.ModelPath, config.Args, config.Port);

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }

    public static ModelPreset? GetModelPreset(ManagerConfig config, string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;
        var key = NormalizePath(modelPath);
        if (config.ModelPresets.TryGetValue(key, out var preset))
            return preset;

        // 路径未命中：尝试把孤儿预设（原文件已移动/改名）重绑定到该模型
        if (RebindOrphanPresets(config, [modelPath], save: true) > 0
            && config.ModelPresets.TryGetValue(key, out preset))
            return preset;

        return null;
    }

    public static void SaveModelPreset(ManagerConfig config, string modelPath, string args, int port)
    {
        if (string.IsNullOrEmpty(modelPath)) return;
        var key = NormalizePath(modelPath);
        config.ModelPresets[key] = new ModelPreset
        {
            Args = args,
            Port = port,
            LastUsed = DateTime.Now.ToString("o"),
            Signature = FileSignature.Compute(modelPath)
        };
    }

    /// <summary>
    /// 把 key 指向已不存在文件的预设（模型被移动/改名/删除）重新绑定到现有的模型文件。
    /// 匹配依据：文件签名（最强）→ 文件名 + 尾部目录段相似度。
    /// </summary>
    public static int RebindOrphanPresets(ManagerConfig config, IEnumerable<string> modelPaths, bool save)
    {
        if (config.ModelPresets == null || config.ModelPresets.Count == 0) return 0;

        var models = modelPaths
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (models.Count == 0) return 0;

        var orphans = config.ModelPresets
            .Where(kv => string.IsNullOrEmpty(kv.Key) || !File.Exists(kv.Key))
            .ToList();
        if (orphans.Count == 0) return 0;

        var needSignature = orphans.Any(kv => !string.IsNullOrEmpty(kv.Value.Signature));
        var signatures = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (needSignature)
            foreach (var m in models)
                signatures[m] = FileSignature.Compute(m);

        var moved = 0;
        foreach (var model in models)
        {
            var targetKey = NormalizePath(model);
            if (config.ModelPresets.ContainsKey(targetKey)) continue;

            var sig = needSignature && signatures.TryGetValue(model, out var s) ? s : null;
            string? bestKey = null;
            ModelPreset? bestPreset = null;
            var bestScore = 0;

            foreach (var kv in orphans)
            {
                if (!config.ModelPresets.ContainsKey(kv.Key)) continue; // 已被其他模型占用（已迁走）
                var score = ScoreOrphan(kv.Key, kv.Value, model, sig);
                if (score > bestScore
                    || (score == bestScore && score > 0
                        && string.CompareOrdinal(kv.Value.LastUsed, bestPreset?.LastUsed) > 0))
                {
                    bestScore = score;
                    bestKey = kv.Key;
                    bestPreset = kv.Value;
                }
            }

            if (bestKey == null || bestPreset == null) continue;

            config.ModelPresets.Remove(bestKey);
            bestPreset.Signature = FileSignature.Compute(model) ?? bestPreset.Signature;
            config.ModelPresets[targetKey] = bestPreset;
            moved++;
            LogService.Write($"检测到模型文件变更，已恢复预设: {Path.GetFileName(model)}");
        }

        // 给还没有签名的预设补签名（仅首次，后续直接命中缓存）
        var upgraded = 0;
        foreach (var model in models)
        {
            if (!config.ModelPresets.TryGetValue(NormalizePath(model), out var p)
                || !string.IsNullOrEmpty(p.Signature))
                continue;
            var s = FileSignature.Compute(model);
            if (s != null)
            {
                p.Signature = s;
                upgraded++;
            }
        }

        if ((moved > 0 || upgraded > 0) && save)
        {
            try { SaveConfig(config); } catch { }
        }
        return moved;
    }

    private static int ScoreOrphan(string orphanKey, ModelPreset preset, string modelPath, string? signature)
    {
        var nameScore = 0;
        if (!string.IsNullOrEmpty(preset.Signature)
            && signature != null
            && string.Equals(preset.Signature, signature, StringComparison.Ordinal))
        {
            nameScore = 1000; // 内容签名完全一致：绝对可信
        }
        else
        {
            var orphanName = Path.GetFileName(orphanKey);
            if (string.IsNullOrEmpty(orphanName)
                || !string.Equals(orphanName, Path.GetFileName(modelPath), StringComparison.OrdinalIgnoreCase))
                return -1;
            nameScore = 1;
        }

        return nameScore + TrailingSegmentScore(orphanKey, modelPath);
    }

    /// <summary>从尾部开始比较目录段（不含文件名）的相同个数。</summary>
    private static int TrailingSegmentScore(string a, string b)
    {
        var sa = a.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var sb = b.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        int i = sa.Length - 2, j = sb.Length - 2, score = 0;
        while (i >= 0 && j >= 0)
        {
            if (!string.Equals(sa[i], sb[j], StringComparison.OrdinalIgnoreCase)) break;
            score++;
            i--;
            j--;
        }
        return score;
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).ToLowerInvariant();
}
