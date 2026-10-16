using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager.Engines;

/// <summary>
/// Which engine a <see cref="StrataBackend"/> drives. Both Strata and PolyStrata are the same
/// shape: a native <c>*.exe</c> that speaks a line protocol, wrapped by the project's own
/// <c>serve/server.py</c>, which exposes the OpenAI/Anthropic HTTP API. Only the engine
/// executable's folder/name and the architecture differ.
/// </summary>
public sealed record StrataEngineProfile(
    string Id,
    string DisplayName,
    string Description,
    string EngineSubdir,
    string EngineExeName,
    string Arch,
    bool VerifiesFamilies);

/// <summary>
/// A Strata-family engine. It does not run a one-shot prompt pipeline; it starts the engine's
/// own OpenAI/Anthropic HTTP service
/// (<c>python -m serve.server --engine strata --config &lt;runconfig&gt; --port &lt;port&gt;</c>),
/// so any agent (pi, Claude Code, Codex) can attach to the base URL.
///
/// The engine reads the GGUF in place (no pack), so the only preparation is the tokenizer
/// directory the server needs (vocab.json / merges.txt / token_type.json).
/// </summary>
public sealed class StrataBackend : IServerBackend
{
    public static readonly StrataEngineProfile UpstreamProfile = new(
        "strata-upstream",
        "Strata (upstream q36)",
        "Strata 原生 qwen35moe 路径（原地读 GGUF，内置 MTP）；通过自带 HTTP 服务接入 agent。",
        "engine", "strata.exe", "qwen35moe", VerifiesFamilies: true);

    public static readonly StrataEngineProfile PolyProfile = new(
        "polystrata",
        "PolyStrata (strata-poly)",
        "基于 Strata 内核的 MoE 引擎（lookup/重复猜测 + 内置 MTP 草稿）；通过自带 HTTP 服务接入 agent。",
        "engine-poly", "strata-poly.exe", "qwen35moe", VerifiesFamilies: false);

    private readonly StrataEngineProfile _profile;

    public StrataBackend(StrataEngineProfile profile)
    {
        _profile = profile;
        Descriptor = new BackendDescriptor(profile.Id, profile.DisplayName, profile.Description,
            BuiltIn: true, GpuHeavy: true);
    }

    public BackendDescriptor Descriptor { get; }

    public IReadOnlyList<ParameterSpec> Parameters { get; } =
    [
        new("packageRoot", "引擎包目录", "路径", ParamType.FolderPath, "", "包含 engine\\strata.exe 与 serve\\server.py 的目录。"),
        new("serveRoot", "服务源码根", "路径", ParamType.FolderPath, "", "包含 serve\\server.py 的目录；留空则用包目录。"),
        new("pythonPath", "Python 解释器", "路径", ParamType.FilePath, "", "Python 3.10+ 的 python.exe；留空自动查找。"),
        new("servePort", "服务端口", "基本", ParamType.Integer, "8091", "HTTP API 端口（OpenAI/Anthropic）。", 1, 65535),
        new("host", "监听地址", "基本", ParamType.Text, "127.0.0.1", "默认仅本机回环。"),
        new("context", "上下文", "基本", ParamType.Integer, "4096", "--max-context。", 256, 1048576),
        new("vramReserveMib", "显存预留 MiB", "基本", ParamType.Integer, "700", "--vram-reserve-mib。", 0, 60000),
        new("poolWorkers", "CPU 池线程", "高级", ParamType.Integer, "0", "--pool-workers（0 自动）。", 0, 256),
        new("reasoningBudget", "reasoning 预算", "高级", ParamType.Integer, "0", "0 = 不限制。", 0, 65536),
        new("anthropicThinking", "Anthropic 思考", "高级", ParamType.Choice, "model", "未显式要求思考时的行为。", Choices: ["model", "on_request"]),
    ];

    /// <summary>
    /// Reads a setting from the backend's own EngineSettings, falling back to the legacy
    /// StrataConfig block so existing installs keep working. Empty values fall through.
    /// </summary>
    private string? Param(ManagerConfig config, string key)
    {
        var settings = config.Engines.GetValueOrDefault(_profile.Id);
        if (settings != null && settings.Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        return null;
    }

    private int ParamInt(ManagerConfig config, string key, int fallback)
    {
        var value = Param(config, key);
        return value != null && int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    public async Task<BackendStatus> ProbeAsync(CancellationToken ct)
    {
        var config = ConfigService.ReadConfig();
        var engine = ResolveEngine(config);
        if (engine == null)
            return BackendStatus.Unavailable($"未找到 {_profile.EngineExeName}；请在面板中选择引擎包目录，或用「初始化环境」下载。");
        if (!File.Exists(engine))
            return BackendStatus.Unavailable($"未找到 {_profile.EngineExeName}：" + engine);
        // Both checks spawn a child process; never run them on the UI thread.
        if (_profile.VerifiesFamilies)
        {
            var supportsQwen35 = await Task.Run(() => EngineSupportsQwen35(engine), ct).ConfigureAwait(false);
            if (!supportsQwen35)
                return BackendStatus.Unavailable("该 strata.exe 没有 qwen35moe 原生路径（需要上游 0.1.44+ 构建）。");
        }
        var server = ResolveServer(config);
        if (server == null)
            return BackendStatus.Unavailable("未找到 serve\\server.py；请设置服务源码根目录。");
        var python = await ResolvePythonAsync(config, ct).ConfigureAwait(false);
        if (python == null)
            return BackendStatus.Unavailable("未找到 Python 3.10+（服务需要）。");
        return new BackendStatus(true, engine, null, null);
    }

    public async Task<CompatibilityReport> InspectAsync(string modelPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return new CompatibilityReport(false, ["请先选择已下载完成的 GGUF 模型。"], [], "未选择模型。", null);
        var md = await Task.Run(() => GgufService.ReadShards(modelPath, ct), ct).ConfigureAwait(false);
        if (_profile.Id == PolyProfile.Id)
        {
            var poly = PolyStrataCompatibility.Analyze(md);
            return new CompatibilityReport(poly.Runnable, poly.Blockers, poly.Notes, poly.Summary,
                Estimate(md, modelPath));
        }
        var report = StrataCompatibility.Analyze(md);
        var estimate = Estimate(md, modelPath);
        return new CompatibilityReport(report.IsPackable, report.Blockers, report.Notes,
            StrataCompatibility.Summary(report), estimate);
    }

    public Task<EngineStatus> StatusAsync(EngineSession? session, CancellationToken ct)
    {
        if (session == null) return Task.FromResult(new EngineStatus(false, false, null, "未运行"));
        return Task.FromResult(new EngineStatus(true, false, session.Port, null));
    }

    public LaunchPlan BuildLaunch(EngineStartRequest request) =>
        BuildLaunch(request, TokenizerDir(request.Config, _profile.Id, ResolveModel(request.Config)));

    public LaunchPlan BuildLaunch(EngineStartRequest request, string tokenizer)
    {
        var config = request.Config;
        var model = ResolveModel(config);
        var engine = ResolveEngine(config) ?? throw new FileNotFoundException($"未找到 {_profile.EngineExeName}。");
        var server = ResolveServer(config) ?? throw new FileNotFoundException("未找到 serve\\server.py。");
        var python = ResolvePython(config) ?? throw new FileNotFoundException("未找到 Python 3.10+。");
        var port = ResolvePort(request);
        var host = Param(config, "host") ?? (string.IsNullOrWhiteSpace(config.Strata.Host) ? "127.0.0.1" : config.Strata.Host);

        var runconfig = BuildRunConfig(config, engine, model, tokenizer, port, host);
        var configPath = RunConfigPath(_profile.Id, model);

        var args = new List<string> { "-m", "serve.server", "--engine", "strata", "--config", configPath, "--port", port.ToString() };
        if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal)) { args.Add("--host"); args.Add(host); }

        var env = new Dictionary<string, string> { ["PYTHONUTF8"] = "1", ["PYTHONUNBUFFERED"] = "1" };
        var files = new Dictionary<string, string> { [configPath] = JsonSerializer.Serialize(runconfig, new JsonSerializerOptions { WriteIndented = true }) };
        // `python -m serve.server` resolves the `serve` package from the working directory, so
        // this must be the source root (serve/'s parent), not the serve/ folder itself.
        var cwd = Path.GetDirectoryName(Path.GetDirectoryName(server))!;
        return new LaunchPlan(python, args, cwd, env, files);
    }

    public async Task<EngineSession> StartAsync(EngineStartRequest request, ILogSink log, CancellationToken ct)
    {
        var model = ResolveModel(request.Config);
        // The server needs the tokenizer directory (vocab.json etc.); prepare it first so the
        // runconfig can point at the directory the tool actually produced.
        var server = ResolveServer(request.Config)!;
        var serveRoot = Path.GetDirectoryName(Path.GetDirectoryName(server))!;
        log.Write("准备分词表…");
        var tokenizer = await StrataPackService.EnsureTokenizerDirAsync(serveRoot, model,
            TokenizerDir(request.Config, _profile.Id, model),
            Param(request.Config, "pythonPath") ?? request.Config.Strata.PythonPath, log.Write, ct).ConfigureAwait(false);
        log.Write("分词表：" + tokenizer);
        var plan = BuildLaunch(request, tokenizer);
        // Materialize generated files (the runconfig) before launching.
        foreach (var (path, content) in plan.GeneratedFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        }
        var port = ResolvePort(request);
        if (ServerService.TestPortInUse(port))
            throw new InvalidOperationException($"端口 {port} 已被占用。请改用其它端口。");
        var args = string.Join(" ", plan.Arguments.Select(LauncherArguments.Quote));
        log.Write($"启动 {_profile.DisplayName} 服务：{plan.FileName} {args}");
        var process = DetachedProcess.Start(plan.FileName, args,
            Path.Combine(ConfigService.GetManagerRoot(), "logs", $"engine-{_profile.Id}.stdout.log"),
            Path.Combine(ConfigService.GetManagerRoot(), "logs", $"engine-{_profile.Id}.stderr.log"),
            plan.WorkingDirectory);
        var session = new EngineSession
        {
            BackendId = _profile.Id,
            ProcessId = process.Id,
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            ExecutablePath = plan.FileName,
            ModelPath = model,
            Arguments = args,
            Port = port,
            Extra = { ["runconfig"] = RunConfigPath(_profile.Id, model) },
        };
        process.Dispose();
        ConfigService.SaveSessionSlot(request.Config, _profile.Id, ToServerSession(session));
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
            ConfigService.SaveSessionSlot(ConfigService.ReadConfig(), _profile.Id, null);
            log.Write($"记录的 {_profile.DisplayName} 服务进程已不存在，已清理记录。");
            return;
        }
        using (process)
        {
            log.Write($"正在停止 {_profile.DisplayName} 服务，PID {process.Id}。");
            process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        ConfigService.SaveSessionSlot(ConfigService.ReadConfig(), _profile.Id, null);
    }

    public Task<bool> ReadyAsync(EngineSession session, CancellationToken ct) =>
        ServerService.TestApiReadyAsync(session.Port, ct);

    public int ResolvePort(EngineStartRequest request) =>
        ParamInt(request.Config, "servePort", request.Config.Strata.ServePort);

    public string HealthUrl(EngineSession session) => $"http://127.0.0.1:{session.Port}/health";

    public void OpenWebUi(EngineSession session) =>
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{session.Port}") { UseShellExecute = true });

    // ---- helpers ----

    private async Task WaitReadyAsync(EngineSession session, ILogSink log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var process = TryGetProcess(session);
            if (process is { HasExited: true })
                throw new InvalidOperationException($"{_profile.DisplayName} 服务提前退出。请查看 logs\\engine-{_profile.Id}.stderr.log。");
            if (await ServerService.TestApiReadyAsync(session.Port, ct).ConfigureAwait(false))
            {
                log.Write($"{_profile.DisplayName} 服务已就绪：http://127.0.0.1:{session.Port}/v1");
                return;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        log.Write("180 秒内 API 尚未就绪，进程仍在加载；可查看日志或停止服务。");
    }

    private static Process? TryGetProcess(EngineSession session)
    {
        try { return Process.GetProcessById(session.ProcessId); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    private Dictionary<string, object> BuildRunConfig(ManagerConfig config, string engine, string model,
        string tokenizer, int port, string host)
    {
        var s = config.Strata;
        var context = ParamInt(config, "context", s.Context);
        var workers = ParamInt(config, "poolWorkers", s.Workers);
        var reserve = ParamInt(config, "vramReserveMib", 700);
        var args = new List<string> { "--native", model, "--max-context", context.ToString() };
        if (s.Prefill > 0) { args.Add("--prefill"); args.Add(s.Prefill.ToString()); }
        if (s.MaxNew > 0) { args.Add("--max-new"); args.Add(s.MaxNew.ToString()); }
        if (workers > 0) { args.Add("--pool-workers"); args.Add(workers.ToString()); }
        if (reserve > 0) { args.Add("--vram-reserve-mib"); args.Add(reserve.ToString()); }
        var cfg = new Dictionary<string, object>
        {
            ["exe"] = engine,
            ["args"] = args,
            ["cwd"] = Path.GetDirectoryName(engine)!,
            ["tokenizer"] = tokenizer,
            ["model_name"] = Path.GetFileNameWithoutExtension(model).ToLowerInvariant(),
            ["arch"] = _profile.Arch,
            ["log"] = Path.Combine(ConfigService.GetManagerRoot(), "logs", $"engine-{_profile.Id}.log"),
            ["port"] = port,
            ["host"] = host,
        };
        var reasoning = ParamInt(config, "reasoningBudget", s.ReasoningBudgetTokens);
        if (reasoning > 0) cfg["reasoning_budget_tokens"] = reasoning;
        var thinking = Param(config, "anthropicThinking") ?? s.AnthropicThinking;
        if (!string.IsNullOrWhiteSpace(thinking)) cfg["anthropic_thinking"] = thinking;
        return cfg;
    }

    private static string RunConfigPath(string backendId, string model) =>
        Path.Combine(DataDir(backendId, model), "strata-runconfig.json");

    /// <summary>The tokenizer directory the server reads (vocab.json etc.); prepared from the GGUF.</summary>
    public static string TokenizerDir(ManagerConfig config, string model) => TokenizerDir(config, "strata-upstream", model);

    public static string TokenizerDir(ManagerConfig config, string backendId, string model) =>
        Path.Combine(DataDir(backendId, model), "tokenizer");

    private static string DataDir(string backendId, string model)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(PathIdentity.Normalize(model))))[..16];
        return Path.Combine(ConfigService.GetManagerRoot(), "data", backendId, hash);
    }

    /// <summary>Resolves the engine exe for the upstream profile (legacy static entry point).</summary>
    public static string? ResolveEngine(ManagerConfig config) => ResolveEngine(config, UpstreamProfile);

    public static string? ResolveEngine(ManagerConfig config, StrataEngineProfile profile)
    {
        var root = Param(config, profile.Id, "packageRoot") ?? config.Strata.PackageRoot;
        if (string.IsNullOrWhiteSpace(root)) return null;
        var engine = Path.Combine(Path.GetFullPath(root), profile.EngineSubdir, profile.EngineExeName);
        return File.Exists(engine) ? engine : null;
    }

    /// <summary>Resolves serve/server.py for the upstream profile (legacy static entry point).</summary>
    public static string? ResolveServer(ManagerConfig config) => ResolveServer(config, UpstreamProfile);

    public static string? ResolveServer(ManagerConfig config, StrataEngineProfile profile)
    {
        var serveRoot = Param(config, profile.Id, "serveRoot") ?? config.Strata.ServeRoot;
        var packageRoot = Param(config, profile.Id, "packageRoot") ?? config.Strata.PackageRoot;
        foreach (var root in new[] { serveRoot, packageRoot })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var server = Path.Combine(Path.GetFullPath(root), "serve", "server.py");
            if (File.Exists(server)) return server;
        }
        return null;
    }

    internal static string? ResolvePython(ManagerConfig config) => ResolvePython(config, UpstreamProfile);

    internal static string? ResolvePython(ManagerConfig config, StrataEngineProfile profile)
    {
        var configured = Param(config, profile.Id, "pythonPath") ?? config.Strata.PythonPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;
        foreach (var candidate in new[] { "python", "py" })
        {
            var path = FindOnPath(candidate);
            if (path != null) return path;
        }
        return null;
    }

    private static async Task<string?> ResolvePythonAsync(ManagerConfig config, CancellationToken ct)
    {
        var direct = ResolvePython(config);
        if (direct == null) return null;
        Process? process = null;
        try
        {
            var start = StrataService.CaptureStart(direct);
            if (Path.GetFileNameWithoutExtension(direct).Equals("py", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add("-3");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import sys; assert sys.version_info>=(3,10)");
            process = Process.Start(start);
            if (process == null) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            return process.ExitCode == 0 ? direct : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        finally
        {
            try { if (process is { HasExited: false }) process.Kill(true); } catch { }
            process?.Dispose();
        }
    }

    private static string? FindOnPath(string name)
    {
        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';');
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in pathExt)
            {
                var candidate = Path.Combine(dir, name + ext.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    internal static string ResolveModel(ManagerConfig config) => ResolveModel(config, UpstreamProfile);

    internal static string ResolveModel(ManagerConfig config, StrataEngineProfile profile)
    {
        // The engine page stores the model in the backend's settings; the legacy Strata block
        // is the fallback for installs that predate it.
        var model = config.Engines.GetValueOrDefault(profile.Id)?.ModelPath;
        if (string.IsNullOrWhiteSpace(model)) model = config.Strata.ModelPath;
        if (!string.IsNullOrWhiteSpace(model) && File.Exists(model)) return Path.GetFullPath(model);
        throw new FileNotFoundException("请先选择已下载完成的 GGUF 模型。", model);
    }

    /// <summary>Reads a backend's own parameter, falling back to the legacy Strata block.</summary>
    private static string? Param(ManagerConfig config, string backendId, string key)
    {
        var settings = config.Engines.GetValueOrDefault(backendId);
        if (settings != null && settings.Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        return null;
    }

    public static bool EngineSupportsQwen35(string strataExe) => StrataService.EngineSupportsQwen35(strataExe);

    private static ResourceEstimate? Estimate(GgufMetadata md, string modelPath)
    {
        try { return ResourceEstimator.ForGguf(modelPath, md.LayerCount); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private ServerSession ToServerSession(EngineSession session) => new()
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
