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
        var isPoly = profile.Id == PolyProfile.Id;
        var list = new List<ParameterSpec>
        {
            new("packageRoot", "引擎包目录", "路径", ParamType.FolderPath, "",
                "包含 engine\\" + profile.EngineExeName + " 与 serve\\server.py 的根目录。留空自动匹配默认目录。",
                Help: "【引擎包目录】\n• 作用：Strata 引擎二进制与配套服务脚本所在的根目录。\n• 建议：留空，系统会自动匹配 engines/strata-upstream 或 engines/polystrata。\n• 包含：engine\\" + profile.EngineExeName + " 与 serve\\server.py。"),
            new("serveRoot", "服务源码根", "路径", ParamType.FolderPath, "",
                "包含 serve\\server.py 的 Python 服务目录；留空默认使用引擎包目录。",
                Help: "【服务源码根】\n• 作用：提供 OpenAI / Anthropic 兼容 HTTP API 的 Python 服务源码根目录。\n• 建议：留空即可，默认使用“引擎包目录”。"),
            new("pythonPath", "Python 解释器", "路径", ParamType.FilePath, "",
                "Python 3.10+ 的 python.exe；留空自动使用一键配置好的专属虚拟环境。",
                Help: "【Python 解释器】\n• 作用：启动 API 服务端所需的 Python 3.10+ 可执行文件。\n• 建议：留空即可，系统会使用「一键配置环境」准备好的专属虚拟环境。"),
            new("servePort", "服务端口", "基本", ParamType.Integer, "8091",
                "Strata HTTP API 监听端口（默认 8091，供 Claude Code / Agent 客户端接入）。", 1, 65535,
                Help: "【服务端口】\n• 作用：Strata HTTP API 监听端口，外部客户端通过它连接模型。\n• 默认：8091（已避开 llama.cpp 的 8080）。\n• 冲突时改成 1024–65535 内任意空闲端口。",
                RangeHint: "1–65535，推荐 8091"),
            new("host", "监听地址", "基本", ParamType.Text, "127.0.0.1",
                "监听 IP 地址。默认 127.0.0.1 仅本机安全访问；设为 0.0.0.0 允许局域网设备访问。",
                Help: "【监听地址】\n• 作用：HTTP API 监听哪个网络地址。\n• 127.0.0.1（默认）：仅本机访问，最安全。\n• 0.0.0.0：允许局域网内其他设备调用，注意防火墙与鉴权。"),
            new("context", "上下文", "基本", ParamType.Integer, "4096",
                "单次最大上下文 Token 数（--max-context）。显存紧张设 2048~4096，显存充裕可设 8192 或更大。", 256, 1048576,
                Help: "【上下文】\n• 作用：引擎单次能处理的上下文 Token 上限（--max-context）。\n• 显存：上下文越大，KV 缓存占用越多。\n• 参考：6GB 显存建议 2048–4096；8–12GB 建议 4096–8192；16GB+ 可到 16384。",
                Unit: "tokens", RangeHint: "256–1,048,576，推荐 4096 / 8192"),
        };
        if (isPoly)
        {
            list.Add(new("vramReserveMib", "显存预留", "基本", ParamType.Integer, "256",
                "为桌面与显卡驱动预留的安全显存量（--vram-reserve-mib，默认 256）。", 0, 60000,
                Help: "【显存预留】\n• 作用：引擎分配显存时强行预留指定 MiB（--vram-reserve-mib），剩余显存用于缓存 MoE 专家。\n• 为什么需要：防止显存被 100% 占满导致桌面黑屏或驱动重置。\n• 参考：单显卡或笔记本独显推荐 256–500 MiB；双显卡可设 256 MiB 腾出更多显存。",
                Unit: "MiB", RangeHint: "0–60,000，推荐 256"));
            list.Add(new("tailSkip", "尾部跳过层", "基本", ParamType.Integer, "0",
                "末尾若干层交回 GPU/CPU 路由处理（--tail-skip）。0 表示不启用。", 0, 64,
                Help: "【尾部跳过层】\n• 作用：让末尾 N 层改用另一条执行路径（--tail-skip）。\n• 实测：PolyStrata 会读取本参数并在日志中确认；0 关闭。\n• 建议：先用 0，确认速度瓶颈后再逐层尝试。",
                RangeHint: "0–64，推荐 0"));
        }
        else
        {
            list.Add(new("prefill", "预填充批大小", "基本", ParamType.Integer, "2048",
                "预填充阶段每批处理的 Token 数（--prefill）。", 1, 32768,
                Help: "【预填充批大小】\n• 作用：处理输入提示时每批的 Token 数（--prefill）。\n• 影响：越大首字延迟的平稳性越好，但峰值显存更高。\n• 建议：保持 2048；显存紧张可降到 512–1024。",
                Unit: "tokens", RangeHint: "1–32,768，推荐 2048"));
            list.Add(new("maxNew", "单次生成上限", "基本", ParamType.Integer, "256",
                "单次请求最多生成的 Token 数（--max-new）。", 1, 131072,
                Help: "【单次生成上限】\n• 作用：单次请求允许生成的最大 Token 数（--max-new）。\n• 建议：256 适合对话；写作类可调到 1024 以上。",
                Unit: "tokens", RangeHint: "1–131,072，推荐 256"));
            list.Add(new("mtpDrafts", "MTP 草稿数", "基本", ParamType.Integer, "2",
                "每步投机采样的草稿 Token 数（--mtp，默认 2）。设为 0 关闭投机加速。", 0, 7,
                Help: "【MTP 草稿数】\n• 作用：每步投机采样同时预测的草稿 Token 数（--mtp）。\n• 原理：带 MTP 头的模型一次可预测多个未来 Token，命中即一次输出多个，实测约 1.8–2.5x 解码加速。\n• 建议：2（默认）兼顾接受率与吞吐；0 关闭投机采样。",
                VisibleWhen: "model.mtp", RangeHint: "0–7，推荐 2"));
            list.Add(new("expertSlots", "显存专家槽位", "基本", ParamType.Integer, "16",
                "每层常驻显存的专家槽位数（--expert-slots，默认 16）。", 0, 256,
                Help: "【显存专家槽位】\n• 作用：每个 MoE 层常驻显存的专家槽位数（--expert-slots）。\n• 原理：高频专家预载进显存，未命中的专家走 CPU 线程池，提升 GPU 命中率。\n• 参考：6GB 显存约 16（约 0.75 GiB）；8–12GB 可从 32 试起；16GB+ 继续增大并看日志。\n• 0：交给引擎自动计算。上下文 > 8192 且未显式设置时同样按 0 处理，避免 40K 上下文在 6GB 卡上换页掉速。",
                VisibleWhen: "model.moe", RangeHint: "0–256，推荐 16（4K 上下文）/ 0（>8K）"));
        }
        list.Add(new("poolWorkers", "CPU 池线程", "高级", ParamType.Integer, "0",
            "CPU 密集任务工作线程数（--pool-workers）。0 表示由引擎自动分配。", 0, 256,
            Help: "【CPU 池线程】\n• 作用：CPU 侧辅助计算（分词调度、候选搜索等）的线程池大小（--pool-workers）。\n• 建议：默认 0，由引擎按物理核心自动分配。",
            Advanced: true, RangeHint: "0–256，推荐 0（自动）"));
        list.Add(new("reasoningBudget", "reasoning 预算", "高级", ParamType.Integer, "0",
            "思考模型单次推理最大 Token 预算。0 为不限制。", 0, 65536,
            Help: "【reasoning 预算】\n• 作用：限制带思维链的模型单次思考输出的 Token 上限。\n• 0（默认）：不设上限，由模型自主推理完再作答。\n• 正数（如 1024、2048）：限制思考深度，换取更快响应。",
            Advanced: true, Unit: "tokens", RangeHint: "0–65,536，0 表示不限"));
        list.Add(new("anthropicThinking", "Anthropic 思考", "高级", ParamType.Choice, "model",
            "Anthropic 协议客户端未要求思考时的行为。",
            Help: "【Anthropic 协议思考行为】\n• 作用：Anthropic 客户端未显式指定 thinking 时的默认行为。\n• 跟随模型（默认）：按模型自身设定决定是否思考。\n• 按客户端请求：仅当请求中显式包含 thinking 时才思考。",
            Choices:
            [
                new ParamChoice("model", "跟随模型", "由模型的系统提示与思考模板决定是否思考（默认）。", Recommended: true),
                new ParamChoice("on_request", "按客户端请求", "仅在 Anthropic 客户端显式要求思考时才产生思考内容。"),
            ],
            Advanced: true));
        Parameters = list;
    }

    public BackendDescriptor Descriptor { get; }

    public IReadOnlyList<ParameterSpec> Parameters { get; }

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

    // The panel shows what the engine will actually receive. Fields that predate the schema
    // panel still resolve from the legacy StrataConfig block, so read them from there when the
    // user has not overridden them yet. Empty means "fall back to the declared default".
    internal string? LegacyPrefill(ManagerConfig config) => config.Strata.Prefill > 0 ? config.Strata.Prefill.ToString() : null;

    internal string? LegacyMaxNew(ManagerConfig config) => config.Strata.MaxNew > 0 ? config.Strata.MaxNew.ToString() : null;

    internal string? LegacyWorkers(ManagerConfig config) => config.Strata.Workers > 0 ? config.Strata.Workers.ToString() : null;

    internal string? LegacyReasoningBudget(ManagerConfig config) =>
        config.Strata.ReasoningBudgetTokens > 0 ? config.Strata.ReasoningBudgetTokens.ToString() : null;

    internal string? LegacyAnthropicThinking(ManagerConfig config) =>
        string.IsNullOrWhiteSpace(config.Strata.AnthropicThinking) ? null : config.Strata.AnthropicThinking;

    public async Task<BackendStatus> ProbeAsync(CancellationToken ct)
    {
        var config = ConfigService.ReadConfig();
        var engine = ResolveEngine(config, _profile);
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
        var server = ResolveServer(config, _profile);
        if (server == null)
            return BackendStatus.Unavailable("未找到 serve\\server.py；请设置服务源码根目录。");
        var python = await ResolvePythonAsync(config, _profile, ct).ConfigureAwait(false);
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
        BuildLaunch(request, TokenizerDir(request.Config, _profile.Id, ResolveModel(request.Config, _profile)));

    public LaunchPlan BuildLaunch(EngineStartRequest request, string tokenizer)
    {
        var config = request.Config;
        var model = ResolveModel(config, _profile);
        var engine = ResolveEngine(config, _profile) ?? throw new FileNotFoundException($"未找到 {_profile.EngineExeName}。");
        var server = ResolveServer(config, _profile) ?? throw new FileNotFoundException("未找到 serve\\server.py。");
        var python = ResolvePython(config, _profile) ?? throw new FileNotFoundException("未找到 Python 3.10+。");
        var port = ResolvePort(request);
        var host = Param(config, "host") ?? (string.IsNullOrWhiteSpace(config.Strata.Host) ? "127.0.0.1" : config.Strata.Host);

        var runconfig = BuildRunConfig(config, engine, model, tokenizer, port, host);
        var configPath = RunConfigPath(_profile.Id, model);

        var args = new List<string> { "-m", "serve.server", "--engine", "strata", "--config", configPath, "--port", port.ToString() };
        if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal)) { args.Add("--host"); args.Add(host); }

        var env = new Dictionary<string, string> { ["PYTHONUTF8"] = "1", ["PYTHONUNBUFFERED"] = "1" };
        var engineDir = Path.GetDirectoryName(engine);
        var managerRoot = ConfigService.GetManagerRoot();
        var llamaCppRoot = ConfigService.GetLlamaCppRoot();
        var extraDirs = new[] { engineDir, managerRoot, llamaCppRoot }
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var extraPath = string.Join(";", extraDirs);
        if (!string.IsNullOrEmpty(extraPath))
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            env["PATH"] = extraPath + ";" + currentPath;
        }

        var files = new Dictionary<string, string> { [configPath] = JsonSerializer.Serialize(runconfig, new JsonSerializerOptions { WriteIndented = true }) };
        // `python -m serve.server` resolves the `serve` package from the working directory, so
        // this must be the source root (serve/'s parent), not the serve/ folder itself.
        var cwd = Path.GetDirectoryName(Path.GetDirectoryName(server))!;
        return new LaunchPlan(python, args, cwd, env, files);
    }

    public async Task<EngineSession> StartAsync(EngineStartRequest request, ILogSink log, CancellationToken ct)
    {
        var model = ResolveModel(request.Config, _profile);
        // The server needs the tokenizer directory (vocab.json etc.); prepare it first so the
        // runconfig can point at the directory the tool actually produced.
        var server = ResolveServer(request.Config, _profile)
            ?? throw new FileNotFoundException($"未找到 {_profile.DisplayName} 的服务源码 serve\\server.py。请先点击「配置环境」初始化。");
        var engine = ResolveEngine(request.Config, _profile)
            ?? throw new FileNotFoundException($"未找到 {_profile.EngineExeName}。请先点击「配置环境」初始化。");
        var python = ResolvePython(request.Config, _profile)
            ?? throw new FileNotFoundException("未找到 Python 3.10+。请先点击「配置环境」初始化。");
        var serveRoot = Path.GetDirectoryName(Path.GetDirectoryName(server))!;
        log.Write("准备分词表…");
        var tokenizer = await StrataPackService.EnsureTokenizerDirAsync(serveRoot, model,
            TokenizerDir(request.Config, _profile.Id, model),
            Param(request.Config, _profile.Id, "pythonPath") ?? request.Config.Strata.PythonPath, log.Write, ct).ConfigureAwait(false);
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
        request.Port ?? ParamInt(request.Config, "servePort", request.Config.Strata.ServePort);

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
        var isPoly = _profile.Id == PolyProfile.Id;
        var args = new List<string> { isPoly ? "--model" : "--native", model, "--max-context", context.ToString() };
        if (!isPoly)
        {
            var workers = ParamInt(config, "poolWorkers", s.Workers);
            var mtp = ParamInt(config, "mtpDrafts", 2);
            // 16 slots are a safe speed-up at 4K on a 6GB card. At 40K the KV cache already
            // consumes that card's free VRAM; forcing 16 slots spills them through WDDM and
            // collapses decode. Larger contexts therefore use the engine's auto sizing unless
            // the user explicitly chooses a slot count.
            var slots = ParamInt(config, "expertSlots", context <= 8192 ? 16 : 0);
            // Both flags are read through ParamInt so the panel's value wins; the legacy
            // StrataConfig block stays the fallback for installs that predate these fields.
            var prefill = ParamInt(config, "prefill", s.Prefill);
            var maxNew = ParamInt(config, "maxNew", s.MaxNew);
            if (prefill > 0) { args.Add("--prefill"); args.Add(prefill.ToString()); }
            if (maxNew > 0) { args.Add("--max-new"); args.Add(maxNew.ToString()); }
            if (workers > 0) { args.Add("--pool-workers"); args.Add(workers.ToString()); }
            if (mtp > 0) { args.Add("--mtp"); args.Add(mtp.ToString()); }
            if (slots > 0) { args.Add("--expert-slots"); args.Add(slots.ToString()); }
        }
        else
        {
            var reserve = ParamInt(config, "vramReserveMib", 256);
            var tailSkip = ParamInt(config, "tailSkip", 0);
            if (reserve > 0) { args.Add("--vram-reserve-mib"); args.Add(reserve.ToString()); }
            // PolyStrata reads this flag (upstream strata.exe logs "ignoring --tail-skip").
            if (tailSkip > 0) { args.Add("--tail-skip"); args.Add(tailSkip.ToString()); }
        }
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
        // Installed by us (winget / python.org), but not on this process's PATH yet.
        return Services.PythonInstaller.ProbeInstalledDirectories();
    }

    private static Task<string?> ResolvePythonAsync(ManagerConfig config, CancellationToken ct) =>
        ResolvePythonAsync(config, UpstreamProfile, ct);

    private static async Task<string?> ResolvePythonAsync(ManagerConfig config, StrataEngineProfile profile, CancellationToken ct)
    {
        var direct = ResolvePython(config, profile);
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
