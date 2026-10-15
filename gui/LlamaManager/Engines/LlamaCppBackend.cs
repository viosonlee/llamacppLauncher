using System.IO;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager.Engines;

/// <summary>
/// Built-in adapter over the existing llama.cpp lifecycle (<see cref="ServerService"/>).
/// It deliberately delegates rather than reimplements, so behaviour is bit-for-bit
/// identical to the pre-plugin launcher. Prism variants are resolved by ServerService.
/// </summary>
public sealed class LlamaCppBackend : IServerBackend
{
    public BackendDescriptor Descriptor { get; } = new(
        "llamacpp",
        "llama.cpp (llama-server)",
        "内置引擎：任意 GGUF 的通用后端，OpenAI 兼容 HTTP 服务。",
        BuiltIn: true,
        GpuHeavy: true);

    // The hand-written parameter panel in MainWindow.Parameters.cs drives llama.cpp.
    public IReadOnlyList<ParameterSpec> Parameters => [];

    public async Task<BackendStatus> ProbeAsync(CancellationToken ct)
    {
        var engine = ConfigService.GetLlamaServerPath();
        if (!File.Exists(engine))
            return BackendStatus.Unavailable("未找到 llama-server.exe；请在设置中指定引擎目录。");
        return await Task.Run(() =>
        {
            System.Diagnostics.Process? process = null;
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo(engine)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                start.ArgumentList.Add("--version");
                process = System.Diagnostics.Process.Start(start);
                if (process == null) return new BackendStatus(true, engine, null, null);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(5000)) return new BackendStatus(true, engine, null, null);
                var text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                var match = System.Text.RegularExpressions.Regex.Match(text, @"version:\s*(\S+)");
                var version = match.Success ? match.Groups[1].Value
                    : text.Split('\n').FirstOrDefault(l => l.Contains("version"))?.Trim();
                return new BackendStatus(true, engine, version, null);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return BackendStatus.Unavailable("探测 llama-server 失败：" + ex.Message);
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
        if (string.IsNullOrWhiteSpace(modelPath))
            return CompatibilityReport.Unknown("未选择模型。");
        var md = await Task.Run(() => GgufService.TryReadShards(modelPath), ct).ConfigureAwait(false);
        if (md == null)
            return new CompatibilityReport(false, ["无法读取 GGUF 元数据。"], [], "读取失败。", null);
        var notes = new List<string>();
        if (ConfigService.IsPrismModel(modelPath))
            notes.Add("检测到 Prism 专用张量；将使用 Prism 变体引擎。");
        if (md.HasTq1)
            notes.Add("含 TQ1_0 张量，GPU 支持取决于引擎版本。");
        var summary = $"llama.cpp：{md.Architecture} / {md.LayerCount} 层 / {md.TensorCount} 张量；通用后端，默认按引擎能力加载。";
        ResourceEstimate? estimate = null;
        try { estimate = ResourceEstimator.ForGguf(modelPath, md.LayerCount); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new CompatibilityReport(true, [], notes, summary, estimate);
    }

    public Task<EngineStatus> StatusAsync(EngineSession? session, CancellationToken ct)
    {
        if (session == null) return Task.FromResult(new EngineStatus(false, false, null, "未运行"));
        return Task.FromResult(new EngineStatus(true, false, session.Port, null));
    }

    public LaunchPlan BuildLaunch(EngineStartRequest request)
    {
        var config = request.Config;
        var model = request.Embedding ? config.EmbeddingModelPath : config.ModelPath;
        var engine = ConfigService.GetLlamaServerPath(model);
        var args = new LauncherArguments(config.Args).Values.ToArray();
        return new LaunchPlan(engine, args, Path.GetDirectoryName(engine) ?? "",
            new Dictionary<string, string>(), new Dictionary<string, string>());
    }

    public async Task<EngineSession> StartAsync(EngineStartRequest request, ILogSink log, CancellationToken ct)
    {
        if (request.Embedding)
            await ServerService.StartEmbeddingAsync(request.Config, log.Write, ct).ConfigureAwait(false);
        else
            await ServerService.StartAsync(request.Config, request.OpenBrowser, log.Write, ct).ConfigureAwait(false);
        var session = (request.Embedding ? request.Config.RunningEmbedding : request.Config.RunningServer)
            ?? throw new InvalidOperationException("启动后未记录会话。");
        return ToEngineSession(session);
    }

    public async Task StopAsync(EngineSession session, ILogSink log)
    {
        var config = ConfigService.ReadConfig();
        if (session.Extra.TryGetValue("embedding", out var e) && e == "1")
            await ServerService.StopEmbeddingAsync(config, log.Write).ConfigureAwait(false);
        else
            await ServerService.StopAsync(config, log.Write).ConfigureAwait(false);
    }

    public Task<bool> ReadyAsync(EngineSession session, CancellationToken ct) =>
        ServerService.TestApiReadyAsync(session.Port, ct);

    public int ResolvePort(EngineStartRequest request) =>
        request.Embedding ? request.Config.EmbeddingPort
            : new LauncherArguments(request.Config.Args).GetPort(request.Config.Port);

    public string HealthUrl(EngineSession session) => $"http://127.0.0.1:{session.Port}/health";

    public void OpenWebUi(EngineSession session) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://127.0.0.1:{session.Port}") { UseShellExecute = true });

    public static EngineSession ToEngineSession(ServerSession session) => new()
    {
        BackendId = "llamacpp",
        ProcessId = session.ProcessId,
        StartTimeUtcTicks = session.StartTimeUtcTicks,
        ExecutablePath = session.EnginePath,
        ModelPath = session.ModelPath,
        Arguments = session.Args,
        Port = session.Port,
        Extra = { ["embedding"] = session.Embedding ? "1" : "0" },
    };
}
