using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager.Engines;

/// <summary>
/// A backend driven entirely by a <see cref="PluginManifest"/>: it launches the declared
/// executable with templated arguments and waits for an HTTP or TCP readiness signal.
/// No plugin code is loaded into the launcher, so a crash is isolated to the child process.
/// </summary>
public sealed class ProcessPluginBackend : IServerBackend
{
    private readonly PluginManifest _manifest;
    private readonly string _installDir;
    internal Func<EngineSession, CancellationToken, Task>? BeforePublishAsync { get; init; }
    internal Action? OnStartLeaseContention { get; init; }

    public ProcessPluginBackend(PluginManifest manifest, string installDir)
    {
        _manifest = manifest;
        _installDir = Path.GetFullPath(installDir);
    }

    public PluginManifest Manifest => _manifest;

    public BackendDescriptor Descriptor { get; private set; } = null!;

    public IReadOnlyList<ParameterSpec> Parameters { get; private set; } = [];

    /// <summary>Must be called once before use; kept separate so the constructor stays simple.</summary>
    public ProcessPluginBackend Initialize()
    {
        if (_manifest.Validate() is { } problem) throw new ArgumentException(problem, nameof(_manifest));
        Descriptor = new BackendDescriptor(_manifest.Id, _manifest.DisplayName, _manifest.Description,
            BuiltIn: false, GpuHeavy: _manifest.GpuHeavy);
        Parameters = _manifest.Parameters.Select(p => p.ToParameterSpec()).ToArray();
        return this;
    }

    private string ExecutablePath()
    {
        var name = _manifest.ExecutableForCurrentOs() ?? throw new FileNotFoundException("清单没有当前系统的可执行文件。");
        return Path.IsPathRooted(name) ? name : Path.Combine(_installDir, name);
    }

    public async Task<BackendStatus> ProbeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var exe = ExecutablePath();
        if (!File.Exists(exe)) return BackendStatus.Unavailable("未找到引擎可执行文件：" + exe);
        if (_manifest.Detect == null || _manifest.Detect.VersionArg.Count == 0)
            return new BackendStatus(true, exe, null, null);
        var arguments = string.Join(" ", _manifest.Detect.VersionArg.Select(LauncherArguments.Quote));
        var pattern = _manifest.Detect.VersionRegex;
        return await Task.Run(async () =>
        {
            try
            {
                var start = StrataService.CaptureStart(exe);
                start.Arguments = arguments;
                var text = await ProcessProbe.RunAsync(start, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var version = pattern.Length > 0
                    ? Regex.Match(text, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200)) is { Success: true } m ? m.Groups[1].Value : null
                    : null;
                return new BackendStatus(true, exe, version, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { return BackendStatus.Unavailable("版本探测超时（5秒），已终止探测进程。"); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or TimeoutException)
            { return BackendStatus.Unavailable("探测失败：" + ex.Message); }
        }, ct).ConfigureAwait(false);
    }

    public async Task<CompatibilityReport> InspectAsync(string modelPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return new CompatibilityReport(false, ["请先选择已下载完成的 GGUF 模型。"], [], "未选择模型。", null);
        return _manifest.Compatibility?.Driver switch
        {
            "strata-matrix" => await StrataMatrixAsync(modelPath, ct).ConfigureAwait(false),
            "polystrata-matrix" => await PolyStrataMatrixAsync(modelPath, ct).ConfigureAwait(false),
            _ => CompatibilityReport.Unknown("该插件未声明兼容性检查。"),
        };
    }

    private static async Task<CompatibilityReport> StrataMatrixAsync(string modelPath, CancellationToken ct)
    {
        var md = await Task.Run(() => GgufService.ReadShards(modelPath, ct), ct).ConfigureAwait(false);
        var report = StrataCompatibility.Analyze(md);
        return new CompatibilityReport(report.IsPackable, report.Blockers, report.Notes,
            StrataCompatibility.Summary(report), null);
    }

    /// <summary>
    /// PolyStrata (strata-poly) reads the GGUF in place and runs the MoE families on its own
    /// engine. Its expert-format support is close to the patched Strata engine's, plus Q4_0/Q4_1
    /// and the MTP draft block. This is a coarse check: unknown roles are noted, not blocked.
    /// </summary>
    private static async Task<CompatibilityReport> PolyStrataMatrixAsync(string modelPath, CancellationToken ct)
    {
        // One implementation: a second copy had already drifted in its notes and whitelist.
        var md = await Task.Run(() => GgufService.ReadShards(modelPath, ct), ct).ConfigureAwait(false);
        return PolyStrataCompatibility.Analyze(md);
    }

    public Task<EngineStatus> StatusAsync(EngineSession? session, CancellationToken ct) =>
        EngineSessionStatus.QueryAsync(session, ReadyAsync, ct);

    public LaunchPlan BuildLaunch(EngineStartRequest request)
    {
        var exe = ExecutablePath();
        var parameters = MergeParameters(request);
        var arguments = _manifest.Launch.Arguments
            .Select(arg => Substitute(arg, request, parameters))
            .ToList();
        var cwd = Substitute(_manifest.Launch.WorkingDirectory, request, parameters)
            .Replace("{installDir}", _installDir, StringComparison.OrdinalIgnoreCase);
        if (!Directory.Exists(cwd)) cwd = _installDir;
        var env = _manifest.Launch.Environment.ToDictionary(kv => kv.Key, kv => Substitute(kv.Value, request, parameters));
        return new LaunchPlan(exe, arguments, cwd, env, new Dictionary<string, string>());
    }

    private Dictionary<string, string> MergeParameters(EngineStartRequest request)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in _manifest.Parameters)
            if (spec.Default != null) parameters[spec.Key] = spec.Default;
        var settings = request.Config.Engines.GetValueOrDefault(_manifest.Id);
        if (settings != null)
            foreach (var (key, value) in settings.Parameters) parameters[key] = value;
        return parameters;
    }

    private string ResolveModel(ManagerConfig config)
    {
        var selected = config.Engines.GetValueOrDefault(_manifest.Id)?.ModelPath;
        return string.IsNullOrWhiteSpace(selected) ? config.ModelPath : selected;
    }

    private string Substitute(string template, EngineStartRequest request, IReadOnlyDictionary<string, string> parameters)
    {
        var result = template
            .Replace("{model}", ResolveModel(request.Config), StringComparison.OrdinalIgnoreCase)
            .Replace("{port}", ResolvePort(request).ToString(), StringComparison.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
            result = result.Replace("{" + key + "}", value, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    public async Task<EngineSession> StartAsync(EngineStartRequest request, ILogSink log, CancellationToken ct)
    {
        request = request with { Config = JsonSerializer.Deserialize<ManagerConfig>(JsonSerializer.Serialize(request.Config))! };
        EngineSession session;
        using (var gpu = _manifest.GpuHeavy ? await EngineArbiter.AcquireGpuStartLeaseAsync(ct, OnStartLeaseContention).ConfigureAwait(false) : null)
        using (await EngineArbiter.AcquireBackendLeaseAsync(_manifest.Id, ct).ConfigureAwait(false))
        {
            var latest = ConfigService.ReadConfig();
            EngineArbiter.EnsureGpuAvailable(latest, _manifest.Id, _manifest.GpuHeavy, displayName: _manifest.DisplayName);
            var recorded = latest.GetRunning(_manifest.Id);
            using (var existing = recorded == null ? null : ServerService.ValidateProcess(recorded))
                if (existing != null) throw new InvalidOperationException($"{_manifest.DisplayName} 已在运行，不会启动第二个实例。");
            var plan = BuildLaunch(request);
            var port = ResolvePort(request);
            var portProblem = port > 0
                ? ServerService.PortUnavailableReason(port, new LauncherArguments(string.Join(" ", plan.Arguments.Select(LauncherArguments.Quote))).Get("--host"))
                : null;
            if (portProblem != null) throw new InvalidOperationException(portProblem);
            var args = string.Join(" ", plan.Arguments.Select(LauncherArguments.Quote));
            log.Write($"启动插件引擎 {_manifest.Id}：{plan.FileName} {args}");
            ct.ThrowIfCancellationRequested();
            using var process = DetachedProcess.Start(plan.FileName, args,
                Path.Combine(ConfigService.GetManagerRoot(), "logs", $"engine-{_manifest.Id}.stdout.log"),
                Path.Combine(ConfigService.GetManagerRoot(), "logs", $"engine-{_manifest.Id}.stderr.log"), plan.WorkingDirectory, plan.Environment);
            ServerSession? captured = null;
            try
            {
                session = new EngineSession { BackendId = _manifest.Id, ProcessId = process.Id,
                    StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks, ExecutablePath = plan.FileName,
                    ModelPath = ResolveModel(request.Config), Arguments = args, Port = port };
                captured = ToServerSession(session);
                using (var verified = ServerService.ValidateProcess(captured))
                    if (verified == null) throw new InvalidOperationException("插件新进程身份验证失败。");
                if (BeforePublishAsync != null) await BeforePublishAsync(session, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                using (var verified = ServerService.ValidateProcess(captured))
                    if (verified == null) throw new InvalidOperationException("插件登记前进程身份已变化。");
                ConfigService.SaveSessionSlot(request.Config, _manifest.Id, captured);
            }
            catch
            {
                await EngineArbiter.CleanupUnpublishedAsync(process, captured, plan.FileName).ConfigureAwait(false);
                throw;
            }
        }
        await WaitReadyAsync(session, log, ct).ConfigureAwait(false);
        return session;
    }

    public async Task StopAsync(EngineSession session, ILogSink log)
    {
        using var lifecycle = await EngineArbiter.AcquireBackendLeaseAsync(_manifest.Id, CancellationToken.None).ConfigureAwait(false);
        Process? process = session.ProcessId > 0 ? ServerService.ValidateProcess(ToServerSession(session)) : null;
        if (process == null)
        {
            ConfigService.ClearSessionSlotIfMatches(_manifest.Id, ToServerSession(session));
            log.Write("记录的引擎进程已不存在，已清理记录。");
            return;
        }
        using (process)
        {
            log.Write($"正在停止 {_manifest.DisplayName}，PID {process.Id}。");
            process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) { throw new InvalidOperationException("引擎进程尚未退出，保留运行记录。", ex); }
        }
        ConfigService.ClearSessionSlotIfMatches(_manifest.Id, ToServerSession(session));
    }

    public Task<bool> ReadyAsync(EngineSession session, CancellationToken ct) =>
        BackendReadinessProbe.QueryAsync(session.Port, _manifest.Ready.Type, _manifest.Ready.Path, ct,
            new LauncherArguments(session.Arguments).Get("--host"));

    public int ResolvePort(EngineStartRequest request)
    {
        static int Checked(int value) => value is >= 1 and <= 65535
            ? value : throw new ArgumentOutOfRangeException(nameof(value), "端口必须为1–65535。");
        static int Parse(string value) => int.TryParse(value, out var port) ? Checked(port)
            : throw new ArgumentException("插件端口必须为整数。");
        if (request.Port is { } overridden)
        {
            Checked(overridden);
            if (!_manifest.Launch.Arguments.Concat(_manifest.Launch.Environment.Values)
                .Any(value => value.Contains("{port}", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("该插件启动参数/环境未包含 {port}，无法安全应用一次性端口覆盖。");
            return overridden;
        }
        var settings = request.Config.Engines.GetValueOrDefault(_manifest.Id);
        var configured = settings?.Parameters.LastOrDefault(p => p.Key.Equals("port", StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrWhiteSpace(configured)) return Parse(configured);
        if (settings?.Port is { } common && common != 0) return Checked(common);
        var declared = _manifest.Parameters.FirstOrDefault(p => p.Key.Equals("port", StringComparison.OrdinalIgnoreCase))?.Default;
        return !string.IsNullOrWhiteSpace(declared) ? Parse(declared) : Checked(request.Config.Port);
    }

    public string HealthUrl(EngineSession session) =>
        $"http://127.0.0.1:{session.Port}{_manifest.Ready.Path}";

    public void OpenWebUi(EngineSession session) =>
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{session.Port}") { UseShellExecute = true });

    private async Task WaitReadyAsync(EngineSession session, ILogSink log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_manifest.Ready.TimeoutSeconds);
        var expected = ToServerSession(session);
        while (DateTime.UtcNow < deadline)
        {
            LaunchSessionGuard.EnsureCurrent(_manifest.Id, expected, ct);
            var ready = await ReadyAsync(session, ct).ConfigureAwait(false);
            if (ready)
            {
                LaunchSessionGuard.EnsureCurrent(_manifest.Id, expected, ct);
                log.Write($"{_manifest.DisplayName} 已就绪：http://127.0.0.1:{session.Port}");
                return;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        log.Write($"{_manifest.Ready.TimeoutSeconds} 秒内 API 尚未就绪，进程仍在加载；可查看日志或停止服务。");
    }

    private static ServerSession ToServerSession(EngineSession session) => new()
    {
        ProcessId = session.ProcessId,
        StartTimeUtcTicks = session.StartTimeUtcTicks,
        EnginePath = session.ExecutablePath,
        ModelPath = session.ModelPath,
        Args = session.Arguments,
        Port = session.Port,
        Embedding = false,
    };
}
