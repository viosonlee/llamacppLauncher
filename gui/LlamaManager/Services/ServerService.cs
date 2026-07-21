using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace LlamaManager.Services;

public static class ServerService
{
    private static Process? _managedProcess;
    private static readonly object _logLock = new object();

    public static bool IsRunning(Models.ManagerConfig config)
    {
        return GetManagedProcess(config) != null;
    }

    public static Process? GetManagedProcess(Models.ManagerConfig config)
    {
        if (config.LastPid <= 0)
            return null;

        try
        {
            var process = Process.GetProcessById(config.LastPid);
            if (process.ProcessName != "llama-server")
                return null;

            var expected = Path.GetFullPath(ConfigService.GetLlamaServerPath());
            if (!string.IsNullOrEmpty(process.MainModule?.FileName))
            {
                var actual = Path.GetFullPath(process.MainModule.FileName);
                if (actual != expected)
                    return null;
            }

            return process;
        }
        catch
        {
            return null;
        }
    }

    public static Process? GetAnyLlamaServerProcess()
    {
        var processes = Process.GetProcessesByName("llama-server");
        if (processes.Length == 0)
            return null;

        try
        {
            var expected = Path.GetFullPath(ConfigService.GetLlamaServerPath());
            foreach (var process in processes)
            {
                try
                {
                    if (!string.IsNullOrEmpty(process.MainModule?.FileName))
                    {
                        var actual = Path.GetFullPath(process.MainModule.FileName);
                        if (actual == expected)
                            return process;
                    }
                }
                catch { }
            }
        }
        catch { }

        return processes[0];
    }

    public static Process? GetActiveProcess(Models.ManagerConfig config)
    {
        var managed = GetManagedProcess(config);
        if (managed != null)
            return managed;

        var anyServer = GetAnyLlamaServerProcess();
        return anyServer;
    }

    public static bool TestPortInUse(int port)
    {
        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners();
            return listeners.Any(l => l.Port == port);
        }
        catch
        {
            return false;
        }
    }

    public static bool TestApiReady(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = client.GetAsync($"http://127.0.0.1:{port}/v1/models").Result;
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> StartAsync(Models.ManagerConfig config, bool openBrowser = false, Action<string>? log = null)
    {
        var serverPath = ConfigService.GetLlamaServerPath();
        if (!File.Exists(serverPath))
            throw new FileNotFoundException($"未找到 llama-server.exe：{serverPath}");

        if (string.IsNullOrEmpty(config.ModelPath) || !File.Exists(config.ModelPath))
            throw new InvalidOperationException("未选择有效模型。");

        if (string.IsNullOrEmpty(config.Args))
            config.Args = PresetService.GeneratePresetArgs(config.ModelPath, config.Port);

        var running = GetManagedProcess(config);
        if (running != null)
        {
            log?.Invoke($"服务已经在运行，PID {running.Id}。");
            if (openBrowser)
                OpenWebUi(config);
            return true;
        }

        var port = ParsePortFromArgs(config.Args, config.Port);
        if (TestPortInUse(port))
            throw new InvalidOperationException($"端口 {port} 已被占用。");

        var quotedModel = $"\"{config.ModelPath.Replace("\"", "\\\"")}\"";
        var argumentLine = $"-m {quotedModel} {config.Args}";

        log?.Invoke($"正在启动 llama-server.exe {argumentLine}");

        var psi = new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = argumentLine,
            WorkingDirectory = ConfigService.GetRoot(),
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _managedProcess = Process.Start(psi);
        if (_managedProcess == null)
            throw new InvalidOperationException("无法启动 llama-server 进程。");

        var stderrPath = ConfigService.GetStderrPath();
        try { File.WriteAllText(stderrPath, ""); } catch { } // Clear old log

        _managedProcess.ErrorDataReceived += (s, e) => {
            if (e.Data != null) {
                lock (_logLock) {
                    try { File.AppendAllText(stderrPath, e.Data + Environment.NewLine); } catch { }
                }
            }
        };
        _managedProcess.OutputDataReceived += (s, e) => {
            if (e.Data != null) {
                lock (_logLock) {
                    try { File.AppendAllText(stderrPath, e.Data + Environment.NewLine); } catch { }
                }
            }
        };
        _managedProcess.BeginErrorReadLine();
        _managedProcess.BeginOutputReadLine();

        config.LastPid = _managedProcess.Id;
        config.LastStartedAt = DateTime.Now.ToString("o");
        config.Port = port;
        ConfigService.SaveConfig(config);

        var deadline = DateTime.Now.AddSeconds(90);
        while (DateTime.Now < deadline)
        {
            if (_managedProcess.HasExited)
            {
                var stderr = "";
                try
                {
                    if (File.Exists(ConfigService.GetStderrPath()))
                    {
                        var lines = File.ReadLines(ConfigService.GetStderrPath()).ToList();
                        stderr = string.Join("\n", lines.TakeLast(40));
                    }
                }
                catch { }

                config.LastPid = 0;
                ConfigService.SaveConfig(config);
                throw new InvalidOperationException($"llama-server 启动失败或提前退出。\n{stderr}");
            }

            if (TestApiReady(port))
            {
                log?.Invoke($"服务已启动，PID {_managedProcess.Id}，端口 {port}。");
                if (openBrowser)
                    OpenWebUi(config);
                return true;
            }

            await Task.Delay(2000);
        }

        log?.Invoke($"服务进程已启动，PID {_managedProcess.Id}，但 90 秒内 API 未就绪。");
        return true;
    }

    public static bool Stop(Models.ManagerConfig config, Action<string>? log = null)
    {
        var process = GetActiveProcess(config);
        if (process == null)
        {
            log?.Invoke("当前没有由本工具管理的 llama-server 进程在运行。");
            config.LastPid = 0;
            ConfigService.SaveConfig(config);
            return false;
        }

        log?.Invoke($"正在停止 llama-server，PID {process.Id}。");
        StopProcessTree(process.Id);
        Thread.Sleep(1000);
        config.LastPid = 0;
        ConfigService.SaveConfig(config);
        return true;
    }

    public static void OpenWebUi(Models.ManagerConfig config)
    {
        var port = ParsePortFromArgs(config.Args, config.Port);
        var url = $"http://127.0.0.1:{port}";
        LogService.Write($"正在打开 Web UI：{url}");
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法打开浏览器，请手动访问 {url}\n错误: {ex.Message}");
            }
        }
    }

    public static int ParsePortFromArgs(string args, int fallback = 8080)
    {
        var match = Regex.Match(args, @"(?:^|\s)--port\s+(\d+)");
        if (match.Success)
            return int.Parse(match.Groups[1].Value);
        return fallback;
    }

    private static void StopProcessTree(int processId)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {processId}");
            foreach (var obj in searcher.Get())
            {
                var childPid = Convert.ToInt32(obj["ProcessId"]);
                StopProcessTree(childPid);
            }
        }
        catch { }

        try
        {
            var process = Process.GetProcessById(processId);
            process.Kill(true);
        }
        catch { }
    }
}
