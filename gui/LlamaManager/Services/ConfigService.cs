using System.IO;
using System.Text.Json;
using LlamaManager.Models;

namespace LlamaManager.Services;

public static class ConfigService
{
    private static readonly string AppDir;
    private static readonly string Root;
    private static readonly string ConfigPath;

    static ConfigService()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var appDir = Path.GetDirectoryName(exeDir) ?? exeDir;

        var candidate = appDir;
        for (int i = 0; i < 5; i++)
        {
            if (File.Exists(Path.Combine(candidate, "llama-server.exe")))
            {
                Root = candidate;
                break;
            }
            var parent = Path.GetDirectoryName(candidate);
            if (parent == null || parent == candidate)
                break;
            candidate = parent;
        }

        if (string.IsNullOrEmpty(Root))
            Root = appDir;

        AppDir = Root;
        ConfigPath = Path.Combine(AppDir, "llama-manager.config.json");
    }

    public static string GetAppDir() => AppDir;
    public static string GetRoot() => Root;
    public static string GetLlamaServerPath() => Path.Combine(Root, "llama-server.exe");
    public static string GetLogPath() => Path.Combine(AppDir, "llama-manager.log");
    public static string GetStdoutPath() => Path.Combine(AppDir, "llama-server.stdout.log");
    public static string GetStderrPath() => Path.Combine(AppDir, "llama-server.stderr.log");

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
