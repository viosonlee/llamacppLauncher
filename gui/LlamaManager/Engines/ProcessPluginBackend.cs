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
        var exe = ExecutablePath();
        if (!File.Exists(exe)) return BackendStatus.Unavailable("未找到引擎可执行文件：" + exe);
        if (_manifest.Detect == null || _manifest.Detect.VersionArg.Count == 0)
            return new BackendStatus(true, exe, null, null);
        return await Task.Run(() =>
        {
            Process? process = null;
            try
            {
                var start = StrataService.CaptureStart(exe);
                foreach (var arg in _manifest.Detect.VersionArg) start.ArgumentList.Add(arg);
                process = Process.Start(start);
                if (process == null) return new BackendStatus(true, exe, null, null);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(5000)) return new BackendStatus(true, exe, null, null);
                var text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                var version = _manifest.Detect.VersionRegex.Length > 0
                    ? Regex.Match(text, _manifest.Detect.VersionRegex) is { Success: true } m ? m.Groups[1].Value : null
                    : null;
                return new BackendStatus(true, exe, version, null);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return BackendStatus.Unavailable("探测失败：" + ex.Message);
            }
            finally
            {
                try { if (process is { HasExited: false }) process.Kill(true); } catch { }
                process?.Dispose();
            }
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
        var md = await Task.Run(() => GgufService.ReadShards(modelPath, ct), ct).ConfigureAwait(false);
        var notes = new List<string>();
        var blockers = new List<string>();
        var supported = new HashSet<uint> { 2, 3, 6, 7, 8, 11, 12, 13, 14, 16, 17, 18, 20, 21, 22, 23, 29, 42 };
        foreach (var (name, type) in md.TensorFormats)
        {
            if (!name.Contains("_exps.weight")) continue;
            if (!supported.Contains(type))
                blockers.Add($"{name} 使用 {StrataCompatibility.TypeName(type)}：strata-poly 可能不支持该专家格式。");
        }
        if (md.HasMtp) notes.Add("含内置 MTP 草稿块：strata-poly 会启用草稿解码。");
        else notes.Add("无 MTP 草稿块：strata-poly 仅用重复/lookup 猜测。");
        notes.Add("strata-poly 使用专家缓存；6GB 卡低于其 8GB 最低要求，速度可能受限。");
        var summary = blockers.Count == 0
            ? $"PolyStrata 预检通过：{md.Architecture} / {md.LayerCount} 层。"
            : $"PolyStrata 预检发现问题（{blockers.Count} 项）。";
        return new CompatibilityReport(blockers.Count == 0, blockers, notes, summary, null);
    }

    public Task<EngineStatus> StatusAsync(EngineSession? session, CancellationToken ct)
    {
        if (session == null) return Task.FromResult(new EngineStatus(false, false, null, "未运行"));
        return Task.FromResult(new EngineStatus(true, false, session.Port, null));
    }

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

    private string Substitute(string template, EngineStartRequest request, IReadOnlyDictionary<string, string> parameters)
    {
        var result = template
            .Replace("{model}", request.Config.ModelPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{port}", ResolvePort(request).ToString(), StringComparison.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
            result = result.Replace("{" + key + "}", value, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    public async Task<EngineSession> StartAsync(EngineStartRequest request, ILogSink log, CancellationToken ct)
    {
        var plan = BuildLaunch(request);
        var port = ResolvePort(request);
        if (port > 0 && ServerService.TestPortInUse(port))
            throw new InvalidOperationException($"端口 {port} 已被占用。请改用其它端口。");
        var args = string.Join(" ", plan.Arguments.Select(LauncherArguments.Quote));
        log.Write($"启动插件引擎 {_manifest.Id}：{plan.FileName} {args}");
        var logName = _manifest.Id;
        var process = DetachedProcess.Start(plan.FileName, args,
            Path.Combine(ConfigService.GetManagerRoot(), "logs", $"engine-{logName}.stdout.log"),
            Path.Combine(ConfigService.GetManagerRoot(), "logs", $"engine-{logName}.stderr.log"));
        var session = new EngineSession
        {
            BackendId = _manifest.Id,
            ProcessId = process.Id,
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            ExecutablePath = plan.FileName,
            ModelPath = request.Config.ModelPath,
            Arguments = args,
            Port = port,
        };
        process.Dispose();
        ConfigService.SaveSessionSlot(request.Config, _manifest.Id, ToServerSession(session));
        await WaitReadyAsync(session, log, ct).ConfigureAwait(false);
        return session;
    }

    public async Task StopAsync(EngineSession session, ILogSink log)
    {
        Process? process = null;
        try
        {
            if (session.ProcessId > 0) process = Process.GetProcessById(session.ProcessId);
            if (process != null && (process.HasExited ||
                process.StartTime.ToUniversalTime().Ticks != session.StartTimeUtcTicks))
            {
                process.Dispose();
                process = null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process = null;
        }
        if (process == null)
        {
            ConfigService.SaveSessionSlot(ConfigService.ReadConfig(), _manifest.Id, null);
            log.Write("记录的引擎进程已不存在，已清理记录。");
            return;
        }
        using (process)
        {
            log.Write($"正在停止 {_manifest.DisplayName}，PID {process.Id}。");
            process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        ConfigService.SaveSessionSlot(ConfigService.ReadConfig(), _manifest.Id, null);
    }

    public Task<bool> ReadyAsync(EngineSession session, CancellationToken ct) =>
        ServerService.TestApiReadyAsync(session.Port, ct);

    public int ResolvePort(EngineStartRequest request)
    {
        var parameters = MergeParameters(request);
        if (parameters.TryGetValue("port", out var port) && int.TryParse(port, out var value)) return value;
        var settings = request.Config.Engines.GetValueOrDefault(_manifest.Id);
        return settings?.Port ?? 0;
    }

    public string HealthUrl(EngineSession session) =>
        $"http://127.0.0.1:{session.Port}{_manifest.Ready.Path}";

    public void OpenWebUi(EngineSession session) =>
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{session.Port}") { UseShellExecute = true });

    private async Task WaitReadyAsync(EngineSession session, ILogSink log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_manifest.Ready.TimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (ProcessGone(session))
                throw new InvalidOperationException($"{_manifest.DisplayName} 提前退出。请查看 logs\\engine-{_manifest.Id}.stderr.log。");
            var ready = _manifest.Ready.Type.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                ? ServerService.TestPortInUse(session.Port)
                : await ServerService.TestApiReadyAsync(session.Port, ct).ConfigureAwait(false);
            if (ready) { log.Write($"{_manifest.DisplayName} 已就绪：http://127.0.0.1:{session.Port}"); return; }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        log.Write($"{_manifest.Ready.TimeoutSeconds} 秒内 API 尚未就绪，进程仍在加载；可查看日志或停止服务。");
    }

    private static bool ProcessGone(EngineSession session)
    {
        try { using var process = Process.GetProcessById(session.ProcessId); return process.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return true; }
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
