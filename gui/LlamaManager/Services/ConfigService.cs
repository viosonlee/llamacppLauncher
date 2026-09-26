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
        return config.ModelPresets.TryGetValue(key, out var preset) ? preset : null;
    }

    public static void SaveModelPreset(ManagerConfig config, string modelPath, string args, int port)
    {
        if (string.IsNullOrEmpty(modelPath)) return;
        var key = NormalizePath(modelPath);
        config.ModelPresets[key] = new ModelPreset
        {
            Args = args,
            Port = port,
            LastUsed = DateTime.Now.ToString("o")
        };
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).ToLowerInvariant();
}
