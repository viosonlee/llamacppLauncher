using System.Diagnostics;
using System.Globalization;
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
    string Arch);

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
    public static readonly StrataEngineProfile PolyProfile = new(
        "polystrata",
        "PolyStrata (strata-poly)",
        "基于 Strata 内核的 MoE 引擎（lookup/重复猜测 + 内置 MTP 草稿）；通过自带 HTTP 服务接入 agent。",
        "engine-poly", "strata-poly.exe", "qwen35moe");

    private readonly StrataEngineProfile _profile;

    public StrataBackend(StrataEngineProfile profile)
    {
        _profile = profile;
        Descriptor = new BackendDescriptor(profile.Id, profile.DisplayName, profile.Description,
            BuiltIn: true, GpuHeavy: true);
        var list = new List<ParameterSpec>
        {
            new("packageRoot", "引擎包目录", "路径", ParamType.FolderPath, "",
                "包含 engine\\" + profile.EngineExeName + " 与 serve\\server.py 的根目录。留空自动匹配默认目录。",
                Help: "【引擎包目录】\n• 作用：Strata 引擎二进制与配套服务脚本所在的根目录。\n• 建议：留空，系统会自动匹配 engines/polystrata。\n• 包含：engine\\" + profile.EngineExeName + " 与 serve\\server.py。"),
            new("serveRoot", "服务源码根", "路径", ParamType.FolderPath, "",
                "包含 serve\\server.py 的 Python 服务目录；留空默认使用引擎包目录。",
                Help: "【服务源码根】\n• 作用：提供 OpenAI / Anthropic 兼容 HTTP API 的 Python 服务源码根目录。\n• 建议：留空即可，默认使用“引擎包目录”。"),
            new("pythonPath", "Python 解释器", "路径", ParamType.FilePath, "",
                "Python 3.10+ 的 python.exe；留空自动使用一键配置好的专属虚拟环境。",
                Help: "【Python 解释器】\n• 作用：启动 API 服务端所需的 Python 3.10+ 可执行文件。\n• 建议：留空即可，系统会使用「一键配置环境」准备好的专属虚拟环境。"),
            new("servePort", "服务端口", "基本", ParamType.Integer, "8080",
                "Strata HTTP API 监听端口（默认 8080，与 llama.cpp 共用 agent 接入地址）。", 1, 65535,
                Help: "【服务端口】\n• 作用：Strata HTTP API 监听端口，外部客户端通过它连接模型。\n• 默认：8080，与 llama.cpp 统一，切换引擎不需要修改 agent 配置。\n• 同一端口一次只能有一个服务监听；请先停止旧引擎再启动新引擎。\n• 启动失败时只报告原因，不会自动更换端口。",
                RangeHint: "1–65535，默认 8080"),
            new("host", "监听地址", "基本", ParamType.Text, "127.0.0.1",
                "监听 IP 地址。默认 127.0.0.1 仅本机安全访问；设为 0.0.0.0 允许局域网设备访问。",
                Help: "【监听地址】\n• 作用：HTTP API 监听哪个网络地址。\n• 127.0.0.1（默认）：仅本机访问，最安全。\n• 0.0.0.0：允许局域网内其他设备调用，注意防火墙与鉴权。"),
            new("context", "上下文", "基本", ParamType.Integer, "4096",
                "单次最大上下文 Token 数（--max-context）。显存紧张设 2048~4096，显存充裕可设 8192 或更大。", 256, 1048576,
                Help: "【上下文】\n• 作用：引擎单次能处理的上下文 Token 上限（--max-context）。\n• 显存：上下文越大，KV 缓存占用越多。\n• 参考：6GB 显存建议 2048–4096；8–12GB 建议 4096–8192；16GB+ 可到 16384。",
                Unit: "tokens", RangeHint: "256–1,048,576，推荐 4096 / 8192"),
        };
        list.Add(new("vramReserveMib", "显存预留", "基本", ParamType.Integer, "256",
            "为桌面与显卡驱动预留的安全显存量（--vram-reserve-mib，默认 256）。", 0, 60000,
            Help: "【显存预留】\n• 作用：引擎分配显存时强行预留指定 MiB（--vram-reserve-mib），剩余显存用于缓存 MoE 专家。\n• 为什么需要：防止显存被 100% 占满导致桌面黑屏或驱动重置。\n• 参考：单显卡或笔记本独显推荐 256–500 MiB；双显卡可设 256 MiB 腾出更多显存。",
            Unit: "MiB", RangeHint: "0–60,000，推荐 256"));
        list.Add(new("tailSkip", "尾部跳过", "基本", ParamType.Integer, "0",
            "显存外、且每个 token 都排在第 N+1 位之后的专家被跳过（--tail-skip）。0 表示不跳过。", 0, 64,
            Help: "【尾部跳过】\n• 作用：显存外、且在一个窗口内每个 token 都排在第 N+1 位或更后的专家被直接跳过（--tail-skip）。\n• 为什么能快：被跳过的是本来也进不了显存、命中率最低的那批专家，省掉了它们的搬运和计算。\n• 代价：答案会略有变化（官方原文：the answers move a little）。\n• 默认 0（不跳过）；项目自带的安装脚本给 Qwen3.6 家族设 6。\n• 建议：6 是质量与速度的平衡点；1 更快，但偏离更大。",
            RangeHint: "0–64，推荐 6（质量优先）或 1（速度优先）"));
        list.Add(new("kv", "KV 缓存格式", "基本", ParamType.Choice, "",
            "注意力缓存的存储格式（--kv）。q8_0 把 KV 显存减半；int8/int8k 适用于 Gemma 4。",
            Help: "【KV 缓存格式】\n• 作用：K/V 注意力缓存的格式（--kv）。\n• 默认（fp16）：精度最高，引擎不传该参数。\n• q8_0：显存约为一半，答案略有差异；省下的显存会给专家缓存，长上下文时解码更快（限 qwen35moe 家族）。\n• int8 / int8k：Gemma 4 系列专用，int8k 仅压缩 Key 缓存。",
            Choices:
            [
                new ParamChoice("", "默认（fp16）", "引擎默认，精度最高。", Recommended: true),
                new ParamChoice("fp16", "fp16", "显式指定全精度。"),
                new ParamChoice("q8_0", "q8_0（KV 减半）", "qwen35moe 家族支持；长上下文更省显存。"),
                new ParamChoice("int8", "int8（Gemma 4）", "Gemma 4 系列专用，KV 均使用 int8。"),
                new ParamChoice("int8k", "int8k（Gemma 4 仅键）", "Gemma 4 系列专用，仅 Key 缓存使用 int8。"),
            ]));
        list.Add(new("cacheBias", "显存专家路由偏置", "基本", ParamType.Decimal, "0",
            "给已在显存内的专家加路由偏置（--cache-bias），减少跨内存取专家。仅 qwen35moe 家族有效，0 为关闭。", 0, 2,
            Help: "【显存专家路由偏置】\n• 作用：设置 --cache-bias F，token 逐个生成时，路由器给已在显存内的专家加分，偏向选用无需跨 PCIe 搬运的专家（权重仍是路由器原始值）。\n• 效果：官方实测 12 GB 显存下解码 196 → 234 tok/s；128 步贪心采样里 121–126 步 top token 不变，答案略有变化。\n• 适用：仅 qwen35moe 家族（Qwen3.6、Tiel Coder、Ornith 等）。\n• 建议：0.3–0.5 起试；显存外专家越多（小显存显卡）收益越大。\n• 默认 0：关闭。",
            RangeHint: "0–2，推荐 0.5", Unit: "F"));
        list.Add(new("conversationCache", "对话驻留缓存", "基本", ParamType.Choice, "",
            "把暂停的对话驻留在内存（--conversation-cache-mib），多会话切换无需重新读取提示词。",
            Help: "【对话驻留缓存】\n• 作用：设置 --conversation-cache-mib，把暂停中的对话状态驻留在物理内存，切回时无需重新预填充。\n• 适用场景：多个 agent 工具（pi / Claude Code 等）共用同一服务端口、来回切换对话时直接有感。\n• auto：按模型加载后的空闲内存自动分配（40%，上限 8192 MiB，空闲不足 1024 MiB 时关闭）。\n• 固定值：显式指定驻留缓存的 MiB 数。\n• 默认（关闭）：引擎行为不变。",
            Choices:
            [
                new ParamChoice("", "关闭", "不驻留，切回对话时重新读取提示词（引擎默认）。", Recommended: true),
                new ParamChoice("auto", "auto（按空闲内存）", "模型加载后空闲内存的 40%，最多 8192 MiB。"),
                new ParamChoice("2048", "2048 MiB", "固定 2 GiB。"),
                new ParamChoice("4096", "4096 MiB", "固定 4 GiB。"),
                new ParamChoice("8192", "8192 MiB", "固定 8 GiB。"),
            ]));
        list.Add(new("tensorCoreMma", "Tensor-Core 线性注意力", "高级", ParamType.Boolean, "1",
            "设置 STRATA_GDN_MMA=1，利用 Tensor Core 加速 Qwen3.6 / Ornith / Tiel 等线性注意力层，Prompt 提速 12%~18%（RTX 30+ 显卡）。",
            Help: "【Tensor-Core 线性注意力加速】\n• 作用：设置环境变量 STRATA_GDN_MMA=1。\n• 效果：利用 llama.cpp 的 Tensor-Core 内核读取 Qwen3.6、Ornith 1.5、Tiel Coder 和 MiniCPM-V 的线性注意力层，Prompt 吞吐提升约 12%~18%。\n• 要求：RTX 30 系列及更新显卡（本机 RTX 3060 支持）。",
            Advanced: true));
        list.Add(new("polyFlashAttention", "Flash Attention 加速", "高级", ParamType.Boolean, "1",
            "设置 STRATA_FATTN=1 与 STRATA_FDEC=1，启用长上下文 Flash Attention 与 Tensor Core 写入核，大幅加速长文本。",
            Help: "【Flash Attention 与解码加速】\n• 作用：设置环境变量 STRATA_FATTN=1 与 STRATA_FDEC=1。\n• 效果：在 Qwen3.6 及其衍生模型（Ornith、Tiel 等）上，长文本 Prompt 通过 Flash Attention 内核计算，生成阶段通过单次显存读取的 Tensor Core 核解码，极大降低显存带宽瓶颈。\n• 要求：RTX 30 系列及更新显卡。",
            Advanced: true));
        list.Add(new("threads", "CPU 线程数", "高级", ParamType.Integer, "0",
            "显存外的专家在 CPU 上计算时使用的线程数（--threads）。0 表示由引擎自动分配。", 0, 256,
            Help: "【CPU 线程数】\n• 作用：显存外的专家在 CPU 上计算时使用的线程数（--threads）。\n• 默认 0：由引擎按可用核心自动分配（本机为 16 线程）。\n• 建议：默认先跑一次，再试 8 / 12 对比；线程过多会与显卡搬运争抢内存带宽，反而变慢。",
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
        list.Add(new("mmproj", "视觉投影 (mmproj)", "视觉 / 多模态", ParamType.FilePath, "",
            "多模态视觉投影模型文件（如 mmproj-Q8_0.gguf）。设置后由 strata-vision 伴生服务提供图像理解。",
            Help: "【视觉投影 (mmproj)】\n• 作用：指定多模态视觉投影模型文件（如 mmproj-Q8_0.gguf）。\n• 原理：Strata 引擎会启动同目录下的 strata-vision.exe 视觉伴生服务，将图片编码为特征嵌入，使大模型具备多模态识图问答能力。\n• 留空：不启动视觉编码器。"));
        list.Add(new("draftModel", "草稿 / MTP 模型", "高级", ParamType.FilePath, "",
            "外部草稿或 MTP 模型文件（--draft-model，如 mtp-*.gguf）。当主模型未内置草稿头时使用。",
            Help: "【草稿 / MTP 模型】\n• 作用：为 strata-poly 指定外部草稿权重文件（--draft-model）。\n• 适用：模型本身未内置 MTP 块、但附带独立的 mtp-*.gguf 文件时。\n• 留空：使用内置草稿块或不使用外部草稿。",
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
        var poly = PolyStrataCompatibility.Analyze(md);
        return new CompatibilityReport(poly.Runnable, poly.Blockers, poly.Notes, poly.Summary,
            Estimate(md, modelPath));
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
        var mma = Param(config, "tensorCoreMma") ?? "1";
        if (mma is "1" or "true" or "True") env["STRATA_GDN_MMA"] = "1";
        var fattn = Param(config, "polyFlashAttention") ?? "1";
        if (fattn is "1" or "true" or "True")
        {
            env["STRATA_FATTN"] = "1";
            env["STRATA_FDEC"] = "1";
        }
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
        var portProblem = ServerService.PortUnavailableReason(port,
            Param(request.Config, "host") ?? request.Config.Strata.Host);
        if (portProblem != null) throw new InvalidOperationException(portProblem);
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
        var args = new List<string> { "--model", model, "--max-context", context.ToString() };
        var reserve = ParamInt(config, "vramReserveMib", 256);
        var tailSkip = ParamInt(config, "tailSkip", 0);
        var threads = ParamInt(config, "threads", s.Workers);
        if (reserve > 0) { args.Add("--vram-reserve-mib"); args.Add(reserve.ToString()); }
        if (tailSkip > 0) { args.Add("--tail-skip"); args.Add(tailSkip.ToString()); }
        if (threads > 0) { args.Add("--threads"); args.Add(threads.ToString()); }
        // KV format: only qwen35moe accepts q8_0; the engine refuses it for other families,
        // so the panel documents the limit and an empty value keeps the engine default.
        var kv = Param(config, "kv");
        if (kv is "fp16" or "q8_0" or "int8" or "int8k") { args.Add("--kv"); args.Add(kv); }
        // Router bias and the conversation park are opt-in (strata-poly defaults both off);
        // emit them only when the user chose a value, so older configs stay byte-identical.
        var cacheBias = Param(config, "cacheBias");
        if (double.TryParse(cacheBias, NumberStyles.Float, CultureInfo.InvariantCulture, out var bias) && bias > 0)
        {
            args.Add("--cache-bias");
            args.Add(bias.ToString("0.0######", CultureInfo.InvariantCulture));
        }
        var conversationCache = Param(config, "conversationCache");
        if (conversationCache is "auto" || (int.TryParse(conversationCache, out var parkMib) && parkMib > 0))
        {
            args.Add("--conversation-cache-mib");
            args.Add(conversationCache);
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
        var mmproj = Param(config, "mmproj");
        if (string.IsNullOrWhiteSpace(mmproj))
        {
            mmproj = ModelPairingService.FindPairedMmproj(model);
        }
        if (!string.IsNullOrWhiteSpace(mmproj) && File.Exists(mmproj))
        {
            var engineDir = Path.GetDirectoryName(engine)!;
            var visionExe = Path.Combine(engineDir, "strata-vision.exe");
            if (File.Exists(visionExe))
            {
                cfg["vision"] = new Dictionary<string, object>
                {
                    ["exe"] = visionExe,
                    ["mmproj"] = Path.GetFullPath(mmproj),
                    ["model"] = Path.GetFullPath(model),
                    ["gpu"] = true,
                };
            }
        }
        var draftModel = Param(config, "draftModel");
        if (string.IsNullOrWhiteSpace(draftModel))
        {
            draftModel = ModelPairingService.FindPairedDraftModel(model);
        }
        if (!string.IsNullOrWhiteSpace(draftModel) && File.Exists(draftModel))
        {
            args.Add("--draft-model");
            args.Add(Path.GetFullPath(draftModel));
        }
        return cfg;
    }

    private static string RunConfigPath(string backendId, string model) =>
        Path.Combine(DataDir(backendId, model), "strata-runconfig.json");

    /// <summary>The tokenizer directory the server reads (vocab.json etc.); prepared from the GGUF.</summary>
    public static string TokenizerDir(ManagerConfig config, string model) => TokenizerDir(config, "polystrata", model);

    public static string TokenizerDir(ManagerConfig config, string backendId, string model) =>
        Path.Combine(DataDir(backendId, model), "tokenizer");

    private static string DataDir(string backendId, string model)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(PathIdentity.Normalize(model))))[..16];
        return Path.Combine(ConfigService.GetManagerRoot(), "data", backendId, hash);
    }

    /// <summary>Resolves the engine exe for the PolyStrata profile (legacy static entry point).</summary>
    public static string? ResolveEngine(ManagerConfig config) => ResolveEngine(config, PolyProfile);

    public static string? ResolveEngine(ManagerConfig config, StrataEngineProfile profile)
    {
        var root = Param(config, profile.Id, "packageRoot") ?? config.Strata.PackageRoot;
        if (string.IsNullOrWhiteSpace(root)) return null;
        var engine = Path.Combine(Path.GetFullPath(root), profile.EngineSubdir, profile.EngineExeName);
        return File.Exists(engine) ? engine : null;
    }

    /// <summary>Resolves serve/server.py for the PolyStrata profile (legacy static entry point).</summary>
    public static string? ResolveServer(ManagerConfig config) => ResolveServer(config, PolyProfile);

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

    internal static string? ResolvePython(ManagerConfig config) => ResolvePython(config, PolyProfile);

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
        ResolvePythonAsync(config, PolyProfile, ct);

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

    internal static string ResolveModel(ManagerConfig config) => ResolveModel(config, PolyProfile);

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
