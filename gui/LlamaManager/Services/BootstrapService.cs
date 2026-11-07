using System.IO;
using LlamaManager.Engines;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>
/// One-click setup over the same verified, versioned acquisition path used by updates. Existing
/// usable engines survive a failed update check; no runtime is copied into a user's live engine
/// directory and no old/global venv is pip-upgraded in place.
/// </summary>
public static class BootstrapService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public sealed record StepResult(string Name, bool Ok, string Detail);

    public static async Task<IReadOnlyList<StepResult>> RunAsync(string backendId, ILogSink log, CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("已有一个初始化任务在运行。");
        try
        {
            return backendId switch
            {
                "llamacpp" or "polystrata" => await BootstrapBuiltInAsync(backendId, log, ct).ConfigureAwait(false),
                "kvmem" => await BootstrapKvmemAsync(log, ct).ConfigureAwait(false),
                _ => await BootstrapPluginAsync(backendId, log, ct).ConfigureAwait(false),
            };
        }
        finally { Gate.Release(); }
    }

    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<StepResult>>> RunAllAsync(ILogSink log, CancellationToken ct = default)
    {
        var results = new Dictionary<string, IReadOnlyList<StepResult>>();
        foreach (var backend in BackendRegistry.All().Where(b => b.Descriptor.BuiltIn && CanDownload(b.Descriptor.Id)))
        {
            var id = backend.Descriptor.Id;
            log.Write("\n=== 配置引擎与匹配运行库：" + id + " ===");
            try { results[id] = await RunAsync(id, log, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.Write(id + " 配置失败：" + ex.Message); results[id] = [new StepResult(id, false, ex.Message)]; }
        }
        return results;
    }

    public static bool CanDownload(string backendId) => EngineAcquisitionService.CanAcquire(backendId);

    private static async Task<IReadOnlyList<StepResult>> BootstrapBuiltInAsync(string backendId, ILogSink log, CancellationToken ct)
    {
        var steps = new List<StepResult>();
        BuiltInInstallation.ProbeResult existing;
        try { existing = await EngineAcquisitionService.ProbeInstalledAsync(backendId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            steps.Add(new StepResult("identity", false, "现有引擎探测失败；没有盲目覆盖：" + ex.Message));
            return steps;
        }
        if (existing.Variant)
        {
            steps.Add(new StepResult("manual-variant", false, existing.Detail));
            return steps;
        }
        if (existing.Exists && !BuiltInInstallation.IsComparableVersion(backendId, existing.Version))
        {
            // A functional manual engine can still be used, but setup cannot pretend to know its
            // release or download over it. Unusable/unidentified executables require an explicit repair.
            steps.Add(new StepResult("manual-version", existing.Available, existing.Detail + " 版本不可可靠比较，初始化保留现有目录；没有自动覆盖。"));
            return steps;
        }
        var current = existing;
        try
        {
            var update = await EngineAcquisitionService.UpdateAsync(backendId, log, ct).ConfigureAwait(false);
            steps.Add(new StepResult("release-update", true, update.Detail ?? $"本机 {update.InstalledVersion}，远端 {update.LatestVersion}。"));
            if (update.Updated) current = await EngineAcquisitionService.ProbeInstalledAsync(backendId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var usable = existing.Exists && existing.Available;
            log.Write(backendId + " 更新/修复未完成，保留原安装：" + ex.Message);
            steps.Add(new StepResult("release-update", usable, usable
                ? "更新检查/下载未完成；已验证的原安装保留：" + ex.Message
                : "安装/修复失败；旧路径不变：" + ex.Message));
            if (!usable) return steps;
        }
        steps.Add(new StepResult("native-runtime", current.Available, current.Detail));
        if (current.PackageRoot != null) steps.Add(new StepResult("package-root", current.Available, current.PackageRoot));
        log.Write("环境检查未加载模型、未停止服务；新安装的引擎/DLL/私有 Python 已在最终版本目录验证。");
        return steps;
    }

    /// <summary>KVMem remains self-contained; never create a venv, run pip or fetch CUDA separately.</summary>
    private static async Task<IReadOnlyList<StepResult>> BootstrapKvmemAsync(ILogSink log, CancellationToken ct)
    {
        var steps = new List<StepResult>();
        var capability = await EngineAcquisitionService.DetectComputeCapabilityAsync(ct).ConfigureAwait(false);
        var architecture = EngineCatalog.KvmemArchitecture(capability);
        if (architecture == null)
        {
            var detail = "无法确定受支持的精确 GPU 架构；KVMem 不猜测 RTX50，不将 sm80/sm90 映射到 RTX 包。";
            log.Write(detail);
            steps.Add(new StepResult("kvmem-architecture", false, detail));
            return steps;
        }
        steps.Add(new StepResult("kvmem-architecture", true, $"sm{architecture} / nvidia-{EngineCatalog.KvmemGpuFamily(architecture.Value)}"));
        var root = KvmemInstallation.ResolvePackageRoot(ConfigService.ReadConfig());
        var layout = root == null ? null : KvmemInstallation.Validate(root, architecture);
        KvmemInstallation.ValidationResult? runtime = layout?.Ok == true
            ? await KvmemInstallation.ValidateRuntimeAsync(root!, ct).ConfigureAwait(false) : null;
        if (root == null || layout?.Ok != true || runtime?.Ok != true)
        {
            log.Write("KVMem：需要安装/修复完整 unified 包；原 PolyStrata、活动后端和模型不变。");
            if (layout?.Ok == false) log.Write(layout.Detail);
            if (runtime?.Ok == false) log.Write(runtime.Detail);
            try
            {
                root = await EngineAcquisitionService.AcquireAsync("kvmem", log, ct).ConfigureAwait(false);
                runtime = new KvmemInstallation.ValidationResult(true, "完整安装提交前已验证最终目录的包内 Python imports/native help/capabilities。",
                    KvmemInstallation.ReadVersion(root), architecture);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.Write("KVMem 安装/修复失败：" + ex.Message); steps.Add(new StepResult("kvmem-unified", false, ex.Message)); return steps; }
        }
        else
        {
            try
            {
                var update = await EngineAcquisitionService.UpdateAsync("kvmem", log, ct).ConfigureAwait(false);
                if (update.Updated)
                {
                    root = update.PackageRoot ?? KvmemInstallation.ResolvePackageRoot(ConfigService.ReadConfig());
                    runtime = new KvmemInstallation.ValidationResult(true, "新版本提交前已验证最终目录的包内 Python imports/native help/capabilities。", update.LatestVersion, architecture);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.Write("KVMem 检查更新未完成，保留已验证安装：" + ex.Message); steps.Add(new StepResult("kvmem-update-check", true, "更新检查未完成；未更改已安装路径。")); }
        }
        if (root == null) { steps.Add(new StepResult("kvmem-unified", false, "未找到完整的 KVMem 版本/显卡目录。")); return steps; }
        layout = KvmemInstallation.Validate(root, architecture);
        steps.Add(new StepResult("kvmem-unified", layout.Ok, layout.Detail + " " + root));
        if (!layout.Ok) return steps;
        steps.Add(new StepResult("bundle-python", true, Path.Combine(root, KvmemInstallation.PythonRelativePath)));
        runtime ??= await KvmemInstallation.ValidateRuntimeAsync(root, ct).ConfigureAwait(false);
        steps.Add(new StepResult("kvmem-runtime", runtime.Ok, runtime.Detail));
        log.Write(runtime.Detail);
        log.Write("KVMem 使用包内 Python/CUDA：无 pip、无 venv、无 CUDA 重下载；此检查不加载模型。");
        return steps;
    }

    private static async Task<IReadOnlyList<StepResult>> BootstrapPluginAsync(string backendId, ILogSink log, CancellationToken ct)
    {
        var backend = BackendRegistry.ById(backendId);
        if (backend == null) return [new StepResult(backendId, false, "未注册的引擎。")];
        var status = await backend.ProbeAsync(ct).ConfigureAwait(false);
        log.Write(status.Available ? backend.Descriptor.DisplayName + "：已找到引擎。" : backend.Descriptor.DisplayName + "：" + status.Problem);
        return [new StepResult(backendId, status.Available, status.Available ? status.ExecutablePath! : status.Problem!)];
    }

    /// <summary>Legacy public requirement reader; newly acquired packages require their own pinned file, not this fallback.</summary>
    public static IReadOnlyList<string> ReadRequirements(string serveRoot)
    {
        var path = Path.Combine(serveRoot, "requirements.txt");
        if (File.Exists(path))
        {
            try
            {
                var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
                if (lines.Length > 0) return lines;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return ["numpy", "jinja2", "regex", "pyyaml", "tqdm", "requests", "pillow", "psutil"];
    }
}
