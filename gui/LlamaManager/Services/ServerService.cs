using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace LlamaManager.Services;

public static class ServerService
{
    private static readonly object _logLock = new object();
    private const string EmbeddingArgs = "--host 127.0.0.1 --port 8081 --embedding -ngl 0 -c 8192 -b 4096";
    private const string BgeM3Filename = "bge-m3-q8_0.gguf";
    private const int EmbeddingPort = 8081;

    // ─── Main Model Server ───

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

            var expected = Path.GetFullPath(ConfigService.GetLlamaServerPath(config.ModelPath));
            if (!string.IsNullOrEmpty(process.MainModule?.FileName))
            {
                var actual = Path.GetFullPath(process.MainModule.FileName);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            var cmdLine = GetProcessCommandLine(process.Id);
            if (string.IsNullOrEmpty(cmdLine))
                return null;

            var port = ParsePortFromArgs(config.Args, config.Port);
            if (!Regex.IsMatch(cmdLine, $@"(?:^|\s)--port\s+{port}\b"))
                return null;

            if (cmdLine.Contains("--embedding", StringComparison.OrdinalIgnoreCase))
                return null;

            return process;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Discover main server by scanning llama-server processes matching our path and port 8080.</summary>
    public static Process? FindMainProcess(Models.ManagerConfig config)
    {
        var expectedPath = Path.GetFullPath(ConfigService.GetLlamaServerPath(config.ModelPath));
        var candidates = Process.GetProcessesByName("llama-server");
        foreach (var p in candidates)
        {
            try
            {
                if (!Path.GetFullPath(p.MainModule?.FileName ?? "").Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var cmdLine = GetProcessCommandLine(p.Id);
                if (cmdLine == null)
                    continue;

                // Must match port from config args, not any port
                var port = ParsePortFromArgs(config.Args, config.Port);
                if (!Regex.IsMatch(cmdLine, $@"(?:^|\s)--port\s+{port}\b"))
                    continue;

                if (cmdLine.Contains("--embedding", StringComparison.OrdinalIgnoreCase))
                    continue;

                config.LastPid = p.Id;
                ConfigService.SaveConfig(config);
                return p;
            }
            catch { }
        }
        return null;
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
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var response = client.GetAsync($"http://127.0.0.1:{port}/health").Result;
            if (response.IsSuccessStatusCode) return true;
        }
        catch { }

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
        var serverPath = ConfigService.GetLlamaServerPath(config.ModelPath);
        if (!File.Exists(serverPath))
            throw new FileNotFoundException($"未找到 llama-server.exe：{serverPath}");

        if (string.IsNullOrEmpty(config.ModelPath) || !File.Exists(config.ModelPath))
            throw new InvalidOperationException("未选择有效模型。");

        if (string.IsNullOrEmpty(config.Args))
            config.Args = PresetService.GeneratePresetArgs(config.ModelPath, config.Port);

        var running = GetManagedProcess(config);
        running ??= FindMainProcess(config);
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
        var serverName = Path.GetFileName(serverPath);
        var isPrism = ConfigService.IsPrismModel(config.ModelPath);

        log?.Invoke(isPrism
            ? $"正在启动 Prism 专属版服务 ({serverName}) {argumentLine}"
            : $"正在启动 llama-server.exe {argumentLine}");

        var workingDir = Path.GetDirectoryName(serverPath) ?? ConfigService.GetLlamaCppRoot();

        var psi = new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = argumentLine,
            WorkingDirectory = workingDir,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("无法启动 llama-server 进程。");

        var stderrPath = ConfigService.GetMainStderrPath();
        var stdoutPath = ConfigService.GetMainStdoutPath();
        try { File.WriteAllText(stderrPath, ""); } catch { }
        try { File.WriteAllText(stdoutPath, ""); } catch { }

        process.ErrorDataReceived += (s, e) => {
            if (e.Data != null) {
                lock (_logLock) {
                    try { File.AppendAllText(stderrPath, e.Data + Environment.NewLine); } catch { }
                }
            }
        };
        process.OutputDataReceived += (s, e) => {
            if (e.Data != null) {
                lock (_logLock) {
                    try { File.AppendAllText(stdoutPath, e.Data + Environment.NewLine); } catch { }
                }
            }
        };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        config.LastPid = process.Id;
        config.LastStartedAt = DateTime.Now.ToString("o");
        config.Port = port;
        ConfigService.SaveConfig(config);

        var deadline = DateTime.Now.AddSeconds(90);
        while (DateTime.Now < deadline)
        {
            if (process.HasExited)
            {
                var stderr = "";
                try
                {
                    if (File.Exists(ConfigService.GetMainStderrPath()))
                    {
                        var lines = File.ReadLines(ConfigService.GetMainStderrPath()).ToList();
                        var errLines = lines.Where(l =>
                            l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("CUDA error", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                        ).TakeLast(8).ToList();

                        if (errLines.Count > 0)
                            stderr = string.Join("\n", errLines);
                        else
                            stderr = string.Join("\n", lines.TakeLast(10));
                    }
                }
                catch { }

                config.LastPid = 0;
                ConfigService.SaveConfig(config);
                throw new InvalidOperationException($"llama-server 提前退出（退出码 {process.ExitCode}）。\n{stderr}");
            }

            if (TestApiReady(port))
            {
                log?.Invoke($"服务已启动，PID {process.Id}，端口 {port}。");
                if (openBrowser)
                    OpenWebUi(config);
                return true;
            }

            await Task.Delay(2000);
        }

        log?.Invoke($"服务进程已启动，PID {process.Id}，但 90 秒内 API 未就绪。");
        return true;
    }

    public static bool Stop(Models.ManagerConfig config, Action<string>? log = null)
    {
        var process = GetManagedProcess(config);
        process ??= FindMainProcess(config);
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

    // ─── Embedding Server ───

    public static Process? GetEmbeddingProcess(Models.ManagerConfig config)
    {
        if (config.EmbeddingLastPid <= 0)
            return null;

        try
        {
            var process = Process.GetProcessById(config.EmbeddingLastPid);
            if (process.ProcessName != "llama-server")
                return null;

            var expected = Path.GetFullPath(ConfigService.GetLlamaServerPath());
            if (!string.IsNullOrEmpty(process.MainModule?.FileName))
            {
                var actual = Path.GetFullPath(process.MainModule.FileName);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            var cmdLine = GetProcessCommandLine(process.Id);
            if (string.IsNullOrEmpty(cmdLine))
                return null;

            if (!cmdLine.Contains("--embedding", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!Regex.IsMatch(cmdLine, @"(?:^|\s)--port\s+8081\b"))
                return null;
            if (!cmdLine.Contains(BgeM3Filename, StringComparison.OrdinalIgnoreCase))
                return null;

            return process;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Discover embedding server by scanning llama-server processes with --embedding flag on port 8081.</summary>
    public static Process? FindEmbeddingProcess(Models.ManagerConfig config)
    {
        var expectedPath = Path.GetFullPath(ConfigService.GetLlamaServerPath());
        var candidates = Process.GetProcessesByName("llama-server");
        foreach (var p in candidates)
        {
            try
            {
                if (!Path.GetFullPath(p.MainModule?.FileName ?? "").Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var cmdLine = GetProcessCommandLine(p.Id);
                if (cmdLine == null)
                    continue;

                if (!cmdLine.Contains("--embedding", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!Regex.IsMatch(cmdLine, @"(?:^|\s)--port\s+8081\b"))
                    continue;

                if (!cmdLine.Contains(BgeM3Filename, StringComparison.OrdinalIgnoreCase))
                    continue;

                config.EmbeddingLastPid = p.Id;
                config.EmbeddingLastStartedAt = DateTime.Now.ToString("o");
                ConfigService.SaveConfig(config);
                return p;
            }
            catch { }
        }
        return null;
    }

    public static async Task<bool> StartEmbeddingAsync(Models.ManagerConfig config, Action<string>? log = null)
    {
        var serverPath = ConfigService.GetLlamaServerPath();
        if (!File.Exists(serverPath))
            throw new FileNotFoundException($"未找到 llama-server.exe：{serverPath}");

        var modelPath = string.IsNullOrEmpty(config.EmbeddingModelPath)
            ? Path.Combine(ConfigService.GetLlamaCppRoot(), "models", BgeM3Filename)
            : config.EmbeddingModelPath;

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"嵌入模型未找到：{modelPath}");

        var running = GetEmbeddingProcess(config);
        running ??= FindEmbeddingProcess(config);
        if (running != null)
        {
            log?.Invoke($"嵌入服务已经在运行，PID {running.Id}。");
            return true;
        }

        if (TestPortInUse(EmbeddingPort))
            throw new InvalidOperationException($"端口 {EmbeddingPort} 已被占用。");

        const int port = EmbeddingPort;
        var quotedModel = $"\"{modelPath.Replace("\"", "\\\"")}\"";
        var argumentLine = $"-m {quotedModel} {EmbeddingArgs}";

        log?.Invoke($"正在启动嵌入服务：llama-server.exe {argumentLine}");

        var psi = new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = argumentLine,
            WorkingDirectory = ConfigService.GetLlamaCppRoot(),
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("无法启动嵌入服务进程。");

        var stderrPath = ConfigService.GetEmbeddingStderrPath();
        var stdoutPath = ConfigService.GetEmbeddingStdoutPath();
        try { File.WriteAllText(stderrPath, ""); } catch { }
        try { File.WriteAllText(stdoutPath, ""); } catch { }

        process.ErrorDataReceived += (s, e) => {
            if (e.Data != null) {
                lock (_logLock) {
                    try { File.AppendAllText(stderrPath, e.Data + Environment.NewLine); } catch { }
                }
            }
        };
        process.OutputDataReceived += (s, e) => {
            if (e.Data != null) {
                lock (_logLock) {
                    try { File.AppendAllText(stdoutPath, e.Data + Environment.NewLine); } catch { }
                }
            }
        };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        config.EmbeddingLastPid = process.Id;
        config.EmbeddingLastStartedAt = DateTime.Now.ToString("o");
        ConfigService.SaveConfig(config);

        var deadline = DateTime.Now.AddSeconds(90);
        while (DateTime.Now < deadline)
        {
            if (process.HasExited)
            {
                var stderr = "";
                try
                {
                    if (File.Exists(ConfigService.GetEmbeddingStderrPath()))
                    {
                        var lines = File.ReadLines(ConfigService.GetEmbeddingStderrPath()).ToList();
                        stderr = string.Join("\n", lines.TakeLast(40));
                    }
                }
                catch { }

                config.EmbeddingLastPid = 0;
                ConfigService.SaveConfig(config);
                throw new InvalidOperationException($"嵌入服务启动失败或提前退出。\n{stderr}");
            }

            if (TestApiReady(port))
            {
                log?.Invoke($"嵌入服务已启动，PID {process.Id}，端口 {port}。");
                return true;
            }

            await Task.Delay(2000);
        }

        log?.Invoke($"嵌入服务进程已启动，PID {process.Id}，但 90 秒内 API 未就绪。");
        return true;
    }

    public static bool StopEmbedding(Models.ManagerConfig config, Action<string>? log = null)
    {
        var process = GetEmbeddingProcess(config);
        process ??= FindEmbeddingProcess(config);
        if (process == null)
        {
            log?.Invoke("当前没有运行的嵌入服务。");
            config.EmbeddingLastPid = 0;
            ConfigService.SaveConfig(config);
            return false;
        }

        log?.Invoke($"正在停止嵌入服务，PID {process.Id}。");
        StopProcessTree(process.Id);
        Thread.Sleep(1000);
        config.EmbeddingLastPid = 0;
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

    private static string? GetProcessCommandLine(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (var obj in searcher.Get())
            {
                return obj["CommandLine"]?.ToString();
            }
        }
        catch { }
        return null;
    }
}
