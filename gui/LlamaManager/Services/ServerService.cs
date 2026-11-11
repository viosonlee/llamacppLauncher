using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using LlamaManager.Models;

namespace LlamaManager.Services;

public static class ServerService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    // Lock only the spawn/persist or stop/persist transaction, never the readiness wait.
    private static async Task<FileStream> LockLifecycle(bool embedding, CancellationToken ct)
    {
        var path = Path.Combine(ConfigService.GetManagerRoot(), "logs", embedding ? "embedding.lock" : "server.lock");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(100, ct).ConfigureAwait(false); }
        }
    }

    public static bool IsRunning(ManagerConfig config)
    {
        using var process = GetManagedProcess(config);
        return process != null;
    }

    public static Process? GetManagedProcess(ManagerConfig config) => GetProcess(config, false);
    public static Process? GetEmbeddingProcess(ManagerConfig config) => GetProcess(config, true);
    public static Process? FindMainProcess(ManagerConfig config) => GetManagedProcess(config);
    public static Process? FindEmbeddingProcess(ManagerConfig config) => GetEmbeddingProcess(config);

    private static Process? GetProcess(ManagerConfig config, bool embedding)
    {
        var latest = ConfigService.ReadConfig();
        var slot = embedding ? ManagerConfig.SlotEmbedding : ManagerConfig.SlotMain;
        if (embedding) { config.SetRunning(slot, latest.RunningEmbedding); config.EmbeddingLastPid = latest.EmbeddingLastPid; }
        else { config.SetRunning(slot, latest.RunningServer); config.LastPid = latest.LastPid; }
        var session = latest.GetRunning(slot);
        if (session != null)
        {
            var validated = ValidateProcess(session);
            if (validated != null) return validated;
        }
        // Migrate only the recorded PID. Never adopt arbitrary processes merely sharing a port.
        var legacy = RecoverLegacy(config, latest, embedding);
        if (legacy != null) return legacy;
        // Adopt a live process only when it is the recorded session relaunched externally
        // (same engine, model, port and flags, verified against the live command line).
        var byScan = RecoverByScan(config, latest, embedding);
        if (byScan != null) return byScan;
        // A detached server can outlive its record (crash, cleared or corrupt config).
        // Adopt it with the same full fingerprint so "start" manages our own server
        // instead of failing with a port conflict.
        return RecoverByPort(config, embedding);
    }

    private static DateTime _lastPortRecoveryUtc = DateTime.MinValue;

    /// <summary>
    /// Last-resort recovery for a server whose config record is gone. It only matches a
    /// live llama-server started from this install's engine for the configured model on
    /// the configured port - never an arbitrary process that merely shares the port.
    /// WMI is touched only for matching process-name candidates and at most every few seconds,
    /// so the two-second status poll stays cheap.
    /// </summary>
    private static Process? RecoverByPort(ManagerConfig config, bool embedding)
    {
        var port = embedding ? config.EmbeddingPort : new LauncherArguments(config.Args).GetPort(config.Port);
        // Windows' listener enumeration can lag both appearance and removal. A false table
        // result must not permanently block identity recovery of an HTTP-ready detached server.
        // Keep the bounded scan interval and every process/command-line identity check below.
        if (port <= 0) return null;
        var now = DateTime.UtcNow;
        if ((now - _lastPortRecoveryUtc).TotalSeconds < 5) return null;
        _lastPortRecoveryUtc = now;
        var model = embedding ? config.EmbeddingModelPath : config.ModelPath;
        foreach (var candidate in Process.GetProcessesByName("llama-server"))
        {
            var keep = false;
            try
            {
                var commandLine = GetCommandLine(candidate.Id);
                if (commandLine == null) continue;
                var args = new LauncherArguments(commandLine);
                if (args.GetPort(0) != port || args.Has("--embedding") != embedding) continue;
                var engine = candidate.MainModule?.FileName ?? "";
                var modelPath = args.Get("-m") ?? model;
                if (!PathIdentity.Equals(engine, ConfigService.GetLlamaServerPath(modelPath))) continue;
                if (!PathIdentity.Equals(modelPath, model)) continue;
                var session = new ServerSession
                {
                    ProcessId = candidate.Id, StartTimeUtcTicks = candidate.StartTime.ToUniversalTime().Ticks,
                    EnginePath = engine, ModelPath = modelPath,
                    Args = string.Join(" ", args.Values.Skip(1).Select(LauncherArguments.Quote)),
                    Port = port, Embedding = embedding
                };
                ConfigService.SaveSession(config, session, embedding);
                keep = true;
                return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or ManagementException) { }
            finally { if (!keep) candidate.Dispose(); }
        }
        return null;
    }

    /// <summary>Names the process holding a port when it is a llama-server, for the error text.</summary>
    private static string DescribePortOccupant(int port)
    {
        try
        {
            foreach (var candidate in Process.GetProcessesByName("llama-server"))
            {
                using (candidate)
                {
                    var commandLine = GetCommandLine(candidate.Id);
                    if (commandLine != null && new LauncherArguments(commandLine).GetPort(0) == port)
                        return $"占用者是 llama-server（PID {candidate.Id}）：它可能是此前启动、未正常退出或使用了其它模型/参数的服务。" +
                               "请先结束该进程，或在“高级选项”页把端口改为其它值。";
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or ManagementException) { }
        return "请结束占用该端口的进程，或在“高级选项”页把端口改为其它值。";
    }

    public static Process? ValidateProcess(ServerSession session)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(session.ProcessId);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == session.StartTimeUtcTicks &&
                PathIdentity.Equals(ProcessIdentityService.ExecutablePath(process), session.EnginePath))
                return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process?.Dispose();
        return null;
    }

    /// <summary>
    /// Whether the recorded process still exists with the identity it was started with. A stale
    /// record (crash, reboot, manual kill) must not present itself as a running service.
    /// </summary>
    public static bool IsSessionAlive(ServerSession? session)
    {
        if (session is not { ProcessId: > 0 }) return false;
        using var process = ValidateProcess(session);
        return process != null;
    }

    private static Process? RecoverLegacy(ManagerConfig config, ManagerConfig latest, bool embedding)
    {
        var pid = embedding ? latest.EmbeddingLastPid : latest.LastPid;
        var started = embedding ? latest.EmbeddingLastStartedAt : latest.LastStartedAt;
        if (pid <= 0 || !DateTime.TryParse(started, out var recorded)) return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            var engine = process.MainModule?.FileName ?? "";
            var expected = ConfigService.GetLlamaServerPath();
            if (!PathIdentity.Equals(engine, expected) && !PathIdentity.Equals(engine, ConfigService.GetPrismServerPath())) return null;
            // Older configs stored wall-clock launch time rather than the OS process creation timestamp.
            if (Math.Abs((process.StartTime.ToUniversalTime() - recorded.ToUniversalTime()).TotalSeconds) > 10) return null;
            var args = new LauncherArguments(GetCommandLine(pid));
            var model = args.Get("-m");
            if (string.IsNullOrEmpty(model) || args.Has("--embedding") != embedding) return null;
            var session = new ServerSession
            {
                ProcessId = pid, StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                EnginePath = engine, ModelPath = model, Args = string.Join(" ", args.Values.Skip(1).Select(LauncherArguments.Quote)),
                Port = args.GetPort(embedding ? 8081 : 8080), Embedding = embedding
            };
            if (!ConfigService.MigrateSession(config, session)) return null;
            var result = process;
            process = null;
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or ManagementException) { }
        finally { process?.Dispose(); }
        return null;
    }

    private static string? GetCommandLine(int processId)
    {
        using var search = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
        search.Options.Timeout = TimeSpan.FromSeconds(2);
        using var rows = search.Get();
        foreach (ManagementObject row in rows)
        {
            using (row) return row["CommandLine"]?.ToString();
        }
        return null;
    }

    private static bool RecordedProcessGone(ServerSession recorded)
    {
        try
        {
            using var process = Process.GetProcessById(recorded.ProcessId);
            return process.HasExited || process.StartTime.ToUniversalTime().Ticks != recorded.StartTimeUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static Process? RecoverByScan(ManagerConfig config, ManagerConfig latest, bool embedding)
    {
        var recorded = embedding ? latest.RunningEmbedding : latest.RunningServer;
        if (recorded == null || !RecordedProcessGone(recorded)) return null;
        foreach (var candidate in Process.GetProcessesByName("llama-server"))
        {
            var adopted = false;
            try
            {
                var engine = candidate.MainModule?.FileName ?? "";
                var commandLine = GetCommandLine(candidate.Id);
                if (commandLine == null) continue;
                var startTicks = candidate.StartTime.ToUniversalTime().Ticks;
                if (!recorded.MatchesFingerprint(engine, commandLine)) continue;
                var live = new ServerSession
                {
                    ProcessId = candidate.Id, StartTimeUtcTicks = startTicks, EnginePath = engine,
                    ModelPath = recorded.ModelPath, Args = recorded.Args, Port = recorded.Port, Embedding = recorded.Embedding
                };
                if (!ConfigService.AdoptSession(config, recorded, live)) return null;
                adopted = true;
                return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or ManagementException) { }
            finally { if (!adopted) candidate.Dispose(); }
        }
        return null;
    }

    public static bool TestPortInUse(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(l => l.Port == port);

    /// <summary>
    /// Probes the requested bind address without changing the configured port. Listener enumeration
    /// misses Windows port exclusions. Hostnames are left to the engine's resolver so this check
    /// never performs blocking DNS/WMI on the UI dispatcher.
    /// </summary>
    public static string? PortUnavailableReason(int port, string? host = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            host = "127.0.0.1";
        if (!IPAddress.TryParse(host, out var address)) return null;
        var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
        try
        {
            listener.Start();
            return null;
        }
        catch (SocketException ex) { return PortBindFailureReason(port, ex.SocketErrorCode); }
        finally { listener.Stop(); }
    }

    /// <summary>Pure error mapping, testable without reserving ports or changing Windows networking.</summary>
    public static string PortBindFailureReason(int port, SocketError error) => error switch
    {
        SocketError.AddressAlreadyInUse => $"端口 {port} 已被占用。请先停止占用服务；不会自动切换服务端口。",
        SocketError.AccessDenied => $"端口 {port} 无法绑定：权限不足或处于 Windows 保留端口段内（常见于 Hyper-V/WSL）。请检查权限或保留范围；不会自动切换服务端口。",
        _ => $"端口 {port} 无法绑定（{error}）。",
    };

    public static async Task<bool> TestApiReadyAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync($"http://127.0.0.1:{port}/health", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    public static int GetRunningPort(ManagerConfig config) => config.RunningServer?.Port ?? ParsePortFromArgs(config.Args, config.Port);
    public static int ParsePortFromArgs(string args, int fallback = 8080) => new LauncherArguments(args).GetPort(fallback);

    /// <summary>Default API model name when the caller omitted -a / --alias: the file stem without directory or extension.</summary>
    internal static string DefaultModelAlias(string modelPath) =>
        Path.GetFileNameWithoutExtension(modelPath).Trim();

    /// <summary>Applies the resolved port, model path and default API alias (preserving an explicit -a / --alias).</summary>
    internal static string ApplyLaunchIdentity(LauncherArguments parsed, string model, int port)
    {
        var args = new LauncherArguments(parsed.Set("--port", port.ToString())).Set("-m", model);
        if (!parsed.Has("--alias"))
        {
            var alias = DefaultModelAlias(model);
            if (!string.IsNullOrEmpty(alias))
                args = new LauncherArguments(args).Set("--alias", alias);
        }
        return args;
    }

    public static Task<bool> StartAsync(ManagerConfig config, bool openBrowser = false, Action<string>? log = null, CancellationToken ct = default)
        => StartCoreAsync(config, false, openBrowser, log, ct);

    public static Task<bool> StartEmbeddingAsync(ManagerConfig config, Action<string>? log = null, CancellationToken ct = default)
        => StartCoreAsync(config, true, false, log, ct);

    private static async Task<bool> StartCoreAsync(ManagerConfig config, bool embedding, bool openBrowser, Action<string>? log, CancellationToken ct)
    {
        // Snapshot before the first await: editing the form cannot change this launch.
        var model = embedding ? config.EmbeddingModelPath : config.ModelPath;
        var fallbackPort = embedding ? config.EmbeddingPort : config.Port;
        if (embedding && string.IsNullOrEmpty(model)) model = Path.Combine(ConfigService.GetRoot(), "models", "bge-m3-q8_0.gguf");
        if (!File.Exists(model)) throw new FileNotFoundException("未选择有效模型。", model);
        if (ConfigService.IsPrismModel(model) && !ConfigService.HasPrismServer())
            throw new FileNotFoundException("该模型需要 Prism 引擎。", ConfigService.GetPrismServerPath());
        var engine = ConfigService.GetLlamaServerPath(model);
        if (!File.Exists(engine)) throw new FileNotFoundException("未找到 llama-server.exe。", engine);
        var args = embedding ? $"--host 127.0.0.1 --port {fallbackPort} --embedding -ngl 0 -c 8192 -b 512" : config.Args;
        if (string.IsNullOrWhiteSpace(args))
            args = await Task.Run(() => PresetService.GeneratePresetArgs(model, fallbackPort), ct).ConfigureAwait(false);
        var parsed = new LauncherArguments(args);
        if (parsed.Has("-m")) throw new ArgumentException("请通过模型列表选择模型，启动参数中不能重复指定 -m。");
        if (parsed.Has("--embedding") != embedding) throw new ArgumentException("主服务不能使用 --embedding 参数。");
        var port = parsed.GetPort(fallbackPort);
        var draft = parsed.Get("-md");
        if (!string.IsNullOrEmpty(draft) && !File.Exists(draft))
            throw new FileNotFoundException("未找到投机解码草稿模型 (-md)。", draft);
        args = ApplyLaunchIdentity(parsed, model, port);
        ServerSession session;
        Process process;
        using (await LockLifecycle(embedding, ct).ConfigureAwait(false))
        {
            var existing = GetProcess(config, embedding);
            if (existing != null)
            {
                process = existing;
                session = (embedding ? config.RunningEmbedding : config.RunningServer)!;
                log?.Invoke($"服务已运行：{Path.GetFileName(session.ModelPath)}，PID {session.ProcessId}，端口 {session.Port}。");
            }
            else
            {
                var portProblem = PortUnavailableReason(port, parsed.Get("--host"));
                if (portProblem != null) throw new InvalidOperationException(portProblem);
                log?.Invoke($"启动 {engine} {args}");
                process = DetachedProcess.Start(engine, args,
                    embedding ? ConfigService.GetEmbeddingStdoutPath() : ConfigService.GetMainStdoutPath(),
                    embedding ? ConfigService.GetEmbeddingStderrPath() : ConfigService.GetMainStderrPath());
                session = new ServerSession
                {
                    ProcessId = process.Id, StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    EnginePath = engine, ModelPath = model, Args = args, Port = port, Embedding = embedding
                };
                try { ConfigService.SaveSession(config, session, embedding); }
                catch { if (!process.HasExited) process.Kill(true); process.Dispose(); throw; }
            }
        }

        using (process)
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var latest = ConfigService.ReadConfig();
                if ((embedding ? latest.RunningEmbedding : latest.RunningServer) != session)
                    throw new OperationCanceledException("此次启动已停止或被替换。");
                if (process.HasExited)
                {
                    var tail = LogService.ReadFileTail(embedding ? ConfigService.GetEmbeddingStderrPath() : ConfigService.GetMainStderrPath(), 12);
                    throw new InvalidOperationException($"服务提前退出（{process.ExitCode}）。\n" + string.Join("\n", tail));
                }
                if (await TestApiReadyAsync(session.Port, ct).ConfigureAwait(false))
                {
                    // A concurrent stop must not produce a late "ready" update.
                    ct.ThrowIfCancellationRequested();
                    if (process.HasExited) throw new OperationCanceledException("服务已停止。");
                    log?.Invoke($"服务已就绪，PID {session.ProcessId}，端口 {session.Port}。");
                    if (openBrowser) OpenWebUi(config);
                    return true;
                }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        log?.Invoke("90 秒内 API 尚未就绪，进程仍在加载；可查看日志或停止服务。");
        return false;
    }

    public static Task<bool> StopAsync(ManagerConfig config, Action<string>? log = null) => StopCoreAsync(config, false, log);
    public static Task<bool> StopEmbeddingAsync(ManagerConfig config, Action<string>? log = null) => StopCoreAsync(config, true, log);

    private static async Task<bool> StopCoreAsync(ManagerConfig config, bool embedding, Action<string>? log)
    {
        using (await LockLifecycle(embedding, CancellationToken.None).ConfigureAwait(false))
        {
            using var process = GetProcess(config, embedding);
            if (process == null)
            {
                ConfigService.SaveSession(config, null, embedding);
                log?.Invoke("当前没有由本工具管理的服务。");
                return false;
            }
            log?.Invoke($"正在停止服务，PID {process.Id}。");
            process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            ConfigService.SaveSession(config, null, embedding);
            return true;
        }
    }

    public static void OpenWebUi(ManagerConfig config)
    {
        var url = $"http://127.0.0.1:{GetRunningPort(config)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
