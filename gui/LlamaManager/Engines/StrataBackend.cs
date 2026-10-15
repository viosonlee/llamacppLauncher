using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager.Engines;

/// <summary>
/// The Strata engine (Niko 0.1.44.1 + APEX patches, "strata-upstream"). It does not run
/// a one-shot prompt pipeline; it starts the engine's own OpenAI/Anthropic HTTP service
/// (<c>python -m serve.server --engine strata --config &lt;runconfig&gt; --port &lt;port&gt;</c>),
/// so any agent (pi, Claude Code, Codex) can attach to the base URL.
///
/// The engine reads the GGUF in place (no pack), so the only preparation is the tokenizer
/// directory the server needs (vocab.json / merges.txt / token_type.json).
/// </summary>
public sealed class StrataBackend : IServerBackend
{
    private const string Id = "strata-upstream";

    public BackendDescriptor Descriptor { get; } = new(
        Id,
        "Strata (upstream q36)",
        "Strata 原生 qwen35moe 路径（原地读 GGUF，内置 MTP）；通过自带 HTTP 服务接入 agent。",
        BuiltIn: true,
        GpuHeavy: true);

    public IReadOnlyList<ParameterSpec> Parameters { get; } =
    [
        new("servePort", "服务端口", "基本", ParamType.Integer, "8091", "HTTP API 端口（OpenAI/Anthropic）。", 1, 65535),
        new("host", "监听地址", "基本", ParamType.Text, "127.0.0.1", "默认仅本机回环。"),
        new("context", "上下文", "基本", ParamType.Integer, "4096", "--max-context。", 256, 1048576),
        new("vramReserveMib", "显存预留 MiB", "基本", ParamType.Integer, "700", "--vram-reserve-mib。", 0, 60000),
        new("poolWorkers", "CPU 池线程", "高级", ParamType.Integer, "0", "--pool-workers（0 自动）。", 0, 256),
        new("reasoningBudget", "reasoning 预算", "高级", ParamType.Integer, "0", "0 = 不限制。", 0, 65536),
        new("anthropicThinking", "Anthropic 思考", "高级", ParamType.Choice, "model", "未显式要求思考时的行为。", Choices: ["model", "on_request"]),
    ];

    public async Task<BackendStatus> ProbeAsync(CancellationToken ct)
    {
        var config = ConfigService.ReadConfig();
        var engine = ResolveEngine(config);
        if (engine == null)
            return BackendStatus.Unavailable("未找到 strata.exe；请在面板中选择 Strata 包目录。");
        if (!File.Exists(engine))
            return BackendStatus.Unavailable("未找到 strata.exe：" + engine);
        if (!EngineSupportsQwen35(engine))
            return BackendStatus.Unavailable("该 strata.exe 没有 qwen35moe 原生路径（需要上游 0.1.44+ 构建）。");
        var server = ResolveServer(config);
        if (server == null)
            return BackendStatus.Unavailable("未找到 serve\\server.py；请设置 Strata 源码根目录。");
        var python = await ResolvePythonAsync(config, ct).ConfigureAwait(false);
        if (python == null)
            return BackendStatus.Unavailable("未找到 Python 3.10+（Strata 服务需要）。");
        return new BackendStatus(true, engine, null, null);
    }

    public async Task<CompatibilityReport> InspectAsync(string modelPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return new CompatibilityReport(false, ["请先选择已下载完成的 GGUF 模型。"], [], "未选择模型。", null);
        var md = await Task.Run(() => GgufService.ReadShards(modelPath, ct), ct).ConfigureAwait(false);
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

    public LaunchPlan BuildLaunch(EngineStartRequest request)
    {
        var config = request.Config;
        var s = config.Strata;
        var model = ResolveModel(config);
        var engine = ResolveEngine(config) ?? throw new FileNotFoundException("未找到 strata.exe。");
        var server = ResolveServer(config) ?? throw new FileNotFoundException("未找到 serve\\server.py。");
        var python = ResolvePython(config) ?? throw new FileNotFoundException("未找到 Python 3.10+。");
        var port = s.ServePort;
        var host = string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host;

        var tokenizer = TokenizerDir(config, model);
        var runconfig = BuildRunConfig(config, engine, model, tokenizer, port, host);
        var configPath = RunConfigPath(model);

        var args = new List<string> { "-m", "serve.server", "--engine", "strata", "--config", configPath, "--port", port.ToString() };
        if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal)) { args.Add("--host"); args.Add(host); }

        var env = new Dictionary<string, string> { ["PYTHONUTF8"] = "1", ["PYTHONUNBUFFERED"] = "1" };
        var files = new Dictionary<string, string> { [configPath] = JsonSerializer.Serialize(runconfig, new JsonSerializerOptions { WriteIndented = true }) };
        var cwd = Path.GetDirectoryName(server)!;
        return new LaunchPlan(python, args, cwd, env, files);
    }

    public async Task<EngineSession> StartAsync(EngineStartRequest request, ILogSink log, CancellationToken ct)
    {
        var plan = BuildLaunch(request);
        var model = ResolveModel(request.Config);
        // The server needs the tokenizer directory (vocab.json etc.) next to the runconfig.
        var server = ResolveServer(request.Config)!;
        var serveRoot = Path.GetDirectoryName(Path.GetDirectoryName(server))!;
        log.Write("准备分词表…");
        await StrataPackService.EnsureTokenizerDirAsync(serveRoot, model, TokenizerDir(request.Config, model),
            request.Config.Strata.PythonPath, log.Write, ct).ConfigureAwait(false);
        // Materialize generated files (the runconfig) before launching.
        foreach (var (path, content) in plan.GeneratedFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        }
        var port = request.Config.Strata.ServePort;
        if (ServerService.TestPortInUse(port))
            throw new InvalidOperationException($"端口 {port} 已被占用。请改用其它端口。");
        var args = string.Join(" ", plan.Arguments.Select(LauncherArguments.Quote));
        log.Write($"启动 Strata 服务：{plan.FileName} {args}");
        var process = DetachedProcess.Start(plan.FileName, args,
            Path.Combine(ConfigService.GetManagerRoot(), "logs", "strata-serve.stdout.log"),
            Path.Combine(ConfigService.GetManagerRoot(), "logs", "strata-serve.stderr.log"));
        var session = new EngineSession
        {
            BackendId = Id,
            ProcessId = process.Id,
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            ExecutablePath = plan.FileName,
            ModelPath = model,
            Arguments = args,
            Port = port,
            Extra = { ["runconfig"] = RunConfigPath(model) },
        };
        process.Dispose();
        ConfigService.SaveSession(request.Config, ToServerSession(session));
        await WaitReadyAsync(session, log, ct).ConfigureAwait(false);
        return session;
    }

    public async Task StopAsync(EngineSession session, ILogSink log)
    {
        var slot = ManagerConfig.SlotMain;
        var recorded = ConfigService.ReadConfig().GetRunning(slot);
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
            ConfigService.SaveSession(ConfigService.ReadConfig(), null);
            log.Write("记录的 Strata 服务进程已不存在，已清理记录。");
            return;
        }
        using (process)
        {
            log.Write($"正在停止 Strata 服务，PID {process.Id}。");
            process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        ConfigService.SaveSession(ConfigService.ReadConfig(), null);
    }

    public Task<bool> ReadyAsync(EngineSession session, CancellationToken ct) =>
        ServerService.TestApiReadyAsync(session.Port, ct);

    public int ResolvePort(EngineStartRequest request) => request.Config.Strata.ServePort;

    public string HealthUrl(EngineSession session) => $"http://127.0.0.1:{session.Port}/health";

    public void OpenWebUi(EngineSession session) =>
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{session.Port}") { UseShellExecute = true });

    // ---- helpers ----

    private static async Task WaitReadyAsync(EngineSession session, ILogSink log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var process = TryGetProcess(session);
            if (process is { HasExited: true })
                throw new InvalidOperationException("Strata 服务提前退出。请查看 logs\\strata-serve.stderr.log。");
            if (await ServerService.TestApiReadyAsync(session.Port, ct).ConfigureAwait(false))
            {
                log.Write($"Strata 服务已就绪：http://127.0.0.1:{session.Port}/v1");
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
        var args = new List<string> { "--native", model, "--max-context", s.Context.ToString() };
        if (s.Prefill > 0) { args.Add("--prefill"); args.Add(s.Prefill.ToString()); }
        if (s.MaxNew > 0) { args.Add("--max-new"); args.Add(s.MaxNew.ToString()); }
        if (s.Workers > 0) { args.Add("--pool-workers"); args.Add(s.Workers.ToString()); }
        var cfg = new Dictionary<string, object>
        {
            ["exe"] = engine,
            ["args"] = args,
            ["cwd"] = Path.GetDirectoryName(engine)!,
            ["tokenizer"] = tokenizer,
            ["model_name"] = Path.GetFileNameWithoutExtension(model).ToLowerInvariant(),
            ["arch"] = "qwen35moe",
            ["log"] = Path.Combine(ConfigService.GetManagerRoot(), "logs", "strata-serve-engine.log"),
            ["port"] = port,
            ["host"] = host,
        };
        if (s.ReasoningBudgetTokens > 0) cfg["reasoning_budget_tokens"] = s.ReasoningBudgetTokens;
        if (!string.IsNullOrWhiteSpace(s.AnthropicThinking)) cfg["anthropic_thinking"] = s.AnthropicThinking;
        return cfg;
    }

    private static string RunConfigPath(string model)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(PathIdentity.Normalize(model))))[..16];
        return Path.Combine(ConfigService.GetManagerRoot(), "data", "strata-serve", hash, "strata-runconfig.json");
    }

    /// <summary>The tokenizer directory the server reads (vocab.json etc.); prepared from the GGUF.</summary>
    public static string TokenizerDir(ManagerConfig config, string model)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(PathIdentity.Normalize(model))))[..16];
        return Path.Combine(ConfigService.GetManagerRoot(), "data", "strata-serve", hash, "tokenizer");
    }

    public static string? ResolveEngine(ManagerConfig config)
    {
        var root = config.Strata.PackageRoot;
        if (string.IsNullOrWhiteSpace(root)) return null;
        var engine = Path.Combine(Path.GetFullPath(root), "engine", "strata.exe");
        return File.Exists(engine) ? engine : null;
    }

    public static string? ResolveServer(ManagerConfig config)
    {
        foreach (var root in new[] { config.Strata.ServeRoot, config.Strata.PackageRoot })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var server = Path.Combine(Path.GetFullPath(root), "serve", "server.py");
            if (File.Exists(server)) return server;
        }
        return null;
    }

    internal static string? ResolvePython(ManagerConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Strata.PythonPath) && File.Exists(config.Strata.PythonPath))
            return config.Strata.PythonPath;
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
        try
        {
            var start = StrataService.CaptureStart(direct);
            if (Path.GetFileNameWithoutExtension(direct).Equals("py", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add("-3");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import sys; assert sys.version_info>=(3,10)");
            using var process = Process.Start(start);
            if (process == null) return null;
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0 ? direct : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
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

    internal static string ResolveModel(ManagerConfig config)
    {
        var model = config.Strata.ModelPath;
        if (!string.IsNullOrWhiteSpace(model) && File.Exists(model)) return Path.GetFullPath(model);
        throw new FileNotFoundException("请先选择已下载完成的 GGUF 模型。", model);
    }

    public static bool EngineSupportsQwen35(string strataExe) => StrataService.EngineSupportsQwen35(strataExe);

    private static ResourceEstimate? Estimate(GgufMetadata md, string modelPath)
    {
        try { return ResourceEstimator.ForGguf(modelPath, md.LayerCount); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
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
