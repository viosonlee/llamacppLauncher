using System.Diagnostics;
using System.IO;
using LlamaManager.Engines;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>
/// One-click engine setup. Small, licence-compatible resources ship with the launcher; anything
/// large or version-bound (engine binaries, Python packages) is fetched here. Every step is
/// idempotent: an engine that already probes as available is left alone, and a failure only marks
/// that backend unavailable.
/// </summary>
public static class BootstrapService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public sealed record StepResult(string Name, bool Ok, string Detail);

    /// <summary>Runs the bootstrap for one backend id and returns the steps it performed.</summary>
    public static async Task<IReadOnlyList<StepResult>> RunAsync(string backendId, ILogSink log, CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("已有一个初始化任务在运行。");
        try
        {
            return backendId switch
            {
                "llamacpp" => await BootstrapLlamaCppAsync(log, ct).ConfigureAwait(false),
                "strata-upstream" or "polystrata" => await BootstrapStrataAsync(backendId, log, ct).ConfigureAwait(false),
                _ => await BootstrapPluginAsync(backendId, log, ct).ConfigureAwait(false),
            };
        }
        finally { Gate.Release(); }
    }

    /// <summary>Whether the bootstrap can fetch this backend's engine automatically.</summary>
    public static bool CanDownload(string backendId) => EngineAcquisitionService.CanAcquire(backendId);

    private static async Task<IReadOnlyList<StepResult>> BootstrapLlamaCppAsync(ILogSink log, CancellationToken ct)
    {
        var steps = new List<StepResult>();
        var engine = ConfigService.GetLlamaServerPath();
        if (File.Exists(engine))
        {
            log.Write("llama.cpp：已找到 llama-server.exe。");
            steps.Add(new StepResult("llama-server", true, engine));
            return steps;
        }
        log.Write("llama.cpp：未找到 llama-server.exe，尝试从官方发布下载…");
        try
        {
            var dir = await EngineAcquisitionService.AcquireAsync("llamacpp", log, ct).ConfigureAwait(false);
            steps.Add(new StepResult("llama-server", true, dir));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Write("llama.cpp 下载失败：" + ex.Message);
            log.Write("请手动把 llama-server.exe 放到安装目录，或在设置中指定。");
            steps.Add(new StepResult("llama-server", false, "下载失败；请手动放置或指定引擎目录。"));
        }
        return steps;
    }

    /// <summary>
    /// Strata family (strata-upstream / polystrata): find the engine + source tree, download them
    /// when missing, then create a venv and install the pinned packages the HTTP service needs.
    /// The dependency list is read from the engine's own requirements.txt.
    /// </summary>
    private static async Task<IReadOnlyList<StepResult>> BootstrapStrataAsync(string backendId, ILogSink log, CancellationToken ct)
    {
        var steps = new List<StepResult>();
        var profile = backendId == "polystrata" ? StrataBackend.PolyProfile : StrataBackend.UpstreamProfile;
        var config = ConfigService.ReadConfig();

        var engine = StrataBackend.ResolveEngine(config, profile);
        var server = StrataBackend.ResolveServer(config, profile);
        if (engine == null || server == null)
        {
            log.Write($"{profile.DisplayName}：引擎或服务源码缺失，尝试从官方发布下载…");
            try
            {
                await EngineAcquisitionService.AcquireAsync(backendId, log, ct).ConfigureAwait(false);
                config = ConfigService.ReadConfig();
                engine = StrataBackend.ResolveEngine(config, profile);
                server = StrataBackend.ResolveServer(config, profile);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.Write("下载失败：" + ex.Message); }
        }
        steps.Add(engine != null
            ? new StepResult(profile.EngineExeName, true, engine)
            : new StepResult(profile.EngineExeName, false, "未找到；请选择引擎包目录或手动放置。"));
        if (engine != null) log.Write(profile.EngineExeName + "：" + engine);

        steps.Add(server != null
            ? new StepResult("serve/server.py", true, server)
            : new StepResult("serve/server.py", false, "未找到；请设置服务源码根目录。"));
        if (server != null) log.Write("serve/server.py：" + server);
        if (server == null || engine == null) return steps;

        // Python 3.10+ is required.
        var python = StrataBackend.ResolvePython(config, profile);
        if (python == null)
        {
            log.Write("未找到 Python 3.10+。请安装 Windows x64 Python（勾选 PATH），或在面板中选择 python.exe。");
            steps.Add(new StepResult("python", false, "未找到 Python 3.10+。"));
            return steps;
        }
        steps.Add(new StepResult("python", true, python));
        log.Write("Python：" + python);

        var serveRoot = Path.GetDirectoryName(Path.GetDirectoryName(server))!;
        var venv = Path.Combine(ConfigService.GetManagerRoot(), "venvs", backendId);
        var venvPython = Path.Combine(venv, "Scripts", "python.exe");
        if (!File.Exists(venvPython))
        {
            log.Write("创建虚拟环境：" + venv);
            var create = await RunAsync(python, ["-m", "venv", venv], log, ct).ConfigureAwait(false);
            steps.Add(new StepResult("venv", create == 0, venv));
            if (create != 0) return steps;
        }
        else steps.Add(new StepResult("venv", true, venv));

        var requirements = ReadRequirements(serveRoot);
        log.Write($"安装依赖（{requirements.Count} 项）：{string.Join(", ", requirements)}");
        var pipArgs = new List<string> { "-m", "pip", "install", "--quiet", "--disable-pip-version-check" };
        pipArgs.AddRange(requirements);
        var pip = await RunAsync(venvPython, pipArgs, log, ct).ConfigureAwait(false);
        steps.Add(new StepResult("pip", pip == 0, pip == 0 ? "依赖安装完成。" : "依赖安装失败，请查看日志。"));
        return steps;
    }

    private static async Task<IReadOnlyList<StepResult>> BootstrapPluginAsync(string backendId, ILogSink log, CancellationToken ct)
    {
        var steps = new List<StepResult>();
        var backend = BackendRegistry.ById(backendId);
        if (backend == null)
        {
            steps.Add(new StepResult(backendId, false, "未注册的引擎。"));
            return steps;
        }
        var status = await backend.ProbeAsync(ct).ConfigureAwait(false);
        log.Write(status.Available
            ? $"{backend.Descriptor.DisplayName}：已找到引擎可执行文件。"
            : $"{backend.Descriptor.DisplayName}：{status.Problem}");
        steps.Add(new StepResult(backend.Descriptor.Id, status.Available, status.Available ? status.ExecutablePath! : status.Problem!));
        return steps;
    }

    /// <summary>Reads the pinned requirement lines from the engine source; falls back to a built-in list.</summary>
    public static IReadOnlyList<string> ReadRequirements(string serveRoot)
    {
        var path = Path.Combine(serveRoot, "requirements.txt");
        if (File.Exists(path))
        {
            try
            {
                var lines = File.ReadAllLines(path)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .ToArray();
                if (lines.Length > 0) return lines;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // Fallback (only when the source tree is absent): the packages the HTTP service needs.
        return ["numpy", "jinja2", "regex", "pyyaml", "tqdm", "requests", "pillow", "psutil"];
    }

    private static async Task<int> RunAsync(string exe, IReadOnlyList<string> arguments, ILogSink log, CancellationToken ct)
    {
        try
        {
            var start = StrataService.CaptureStart(exe);
            foreach (var arg in arguments) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process == null) return -1;
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
                log.Write($"命令失败（退出码 {process.ExitCode}）：{exe} {string.Join(" ", arguments)}");
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Write("执行失败：" + ex.Message);
            return -1;
        }
    }
}
