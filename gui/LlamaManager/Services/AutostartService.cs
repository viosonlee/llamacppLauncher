using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace LlamaManager.Services;

public static class AutostartService
{
    private const string TaskName = "LlamaCppModelServer";
    private const string RunValueName = "LlamaCppModelServer";
    private static readonly string StartupFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "LlamaCppModelServer.cmd");

    public static bool IsEnabled() => HasRunEntry() || HasStartupFile() || HasScheduledTask();

    public static bool IsPrimaryEnabled() => HasRunEntry() || HasStartupFile();

    public static string GetStatusText()
    {
        try
        {
            var runEntry = HasRunEntry();
            var startupFile = HasStartupFile();
            var task = HasScheduledTask();

            if (runEntry && startupFile)
                return task ? "已开启（注册表 + 启动文件 + 计划任务）" : "已开启（注册表 + 启动文件）";
            if (runEntry)
                return task ? "已开启（注册表 + 计划任务）" : "已开启（注册表）";
            if (startupFile)
                return task ? "已开启（启动文件 + 计划任务）" : "已开启（启动文件）";
            if (task)
                return "已开启（计划任务）";
            return "已关闭";
        }
        catch
        {
            return "检测失败";
        }
    }

    public static void Enable(Models.ManagerConfig config)
    {
        if (string.IsNullOrEmpty(config.ModelPath) || !File.Exists(config.ModelPath))
            throw new InvalidOperationException("开启自启动前请先选择有效模型。");

        if (string.IsNullOrEmpty(config.Args))
            config.Args = PresetService.GeneratePresetArgs(config.ModelPath, config.Port);

        var psExe = FindPowerShellExe();
        var scriptPath = FindScriptPath();

        var runArgs = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" -Service";
        var runCommand = $"\"{psExe}\" {runArgs}";

        SetRunEntry(runCommand);
        SetStartupFile(psExe, scriptPath);

        config.AutoStartEnabled = IsPrimaryEnabled();
        ConfigService.SaveConfig(config);
    }

    public static void Disable(Models.ManagerConfig config)
    {
        RemoveRunEntry();
        RemoveStartupFile();

        config.AutoStartEnabled = IsPrimaryEnabled();
        ConfigService.SaveConfig(config);
    }

    private static string FindScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(ConfigService.GetRoot(), "llama-manager.ps1"),
            Path.Combine(ConfigService.GetAppDir(), "llama-manager.ps1"),
            Path.Combine(ConfigService.GetAppDir(), "..", "llama-manager.ps1"),
            Path.Combine(ConfigService.GetAppDir(), "..", "..", "llama-manager.ps1")
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return Path.GetFullPath(candidates[0]);
    }

    private static bool HasRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            var value = key?.GetValue(RunValueName);
            return value != null && !string.IsNullOrEmpty(value.ToString());
        }
        catch { return false; }
    }

    private static void SetRunEntry(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key?.SetValue(RunValueName, command, RegistryValueKind.String);
    }

    private static void RemoveRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue(RunValueName, false);
        }
        catch { }
    }

    private static bool HasStartupFile() => File.Exists(StartupFilePath);

    private static void SetStartupFile(string psExe, string scriptPath)
    {
        var content = $"@echo off\r\nstart \"\" /min \"{psExe}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" -Service";
        File.WriteAllText(StartupFilePath, content, System.Text.Encoding.ASCII);
    }

    private static void RemoveStartupFile()
    {
        try { File.Delete(StartupFilePath); } catch { }
    }

    private static bool HasScheduledTask()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_ScheduledTask WHERE Name = '{TaskName}'");
            return searcher.Get().Count > 0;
        }
        catch { return false; }
    }

    private static string FindPowerShellExe()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';'))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var fullPath = Path.Combine(dir.Trim(), "pwsh.exe");
            if (File.Exists(fullPath)) return fullPath;
        }
        foreach (var dir in pathEnv.Split(';'))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var fullPath = Path.Combine(dir.Trim(), "powershell.exe");
            if (File.Exists(fullPath)) return fullPath;
        }
        return "powershell.exe";
    }
}
