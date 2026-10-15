using System.IO;
using Microsoft.Win32;
using LlamaManager.Models;

namespace LlamaManager.Services;

public static class AutostartService
{
    private const string Name = "LlamaCppModelServer";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string StartupFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Name + ".cmd");
    private static readonly string Schtasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (DateTime At, bool Enabled, string Text)? Cached;

    public static async Task<(bool Enabled, string Text)> GetStatusAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (Cached is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(30)) return (cached.Enabled, cached.Text);
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var run = key?.GetValue(Name) is string { Length: > 0 };
            var legacy = File.Exists(StartupFile) || await HasLegacyTaskAsync();
            var text = legacy ? "已开启（有旧注册，重新开启可迁移）" : run ? "已开启（登录后启动）" : "已关闭";
            Cached = (DateTime.UtcNow, run || legacy, text);
            return (run || legacy, text);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            var enabled = Cached?.Enabled ?? false;
            var text = "检测失败：" + ex.Message;
            Cached = (DateTime.UtcNow, enabled, text);
            return (enabled, text);
        }
        finally { Gate.Release(); }
    }

    public static string GetExecutablePath()
    {
        var current = Environment.ProcessPath;
        if (current != null && Path.GetFileName(current).Equals("LlamaManager.exe", StringComparison.OrdinalIgnoreCase)) return current;
        var published = Path.Combine(ConfigService.GetManagerRoot(), "dist", "LlamaManager.exe");
        if (File.Exists(published)) return published;
        throw new FileNotFoundException("请先发布应用到 dist 目录。", published);
    }

    public static async Task EnableAsync(ManagerConfig config)
    {
        var backendId = string.IsNullOrWhiteSpace(config.ActiveBackendId) ? "llamacpp" : config.ActiveBackendId;
        if (backendId == "llamacpp" && !File.Exists(config.ModelPath))
            throw new FileNotFoundException("开启自启动前请先选择有效模型。");
        var executable = GetExecutablePath();
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(Name, LauncherArguments.Quote(executable) + $" --service --backend {backendId}", RegistryValueKind.String);
        await RemoveLegacyAsync();
        config.AutoStartEnabled = true;
        ConfigService.SaveAutostartSetting(true);
        Cached = null;
    }

    public static async Task DisableAsync(ManagerConfig config)
    {
        await RemoveLegacyAsync();
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(Name, throwOnMissingValue: false);
        config.AutoStartEnabled = false;
        ConfigService.SaveAutostartSetting(false);
        Cached = null;
    }

    private static async Task<bool> HasLegacyTaskAsync()
    {
        try { await ProcessProbe.RunAsync(Schtasks, $"/Query /TN {Name} /FO CSV", TimeSpan.FromSeconds(3)); return true; }
        catch (IOException) { return false; } // schtasks exits nonzero when the task does not exist
    }

    private static async Task RemoveLegacyAsync()
    {
        // Only the exact legacy entries created by this launcher are migrated.
        if (await HasLegacyTaskAsync()) await ProcessProbe.RunAsync(Schtasks, $"/Delete /TN {Name} /F", TimeSpan.FromSeconds(5));
        if (File.Exists(StartupFile)) File.Delete(StartupFile);
    }
}
