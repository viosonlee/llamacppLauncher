using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager.Engines;

/// <summary>
/// Fetches a backend's engine from its GitHub release, verifies it and unpacks it into the
/// manager's own <c>engines/&lt;id&gt;</c> folder, then records the resulting paths in the config.
/// This is what turns "初始化环境" from a bare existence check into a one-click install.
/// </summary>
public static class EngineAcquisitionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>Release-fetch seam so the mapping and layout can be tested without a network.</summary>
    public static Func<string, CancellationToken, Task<JsonArray?>> ReleasesFetcher { get; set; } = FetchReleasesAsync;

    /// <summary>Archive and checksum seams allow complete install/cancellation tests without GPU/network downloads.</summary>
    public static Func<ReleaseAsset, string, ILogSink, CancellationToken, Task<string>> ArchiveFetcher { get; set; } = FetchAsync;
    public static Func<ReleaseAsset, CancellationToken, Task<string>> ChecksumFetcher { get; set; } = FetchChecksumAsync;

    /// <summary>Runs the bounded GPU probe off the UI dispatcher; the existing delegate remains injectable.</summary>
    public static Task<double> DetectComputeCapabilityAsync(CancellationToken ct = default) => Task.Run(DetectComputeCapability, ct);

    private static async Task<JsonArray?> FetchReleasesAsync(string repo, CancellationToken ct)
    {
        // llama.cpp publishes its Windows binaries as prereleases (nightly builds), so the plain
        // /releases/latest is not enough: scan the list and pick the first release that matches.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{repo}/releases?per_page=30");
        request.Headers.UserAgent.ParseAdd("LlamaManager");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var text = await ReadLimitedHttpTextAsync(response, 8 << 20, deadline.Token).ConfigureAwait(false);
            return JsonNode.Parse(text) as JsonArray;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub 发布元数据读取超过 2 分钟上限；已安装引擎不变。");
        }
    }

    /// <summary>Repositories that support one-click download, by backend id.</summary>
    public static string? RepoFor(string backendId) => backendId switch
    {
        "llamacpp" => EngineCatalog.LlamaCppRepo,
        "polystrata" => EngineCatalog.PolyStrataRepo,
        "kvmem" => EngineCatalog.KvmemRepo,
        _ => null,
    };

    /// <summary>Builds the install plan for a backend from a release, or null when unsupported.</summary>
    public static EngineInstallPlan? PlanFor(string backendId, ReleaseInfo release, string managerRoot, double? computeCapability = null) => backendId switch
    {
        "llamacpp" => EngineCatalog.LlamaCppPlan(release, managerRoot, computeCapability ?? DetectComputeCapability()),
        "polystrata" => EngineCatalog.PolyStrataPlan(release, managerRoot),
        "kvmem" => EngineCatalog.KvmemPlan(release, managerRoot, computeCapability ?? DetectComputeCapability()),
        _ => null,
    };

    /// <summary>
    /// The GPU's compute capability (e.g. 7.5 for Turing), via nvidia-smi; 0 when unknown.
    /// Decides which CUDA build of llama.cpp to fetch - the 13.4 nightlies no longer carry
    /// kernels for pre-Ampere cards.
    /// </summary>
    private static readonly object CapabilityGate = new();
    private static DateTime CapabilityCheckedAt;
    private static double CachedCapability;

    public static Func<double> DetectComputeCapability { get; set; } = DetectCachedComputeCapability;

    private static double DetectCachedComputeCapability()
    {
        lock (CapabilityGate)
        {
            if (DateTime.UtcNow - CapabilityCheckedAt < TimeSpan.FromMinutes(1)) return CachedCapability;
            CachedCapability = ProbeComputeCapability();
            CapabilityCheckedAt = DateTime.UtcNow;
            return CachedCapability;
        }
    }

    private static double ProbeComputeCapability()
    {
        try
        {
            var output = Services.ProcessProbe.RunAsync("nvidia-smi",
                "--query-gpu=compute_cap --format=csv,noheader", TimeSpan.FromSeconds(10))
                .GetAwaiter().GetResult();
            if (double.TryParse(output.Trim().Split('\n')[0].Trim(),
                    System.Globalization.CultureInfo.InvariantCulture, out var cap))
            {
                LogService.Write($"GPU 计算能力：{cap}（选择 CUDA {(cap < 8.0 ? "12.4" : "13.4")} 构建）。");
                return cap;
            }
            LogService.Write("无法解析 nvidia-smi 的计算能力输出，按 CUDA 13.4 选择：" + output.Trim());
            return 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            LogService.Write("nvidia-smi 探测失败（按 CUDA 13.4 选择）：" + ex.Message);
            return 0;
        }
    }

    /// <summary>Whether the launcher can download this backend's engine automatically.</summary>
    public static bool CanAcquire(string backendId) => RepoFor(backendId) != null;

    /// <summary>
    /// Downloads and installs a backend's engine. Returns the engine directory on success.
    /// The caller decides whether to run this (the GUI/CLI bootstrap does, after a probe fails).
    /// </summary>
    public static async Task<string> AcquireAsync(string backendId, ILogSink log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var selected = await LatestPlanAsync(backendId, log, ct).ConfigureAwait(false);
        log.Write($"选用版本：{selected.Release.Tag}；{selected.Plan.Architecture ?? "x64"}。");
        return await InstallSelectedAsync(selected.Plan, selected.Release, log, ct).ConfigureAwait(false);
    }

    /// <summary>Actual acquisition plans never target a user's legacy/manual directory.</summary>
    public static EngineInstallPlan? ManagedPlanFor(string backendId, ReleaseInfo release, string managerRoot,
        double computeCapability, double maximumCudaVersion = 0) => backendId switch
    {
        "llamacpp" => EngineCatalog.ManagedLlamaCppPlan(release, managerRoot, computeCapability, maximumCudaVersion),
        "polystrata" => EngineCatalog.ManagedPolyStrataPlan(release, managerRoot),
        "kvmem" => EngineCatalog.KvmemPlan(release, managerRoot, computeCapability),
        _ => null,
    };

    public static Func<double> DetectCudaVersion { get; set; } = DetectCachedCudaVersion;
    private static readonly object DriverGate = new();
    private static DateTime DriverCheckedAt;
    private static double CachedDriverCuda;

    public static Task<double> DetectCudaVersionAsync(CancellationToken ct = default) => Task.Run(DetectCudaVersion, ct);

    private static double DetectCachedCudaVersion()
    {
        lock (DriverGate)
        {
            if (DateTime.UtcNow - DriverCheckedAt < TimeSpan.FromMinutes(1)) return CachedDriverCuda;
            try
            {
                var output = ProcessProbe.RunAsync("nvidia-smi", "", TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                CachedDriverCuda = ParseDriverCudaVersion(output);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                CachedDriverCuda = 0;
                LogService.Write("CUDA driver 版本探测失败；不会猜测新版 CUDA 运行库：" + ex.Message);
            }
            DriverCheckedAt = DateTime.UtcNow;
            return CachedDriverCuda;
        }
    }

    /// <summary>Both legacy CUDA Version and the newer split KMD/CUDA UMD Version nvidia-smi headers.</summary>
    public static double ParseDriverCudaVersion(string output)
    {
        if (output.Length > 65536) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(output, @"CUDA(?:\s+UMD)?\s+Version:\s*(\d{1,2}\.\d{1,2})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var cuda) ? cuda : 0;
    }

    private sealed record SelectedPlan(EngineInstallPlan Plan, ReleaseInfo Release);

    private static async Task<SelectedPlan> LatestPlanAsync(string backendId, ILogSink? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var repo = RepoFor(backendId) ?? throw new InvalidOperationException($"{backendId} 不支持自动下载（可选 Prism 变体须手动管理）。");
        var capability = await DetectComputeCapabilityAsync(ct).ConfigureAwait(false);
        if (backendId == "kvmem" && EngineCatalog.KvmemArchitecture(capability) == null)
            throw new InvalidOperationException("无法确定受支持的精确 KVMem GPU 架构；不猜测或跨架构更新。");
        var driverCuda = backendId == "llamacpp" ? await DetectCudaVersionAsync(ct).ConfigureAwait(false) : 0;
        log?.Write($"查询 {repo} 的最新适用版本…");
        if (backendId == "llamacpp") log?.Write(driverCuda > 0
            ? $"本机 driver 支持 CUDA {driverCuda}；只配对同一发布、同一 CUDA minor 的 binary/cudart。"
            : "未能读取 driver CUDA 版本；仅考虑保守的官方 CUDA 12.4 配对，不猜测 CUDA 13.x。");
        var releases = await ReleasesFetcher(repo, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"无法获取 {repo} 的发布信息（网络或仓库不可用）。");
        SelectedPlan? selected = null;
        foreach (var node in releases.Take(30))
        {
            ct.ThrowIfCancellationRequested();
            if (node == null || node["draft"]?.GetValue<bool>() == true) continue;
            var release = EngineCatalog.ParseRelease(node);
            var plan = ManagedPlanFor(backendId, release, ConfigService.GetManagerRoot(), capability, driverCuda);
            if (plan != null && (selected == null || DoctorService.IsOutdated(selected.Release.Tag, release.Tag)))
                selected = new SelectedPlan(plan, release);
        }
        return selected ?? throw new InvalidOperationException($"{repo} 最近 30 个发布没有本机可用的完整引擎/匹配运行库资产；旧目录与配置保持不变。");
    }

    /// <summary>Doctor uses exactly the same compatible-release selection as the actual update path.</summary>
    public static async Task<ReleaseInfo?> LatestCompatibleReleaseAsync(string backendId, CancellationToken ct = default) =>
        (await LatestPlanAsync(backendId, null, ct).ConfigureAwait(false)).Release;

    private static Task<string> InstallSelectedAsync(EngineInstallPlan plan, ReleaseInfo release, ILogSink log, CancellationToken ct) =>
        plan.BackendId == "kvmem" ? InstallKvmemAsync(plan, release, log, ct) : InstallManagedAsync(plan, release, log, ct);

    private static readonly SemaphoreSlim ManagedInstallGate = new(1, 1);

    /// <summary>Only the matching release's literal NVIDIA wheel pins are resolved; never import/execute upstream setup.py.</summary>
    public static Func<string, CancellationToken, Task<ReleaseAsset?>> RuntimeWheelFetcher { get; set; } = FetchRuntimeWheelAsync;

    /// <summary>Setup command seam for isolated venv/pip/cancellation tests; production kills only its own setup process.</summary>
    public static Func<System.Diagnostics.ProcessStartInfo, TimeSpan, CancellationToken, Task<BuiltInInstallation.ProbeOutput>> SetupProcessRunner { get; set; } =
        (start, timeout, ct) => BuiltInInstallation.NativeProbe(start, timeout, ct);

    private static async Task<string> InstallManagedAsync(EngineInstallPlan plan, ReleaseInfo release, ILogSink log, CancellationToken ct)
    {
        await ManagedInstallGate.WaitAsync(ct).ConfigureAwait(false);
        var managerRoot = ConfigService.GetManagerRoot();
        var root = Path.GetFullPath(EngineCatalog.EngineRoot(managerRoot, plan.BackendId));
        string? staging = null;
        string? installed = null;
        var committed = false;
        try
        {
            KvmemInstallation.EnsureNoReparsePoints(root);
            Directory.CreateDirectory(root);
            using var lease = new FileStream(Path.Combine(root, ".acquire.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var config = ConfigService.ReadConfig();
            var previous = config.Engines.GetValueOrDefault(plan.BackendId) ?? new EngineSettings();
            var expected = new EngineSettings { ExecutablePath = previous.ExecutablePath, Parameters = new Dictionary<string, string>(previous.Parameters) };
            staging = Path.Combine(root, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, BuiltInInstallation.PendingMarker), plan.BackendId + "/" + release.Tag);
            var downloads = Path.Combine(managerRoot, "downloads", plan.BackendId, release.Tag);
            KvmemInstallation.EnsureNoReparsePoints(downloads);
            Directory.CreateDirectory(downloads);
            var records = new List<BuiltInInstallation.InstalledDownload>();
            var stagedEngineDir = plan.BackendId == "polystrata" ? Path.Combine(staging, "engine-poly") : staging;
            foreach (var asset in plan.EngineArchives)
            {
                if (EngineCatalog.ParseDigest(asset.Sha256) == null)
                    throw new InvalidDataException("官方引擎/运行库发布没有可信 SHA-256，拒绝启用未校验资产：" + asset.Name);
                var archive = await FetchVerifiedArchiveAsync(asset, downloads, records, log, ct).ConfigureAwait(false);
                await ExtractManagedArchiveAsync(archive, stagedEngineDir, log, ct).ConfigureAwait(false);
            }
            if (plan.SourceArchive != null)
            {
                var source = await FetchVerifiedArchiveAsync(plan.SourceArchive, downloads, records, log, ct).ConfigureAwait(false);
                // Runtime source only: bench logs, demonstration GGUFs and model/config folders
                // are not needed to serve a model and must not make future owned cleanup ambiguous.
                await ExtractManagedArchiveAsync(source, staging, log, ct, runtimeSourceOnly: true).ConfigureAwait(false);
            }
            var cudaVersion = plan.CudaVersion;
            IReadOnlyList<string> runtimePins = [];
            if (plan.BackendId == "polystrata")
            {
                var exe = Path.Combine(stagedEngineDir, StrataBackend.PolyProfile.EngineExeName);
                var build = BuiltInInstallation.ReadPolyBuild(exe) ?? throw new InvalidDataException("PolyStrata 发布缺少可读 BUILD.json。");
                var releaseVersion = release.Tag.TrimStart('v', 'V');
                var baseVersion = System.Text.RegularExpressions.Regex.Replace(releaseVersion, @"-poly\.\d+(?:[.-][A-Za-z0-9]+)*$", "");
                if (build.Version != releaseVersion && build.Version != baseVersion)
                    throw new InvalidDataException($"PolyStrata BUILD.version {build.Version ?? "未知"} 与发布 {release.Tag} 不一致；不会伪改 BUILD.json。");
                if (build.Backend is not (null or "cuda") || build.CudaVersion == null)
                    throw new InvalidDataException("PolyStrata 下载不是可识别的官方 CUDA 发布；不会将 HIP/手工构建伪装成 CUDA。");
                cudaVersion = build.CudaVersion;
                var capability = await DetectComputeCapabilityAsync(ct).ConfigureAwait(false);
                var architecture = double.IsFinite(capability) ? (int)Math.Round(capability * 10) : 0;
                if (architecture <= 0 || !build.Architectures.Contains(architecture))
                    throw new InvalidDataException($"PolyStrata 发布不包含本机精确 sm{architecture} 内核，或 GPU 无法探测；不安装不匹配资产。");
                var driverCuda = await DetectCudaVersionAsync(ct).ConfigureAwait(false);
                if (driverCuda <= 0 || !Version.TryParse(cudaVersion, out var requiredCuda) ||
                    !Version.TryParse(driverCuda.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), out var driverVersion) || requiredCuda > driverVersion)
                    throw new InvalidDataException($"PolyStrata 发布需要 CUDA {cudaVersion}，本机 driver 支持 {driverCuda}；先修复/升级 driver，安装未提交。");
                var setup = Path.Combine(staging, "setup.py");
                if (!File.Exists(setup) || new FileInfo(setup).Length > (2 << 20))
                    throw new InvalidDataException("PolyStrata 同版本源码缺少有界可读 setup.py，不能确定其 CUDA wheel pins。");
                runtimePins = BuiltInInstallation.ParseCudaWheelPins(await File.ReadAllTextAsync(setup, ct).ConfigureAwait(false), cudaVersion);
                if (runtimePins.Count == 0)
                    throw new InvalidDataException($"PolyStrata CUDA {cudaVersion} 与同发布 setup.py 的 exact NVIDIA pins 无法配对；拒绝猜测其它引擎运行库。");
                foreach (var pin in runtimePins)
                {
                    log.Write("获取 PolyStrata 同发布运行库：" + pin);
                    var wheel = await RuntimeWheelFetcher(pin, ct).ConfigureAwait(false)
                        ?? throw new InvalidDataException("没有匹配 Windows x64 的可信 NVIDIA wheel：" + pin);
                    if (EngineCatalog.ParseDigest(wheel.Sha256) == null) throw new InvalidDataException("NVIDIA wheel 没有可信 SHA-256：" + pin);
                    var archive = await FetchVerifiedArchiveAsync(wheel, downloads, records, log, ct).ConfigureAwait(false);
                    await ExtractRuntimeDllsAsync(archive, stagedEngineDir, log, ct).ConfigureAwait(false);
                }
                foreach (var required in new[] { "serve/server.py", "requirements.txt", "tools/strata_tokenizer.py", "tools/gguf_reader.py" })
                    if (!File.Exists(Path.Combine(staging, required))) throw new InvalidDataException("PolyStrata 同发布源码不完整：" + required);
                if (!File.Exists(Path.Combine(stagedEngineDir, "strata-vision.exe"))) throw new InvalidDataException("PolyStrata 发布缺少配套 strata-vision.exe。");
            }
            if (cudaVersion == null) throw new InvalidDataException("无法确定引擎发布的匹配 CUDA 运行库版本。");
            var runtimeProblem = BuiltInInstallation.ValidateCudaLibraries(stagedEngineDir, cudaVersion);
            if (runtimeProblem != null) throw new InvalidDataException(runtimeProblem);
            var shippedLogs = await CaptureShippedLogsAsync(staging, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var targetName = release.Tag + "-cuda-" + cudaVersion;
            var target = Path.GetFullPath(Path.Combine(root, targetName));
            if (!PathIdentity.Equals(Path.GetDirectoryName(target), root)) throw new IOException("安装目标不是独立 direct-child 版本目录。");
            if (Directory.Exists(target) || File.Exists(target)) target += "-" + Guid.NewGuid().ToString("N")[..8];
            KvmemInstallation.EnsureNoReparsePoints(target);
            Directory.Move(staging, target);
            installed = target;
            staging = null;
            var engineDirectory = plan.BackendId == "polystrata" ? Path.Combine(target, "engine-poly") : target;
            var executable = Path.Combine(engineDirectory, plan.BackendId == "polystrata" ? "strata-poly.exe" : "llama-server.exe");
            if (!File.Exists(executable)) throw new InvalidDataException("下载包缺少预期的原生引擎：" + executable);
            var paths = new Dictionary<string, string>
            {
                ["packageRoot"] = target, ["releaseVersion"] = release.Tag, ["runtimeCuda"] = cudaVersion,
            };
            string? privatePython = null;
            if (plan.BackendId == "polystrata")
            {
                paths["serveRoot"] = target;
                privatePython = await PreparePrivatePythonAsync(target, config, log, ct).ConfigureAwait(false);
                if (privatePython != null) paths["pythonPath"] = privatePython;
            }
            var executableDigest = await EngineDownloadService.Sha256Async(executable, ct).ConfigureAwait(false);
            var metadata = new BuiltInInstallation.ReleaseMetadata(1, "LlamaManager", plan.BackendId,
                RepoFor(plan.BackendId)!, release.Tag, release.Tag.TrimStart('v', 'V'), plan.BackendId == "polystrata" ? "x64" : "cuda" + cudaVersion,
                target, DateTime.UtcNow, Path.GetRelativePath(target, executable), executableDigest, records)
            {
                CudaVersion = cudaVersion, RuntimePackages = runtimePins, ShippedLogs = shippedLogs,
                PythonRelativePath = privatePython == null ? null : Path.GetRelativePath(target, privatePython),
            };
            BuiltInInstallation.WriteReleaseMetadata(target, metadata);
            if (plan.BackendId == "llamacpp")
            {
                var validation = await BuiltInInstallation.ProbeLlamaAsync(executable, ct).ConfigureAwait(false);
                if (!validation.Available || validation.Variant || !string.Equals(validation.Version, release.Tag, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"下载包原生 --version 与 {release.Tag} 不一致或运行库不可用：" + validation.Detail);
            }
            else
            {
                var installedSettings = new EngineSettings
                {
                    ExecutablePath = executable, Parameters = new Dictionary<string, string>(previous.Parameters),
                };
                foreach (var (key, value) in paths) installedSettings.Parameters[key] = value;
                var probeConfig = new ManagerConfig { Strata = config.Strata };
                probeConfig.Engines[plan.BackendId] = installedSettings;
                var validation = await BuiltInInstallation.ProbeAsync(plan.BackendId, probeConfig, ct).ConfigureAwait(false);
                if (!validation.Available || validation.Variant || validation.Version == null)
                    throw new InvalidDataException("PolyStrata 最终版本目录的 native/Python/运行库检查失败：" + validation.Detail);
                var visionProblem = await BuiltInInstallation.ValidateVisionAsync(Path.Combine(engineDirectory, "strata-vision.exe"), ct).ConfigureAwait(false);
                if (visionProblem != null) throw new InvalidDataException(visionProblem);
            }
            ct.ThrowIfCancellationRequested();
            ConfigService.SaveEngineInstallation(plan.BackendId, executable, paths, ct, expected);
            committed = true;
            try { File.Delete(Path.Combine(target, BuiltInInstallation.PendingMarker)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Write("安装路径已原子提交；pending 清理提示：" + ex.Message); }
            await Task.Run(() => RegisterOwnedAndPrune(root, metadata, log, ct)).ConfigureAwait(false);
            log.Write($"{plan.BackendId} {release.Tag} / CUDA {cudaVersion} 已校验安装到独立版本目录：{target}。");
            log.Write("仅提交新安装路径；未就地覆盖旧引擎，未停止服务，活动后端/模型/用户参数/预设/Running 均保留。");
            return target;
        }
        finally
        {
            if (staging != null) RemoveOwnedInstallation(staging, root, log);
            if (!committed && installed != null) RemoveOwnedInstallation(installed, root, log);
            ManagedInstallGate.Release();
        }
    }

    private static async Task<string?> PreparePrivatePythonAsync(string packageRoot, ManagerConfig config, ILogSink log, CancellationToken ct)
    {
        var settings = config.Engines.GetValueOrDefault("polystrata");
        var configured = settings?.Parameters.GetValueOrDefault("pythonPath");
        if (string.IsNullOrWhiteSpace(configured)) configured = config.Strata.PythonPath;
        var oldRoot = settings?.Parameters.GetValueOrDefault("packageRoot") ?? config.Strata.PackageRoot;
        var oldMetadata = string.IsNullOrWhiteSpace(oldRoot) ? null : BuiltInInstallation.ReadReleaseMetadata(oldRoot);
        var wasPrivate = !string.IsNullOrWhiteSpace(configured) && oldMetadata?.PythonRelativePath != null &&
            PathIdentity.Equals(configured, Path.Combine(oldRoot!, oldMetadata.PythonRelativePath));
        if (!string.IsNullOrWhiteSpace(configured) && !wasPrivate)
        {
            // An explicit user interpreter is never pip-upgraded or repointed by an engine update.
            var problem = await BuiltInInstallation.ValidatePythonAsync(configured, packageRoot, ct).ConfigureAwait(false);
            if (problem != null) throw new InvalidDataException("保留用户指定 Python，但它不能运行新发布；请手动修复依赖或清空 pythonPath：" + problem);
            log.Write("保留已验证的用户指定 Python：" + configured);
            return null;
        }
        var basePython = StrataBackend.ResolvePython(config, StrataBackend.PolyProfile);
        if (basePython == null)
        {
            var python = await PythonInstaller.EnsureAsync(log, ct).ConfigureAwait(false);
            if (!python.Ok || python.PythonPath == null) throw new InvalidDataException("无法准备 Python 3.10+：" + python.Detail);
            basePython = python.PythonPath;
        }
        var venv = Path.Combine(packageRoot, "runtime", "venv");
        var create = BuiltInInstallation.CreateProbeStart(basePython, "-m", "venv", venv);
        log.Write("在最终版本目录创建独立 Python venv（旧服务/全局 venv 不修改）：" + venv);
        var created = await SetupProcessRunner(create, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        if (created.ExitCode != 0) throw new IOException("私有 venv 创建失败：" + created.Output);
        var pythonExe = Path.Combine(venv, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) throw new IOException("私有 venv 未提供 python.exe。");
        var requirements = Path.Combine(packageRoot, "requirements.txt");
        if (!File.Exists(requirements) || new FileInfo(requirements).Length > (1 << 20))
            throw new InvalidDataException("同发布 requirements.txt 缺失或异常过大，不能猜测依赖。");
        var pip = BuiltInInstallation.CreateProbeStart(pythonExe, "-m", "pip", "install", "--quiet", "--disable-pip-version-check",
            "--no-input", "--only-binary=:all:", "-r", requirements);
        pip.Environment["PIP_CACHE_DIR"] = Path.Combine(packageRoot, "runtime", "pip-cache");
        log.Write("安装该 PolyStrata 发布的 pinned requirements（仅新版本私有 venv）。");
        var installed = await SetupProcessRunner(pip, TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);
        if (installed.ExitCode != 0) throw new IOException("私有 venv 依赖安装失败：" + installed.Output);
        var problemAfter = await BuiltInInstallation.ValidatePythonAsync(pythonExe, packageRoot, ct).ConfigureAwait(false);
        if (problemAfter != null) throw new InvalidDataException(problemAfter);
        return pythonExe;
    }

    private static async Task<string> FetchVerifiedArchiveAsync(ReleaseAsset asset, string downloads,
        List<BuiltInInstallation.InstalledDownload> records, ILogSink log, CancellationToken ct)
    {
        var archive = await ArchiveFetcher(asset, downloads, log, ct).ConfigureAwait(false);
        var actual = await EngineDownloadService.Sha256Async(archive, ct).ConfigureAwait(false);
        var expected = EngineCatalog.ParseDigest(asset.Sha256);
        if (expected != null && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new EngineDownloadService.ChecksumMismatchException(expected, actual);
        var length = new FileInfo(archive).Length;
        if (length <= 0) throw new InvalidDataException("下载包为空：" + asset.Name);
        records.Add(new(asset.Name, asset.Url, Path.GetFullPath(archive), actual, length));
        log.Write(expected == null ? "已记录 HTTPS 源码包 SHA-256（上游未单独公布 digest）：" + actual : "发布 SHA-256 校验通过：" + asset.Name);
        return archive;
    }

    private static async Task<ReleaseAsset?> FetchRuntimeWheelAsync(string pin, CancellationToken ct)
    {
        var parts = pin.Split("==");
        if (parts.Length != 2) return null;
        using var response = await Http.GetAsync($"https://pypi.org/pypi/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/json", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await ReadLimitedHttpTextAsync(response, 1 << 20, ct).ConfigureAwait(false);
        var data = JsonNode.Parse(text);
        if (data?["urls"] is not JsonArray urls) return null;
        var prefix = parts[0].Replace('-', '_') + "-" + parts[1] + "-";
        foreach (var node in urls.Take(64))
        {
            var name = node?["filename"]?.GetValue<string>();
            var digest = EngineCatalog.ParseDigest(node?["digests"]?["sha256"]?.GetValue<string>());
            var url = node?["url"]?.GetValue<string>();
            if (name != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith("-py3-none-win_amd64.whl", StringComparison.OrdinalIgnoreCase) &&
                digest != null && url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "files.pythonhosted.org")
                return new ReleaseAsset(name, url, digest);
        }
        return null;
    }

    private static async Task<string> ReadLimitedHttpTextAsync(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new IOException("发布/运行库元数据超过读取上限。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var text = new System.Text.StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) return text.ToString();
            if (text.Length + read > maximum) throw new IOException("发布/运行库元数据超过读取上限。");
            text.Append(buffer, 0, read);
        }
    }

    private static void ValidateArchiveEntry(ZipArchiveEntry entry)
    {
        var name = entry.FullName.Replace('\\', '/');
        if (name.StartsWith('/') || Path.IsPathRooted(name) || name.Split('/').Any(segment => segment is "." or ".." ||
            segment.Contains(':') || segment.EndsWith(' ') || segment.EndsWith('.') ||
            System.Text.RegularExpressions.Regex.IsMatch(segment, @"^(?:con|prn|nul|aux|com[1-9]|lpt[1-9])(?:\..*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new IOException("压缩包包含不安全 Windows 路径：" + entry.FullName);
        var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
        if (unixType == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            throw new IOException("压缩包包含符号链接/目录联接：" + entry.FullName);
        var leaf = name.TrimEnd('/').Split('/').LastOrDefault();
        if (leaf is BuiltInInstallation.PendingMarker or BuiltInInstallation.ReleaseMetadataName or ".llama-manager-owned.json")
            throw new IOException("压缩包包含保留的 launcher 安装标记。");
    }

    private static async Task<int> ExtractManagedArchiveAsync(string archive, string destination, ILogSink log, CancellationToken ct, bool runtimeSourceOnly = false)
    {
        var root = Path.GetFullPath(destination);
        KvmemInstallation.EnsureNoReparsePoints(root);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 100000) throw new IOException("引擎压缩包成员数量异常过大。");
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToArray();
        foreach (var entry in zip.Entries) { ct.ThrowIfCancellationRequested(); ValidateArchiveEntry(entry); }
        var first = names.FirstOrDefault(n => n.Length > 0);
        var slash = first?.IndexOf('/') ?? -1;
        var prefix = slash > 0 && names.All(n => n.Length == 0 || n.StartsWith(first![..(slash + 1)], StringComparison.Ordinal))
            ? first![..(slash + 1)] : null;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Target, bool Directory)>();
        long expanded = 0;
        for (var i = 0; i < zip.Entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var name = prefix == null ? names[i] : names[i][prefix.Length..];
            if (name.Length == 0) continue;
            if (runtimeSourceOnly && !name.StartsWith("serve/", StringComparison.Ordinal) && !name.StartsWith("tools/", StringComparison.Ordinal) &&
                name is not ("requirements.txt" or "setup.py" or "LICENSE" or "LICENSE.md" or "LICENSE.txt" or "NOTICE" or "CMakeLists.txt")) continue;
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !targets.Add(target.TrimEnd(Path.DirectorySeparatorChar))) throw new IOException("压缩包越界或重复路径：" + name);
            expanded += zip.Entries[i].Length;
            if (expanded > (8L << 30)) throw new IOException("引擎压缩包解压大小超过 8 GiB 安全上限。");
            entries.Add((zip.Entries[i], target, name.EndsWith('/')));
        }
        var count = 0;
        foreach (var (entry, target, directory) in entries)
        {
            ct.ThrowIfCancellationRequested();
            KvmemInstallation.EnsureNoReparsePoints(target);
            if (directory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            await input.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
            count++;
        }
        log.Write($"已安全解压 {count} 个文件到本次新版本目录 {root}（不覆盖现有文件）。");
        return count;
    }

    private static async Task ExtractRuntimeDllsAsync(string archive, string destination, ILogSink log, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        KvmemInstallation.EnsureNoReparsePoints(root);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 4096) throw new IOException("NVIDIA wheel 的成员数量异常过大。");
        foreach (var entry in zip.Entries) { ct.ThrowIfCancellationRequested(); ValidateArchiveEntry(entry); }
        var dlls = zip.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (dlls.Length == 0 || dlls.Length > 64) throw new InvalidDataException("NVIDIA Windows wheel 缺少有界的原生 DLL 集合。");
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in dlls)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/').Split('/').Last();
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!PathIdentity.Equals(Path.GetDirectoryName(target), root) || !targets.Add(target) || File.Exists(target))
                throw new IOException("NVIDIA wheel DLL 重复/冲突，拒绝覆盖：" + name);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            await input.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
        }
        log.Write($"已从同版本 NVIDIA wheel 部署 {dlls.Length} 个 DLL 到新 engine-poly 目录。");
    }

    private static readonly SemaphoreSlim KvmemInstallGate = new(1, 1);

    private static async Task<string> InstallKvmemAsync(EngineInstallPlan plan, ReleaseInfo release, ILogSink log, CancellationToken ct)
    {
        await KvmemInstallGate.WaitAsync(ct).ConfigureAwait(false);
        string? staging = null;
        string? installedRoot = null;
        var committed = false;
        var root = Path.GetFullPath(EngineCatalog.EngineRoot(ConfigService.GetManagerRoot(), "kvmem"));
        try
        {
            KvmemInstallation.EnsureNoReparsePoints(root);
            Directory.CreateDirectory(root);
            // Serializes separate CLI/GUI processes too; no model service is stopped or replaced.
            using var lease = new FileStream(Path.Combine(root, ".acquire.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var originalSettings = ConfigService.ReadConfig().Engines.GetValueOrDefault("kvmem") ?? new EngineSettings();
            var expectedSettings = new EngineSettings { ExecutablePath = originalSettings.ExecutablePath, Parameters = new Dictionary<string, string>(originalSettings.Parameters) };
            if (plan.EngineArchives.Count != 1 || plan.KvmemArchitecture == null)
                throw new InvalidOperationException("KVMem 必须使用一个精确架构的完整 unified 包。");
            var asset = plan.EngineArchives[0];
            var digest = EngineCatalog.ParseDigest(asset.Sha256);
            if (digest == null && plan.ChecksumAsset != null)
            {
                var checksum = await ChecksumFetcher(plan.ChecksumAsset, ct).ConfigureAwait(false);
                digest = EngineCatalog.ParseChecksum(checksum, asset.Name);
            }
            if (digest == null) throw new InvalidOperationException("KVMem 缺少可信的 SHA-256，拒绝安装未校验的包。");
            asset = asset with { Sha256 = digest };
            var downloads = Path.Combine(ConfigService.GetManagerRoot(), "downloads", "kvmem");
            KvmemInstallation.EnsureNoReparsePoints(downloads);
            Directory.CreateDirectory(downloads);
            var archive = await ArchiveFetcher(asset, downloads, log, ct).ConfigureAwait(false);
            // Independently verify even an injected/cached archive fetcher before extracting anything.
            var actual = await EngineDownloadService.Sha256Async(archive, ct).ConfigureAwait(false);
            if (!actual.Equals(digest, StringComparison.OrdinalIgnoreCase))
                throw new EngineDownloadService.ChecksumMismatchException(digest, actual);
            ct.ThrowIfCancellationRequested();
            log.Write($"KVMem SHA-256 校验通过：{actual}。");
            staging = Path.Combine(root, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, KvmemInstallation.PendingMarker), asset.Name);
            await ExtractKvmemBundleAsync(archive, staging, log, ct, allowPendingMarker: true).ConfigureAwait(false);
            var layout = KvmemInstallation.Validate(staging, plan.KvmemArchitecture, release.Tag);
            if (!layout.Ok) throw new InvalidDataException(layout.Detail);
            // Snapshot only logs from this freshly verified extraction, before any native/Python
            // process starts. Never adopt or fingerprint logs in an older manual installation.
            var shippedLogs = await CaptureShippedLogsAsync(staging, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(plan.EngineDir);
            if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("KVMem 安装目标不是独立版本/显卡目录。");
            // An existing version is immutable. A repair/unconditional install gets a fresh sibling.
            if (Directory.Exists(target) || File.Exists(target)) target += "-" + Guid.NewGuid().ToString("N")[..8];
            KvmemInstallation.EnsureNoReparsePoints(target);
            Directory.Move(staging, target);
            installedRoot = target;
            staging = null;
            // Validate the final relocated Python/native layout before committing any setting.
            // The pending marker prevents this uncommitted sibling from becoming a default root.
            var runtime = await KvmemInstallation.ValidateRuntimeAsync(target, ct).ConfigureAwait(false);
            if (!runtime.Ok) throw new InvalidDataException(runtime.Detail);
            ct.ThrowIfCancellationRequested();
            var worker = Path.Combine(target, KvmemInstallation.WorkerRelativePath);
            var workerDigest = await EngineDownloadService.Sha256Async(worker, ct).ConfigureAwait(false);
            var metadata = new BuiltInInstallation.ReleaseMetadata(1, "LlamaManager", "kvmem",
                EngineCatalog.KvmemRepo, release.Tag, runtime.Version!, "sm" + runtime.Architecture,
                target, DateTime.UtcNow, KvmemInstallation.WorkerRelativePath, workerDigest,
                [new BuiltInInstallation.InstalledDownload(asset.Name, asset.Url, Path.GetFullPath(archive), actual, new FileInfo(archive).Length)])
            { PythonRelativePath = KvmemInstallation.PythonRelativePath, ShippedLogs = shippedLogs };
            BuiltInInstallation.WriteReleaseMetadata(target, metadata);
            ct.ThrowIfCancellationRequested();
            // Keep an explicit user ExecutablePath untouched; this backend resolves the bundled
            // worker from packageRoot. Installation path presets migrate narrowly in the same transaction.
            ConfigService.SaveEngineInstallation("kvmem", expectedSettings.ExecutablePath, new Dictionary<string, string>
            {
                ["packageRoot"] = target,
                ["serveRoot"] = Path.Combine(target, KvmemInstallation.ServeRootRelativePath),
                ["bundlePython"] = Path.Combine(target, KvmemInstallation.PythonRelativePath),
            }, ct, expectedSettings);
            committed = true; // cancellation after this atomic settings commit is a successful install
            try { File.Delete(Path.Combine(target, KvmemInstallation.PendingMarker)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Write("KVMem 路径已记录；安装标记清理提示：" + ex.Message);
            }
            await Task.Run(() => RegisterOwnedAndPrune(root, metadata, log, ct)).ConfigureAwait(false);
            log.Write($"KVMem {runtime.Version} / sm{runtime.Architecture} 已完整安装并验证：{target}。");
            log.Write("仅记录 kvmem 安装路径；未切换活动后端，未修改模型/用户参数/预设/运行会话，原 PolyStrata 保留。");
            return target;
        }
        finally
        {
            if (staging != null) RemoveOwnedInstallation(staging, root, log);
            if (!committed && installedRoot != null) RemoveOwnedInstallation(installedRoot, root, log);
            KvmemInstallGate.Release();
        }
    }

    private static async Task<IReadOnlyList<BuiltInInstallation.ShippedPackageFile>> CaptureShippedLogsAsync(string freshRoot, CancellationToken ct)
    {
        var root = Path.GetFullPath(freshRoot);
        KvmemInstallation.EnsureNoReparsePoints(root);
        var logs = Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories).Take(1025).ToArray();
        if (logs.Length > 1024) throw new InvalidDataException("可信包随附日志数量超出 1024 上限。");
        var result = new List<BuiltInInstallation.ShippedPackageFile>();
        foreach (var path in logs)
        {
            ct.ThrowIfCancellationRequested();
            KvmemInstallation.EnsureNoReparsePoints(path);
            var length = new FileInfo(path).Length;
            if (length > (64L << 20)) throw new InvalidDataException("单个随包日志超出 64 MiB 上限，不能安全记录：" + path);
            result.Add(new(Path.GetRelativePath(root, path), await EngineDownloadService.Sha256Async(path, ct).ConfigureAwait(false), length));
        }
        return result;
    }

    /// <summary>Post-commit maintenance only. Failure/cancellation never turns a committed install into a rollback.</summary>
    private static void RegisterOwnedAndPrune(string engineRoot, BuiltInInstallation.ReleaseMetadata metadata, ILogSink log, CancellationToken ct)
    {
        try
        {
            var downloads = Path.GetFullPath(Path.Combine(ConfigService.GetManagerRoot(), "downloads", metadata.BackendId));
            var manifest = new EngineVersionRetention.OwnershipManifest
            {
                BackendId = metadata.BackendId, Version = metadata.Version, Architecture = metadata.Architecture,
                Root = metadata.Root, InstalledAtUtc = new DateTimeOffset(metadata.InstalledUtc),
                ShippedLogs = metadata.ShippedLogs.Select(file => new EngineVersionRetention.OwnedPackageFile(file.RelativePath, file.Sha256, file.Length)).ToArray(),
                Downloads = metadata.Downloads.Select(asset => new EngineVersionRetention.OwnedDownload(
                    Path.GetDirectoryName(Path.GetFullPath(asset.DownloadPath))!, Path.GetFullPath(asset.DownloadPath), asset.Sha256, asset.Length)).ToArray(),
            };
            // The root is already committed; register its ownership even if cancellation arrived
            // just afterwards. Cleanup is separately cancellable and cannot undo the path transaction.
            if (!File.Exists(Path.Combine(metadata.Root, EngineVersionRetention.ManifestFileName)))
                EngineVersionRetention.WriteOwnedManifest(engineRoot, manifest, downloads, log.Write, CancellationToken.None);
            var config = ConfigService.ReadConfig();
            var protections = CollectProtectedInstallationPaths(config);
            var sessions = new List<EngineVersionRetention.RunningSessionReference>();
            foreach (var (slot, session) in config.Running)
            {
                var referenced = new List<string>();
                var runConfig = new LauncherArguments(session.Args).Get("--config");
                if (!string.IsNullOrWhiteSpace(runConfig))
                {
                    try
                    {
                        using var file = new FileStream(runConfig, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (file.Length > (1 << 20)) throw new IOException("running runconfig 超过读取上限。");
                        using var json = System.Text.Json.JsonDocument.Parse(file);
                        foreach (var key in new[] { "exe", "cwd" })
                            if (json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String &&
                                value.GetString() is { Length: > 0 } path) referenced.Add(path);
                        if (json.RootElement.TryGetProperty("lib_dirs", out var directories) && directories.ValueKind == System.Text.Json.JsonValueKind.Array)
                            referenced.AddRange(directories.EnumerateArray().Where(d => d.ValueKind == System.Text.Json.JsonValueKind.String).Select(d => d.GetString()!).Where(p => !string.IsNullOrWhiteSpace(p)));
                        if (json.RootElement.TryGetProperty("vision", out var vision) && vision.ValueKind == System.Text.Json.JsonValueKind.Object &&
                            vision.TryGetProperty("exe", out var visionExe) && visionExe.ValueKind == System.Text.Json.JsonValueKind.String && visionExe.GetString() is { Length: > 0 } visionPath)
                            referenced.Add(visionPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                    {
                        // A live global Python can hold a package without its exe path naming that
                        // package. Unknown runtime references conservatively protect this backend tree.
                        if (slot is "polystrata" or "kvmem") referenced.Add(EngineCatalog.EngineRoot(ConfigService.GetManagerRoot(), slot));
                        log.Write("无法读 recorded session 的 runconfig；只读保守保留相关版本：" + ex.Message);
                    }
                }
                sessions.Add(new(session.ProcessId, session.StartTimeUtcTicks, session.EnginePath) { ReferencedRoots = referenced });
            }
            var result = EngineVersionRetention.Run(new EngineVersionRetention.RetentionRequest(engineRoot, metadata.BackendId)
            {
                CurrentRoots = [metadata.Root], ProtectedRoots = protections,
                RunningSessions = sessions, DownloadRoot = downloads,
            }, log.Write, ct);
            log.Write($"版本保留：{result.Kept.Count} 保留，{result.Pruned.Count} 清理，{result.Blocked.Count} 安全阻止，{result.Skipped.Count} 未认领/跳过；取消={result.Cancelled}。");
        }
        catch (Exception ex)
        {
            log.Write("新安装已提交；ownership/旧版本清理未完成（不回滚、不认领手工目录）：" + ex.Message);
        }
    }

    private static IReadOnlyList<string> CollectProtectedInstallationPaths(ManagerConfig config)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathKeys = new[] { "packageRoot", "serveRoot", "pythonPath", "bundlePython" };
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var canonical = Path.GetFullPath(value);
            // Legacy installs live directly in engines/<id>, which is now also the container of
            // managed revisions. Protect the legacy payload, not every sibling underneath it.
            var container = BackendRegistry.All().Where(b => b.Descriptor.BuiltIn && CanAcquire(b.Descriptor.Id))
                .Select(b => EngineCatalog.EngineRoot(ConfigService.GetManagerRoot(), b.Descriptor.Id))
                .Any(root => PathIdentity.Equals(root, canonical));
            if (container)
            {
                foreach (var relative in new[] { "engine-poly", "serve", "llama-server.exe", "runtime/python", "workers", "backends" })
                    paths.Add(Path.Combine(canonical, relative));
            }
            else paths.Add(canonical);
        }
        Add(config.Strata.PackageRoot); Add(config.Strata.ServeRoot); Add(config.Strata.PythonPath);
        Add(config.ModelPath); Add(config.EmbeddingModelPath); Add(config.ModelsDirectory);
        foreach (var (model, _) in config.ModelPresets) Add(model);
        foreach (var (_, settings) in config.Engines)
        {
            Add(settings.ExecutablePath); Add(settings.ModelPath);
            foreach (var key in pathKeys) Add(settings.Parameters.GetValueOrDefault(key));
            foreach (var (model, preset) in settings.ModelPresets)
            {
                Add(model);
                foreach (var key in pathKeys) Add(preset.GetValueOrDefault(key));
            }
        }
        return paths.ToArray();
    }

    private static void RemoveOwnedInstallation(string directory, string parent, ILogSink log)
    {
        try
        {
            var resolved = Path.GetFullPath(directory);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(resolved, KvmemInstallation.PendingMarker)))
                throw new IOException("拒绝清理不属于本次 KVMem 安装的目录。");
            KvmemInstallation.EnsureNoReparsePoints(resolved);
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write("KVMem 未完成目录未启用；清理提示：" + ex.Message);
        }
    }

    /// <summary>
    /// Safely flattens only the outer archive wrapper into a new empty directory, preserving all
    /// unified package components. Traversal, drive/ADS paths, duplicates and symlink entries fail
    /// before any archive member is written. Cancellation never records an installation path.
    /// </summary>
    public static Task<int> ExtractKvmemBundleAsync(string archive, string destination, ILogSink log, CancellationToken ct = default) =>
        ExtractKvmemBundleAsync(archive, destination, log, ct, allowPendingMarker: false);

    private static async Task<int> ExtractKvmemBundleAsync(string archive, string destination, ILogSink log, CancellationToken ct, bool allowPendingMarker)
    {
        ct.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(destination);
        KvmemInstallation.EnsureNoReparsePoints(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any(p =>
                !allowPendingMarker || !Path.GetFileName(p).Equals(KvmemInstallation.PendingMarker, StringComparison.Ordinal)))
            throw new IOException("KVMem unified 包只能解压到新空目录，不能覆盖已有引擎。");
        using var zip = ZipFile.OpenRead(archive);
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToArray();
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            if (name.StartsWith('/') || Path.IsPathRooted(name) ||
                name.Split('/').Any(segment => segment is "." or ".." || segment.Contains(':') ||
                    segment.EndsWith(' ') || segment.EndsWith('.')))
                throw new IOException("压缩包包含不安全路径：" + name);
        }
        var first = names.FirstOrDefault(n => n.Length > 0);
        var slash = first?.IndexOf('/') ?? -1;
        var prefix = slash > 0 && names.All(n => n.Length == 0 || n.StartsWith(first![..(slash + 1)], StringComparison.Ordinal))
            ? first![..(slash + 1)] : null;
        var entries = new List<(ZipArchiveEntry Entry, string Target, bool Directory)>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < zip.Entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.Entries[i];
            if (names[i].Length == 0) continue;
            var name = prefix != null ? names[i][prefix.Length..] : names[i];
            if (name.Length == 0) continue;
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new IOException("KVMem 压缩包包含符号链接/目录联接：" + entry.FullName);
            var directory = name.EndsWith('/');
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !targets.Add(target.TrimEnd(Path.DirectorySeparatorChar)))
                throw new IOException("压缩包包含越界或重复路径：" + entry.FullName);
            if (new[] { KvmemInstallation.PendingMarker, BuiltInInstallation.ReleaseMetadataName, ".llama-manager-owned.json" }
                .Any(marker => name.Equals(marker, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("压缩包包含保留的安装标记。");
            entries.Add((entry, target, directory));
        }
        Directory.CreateDirectory(root);
        var count = 0;
        foreach (var (entry, target, directory) in entries)
        {
            ct.ThrowIfCancellationRequested();
            KvmemInstallation.EnsureNoReparsePoints(target);
            if (directory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            await source.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
            count++;
        }
        log.Write($"已安全 flatten 完整 KVMem unified 包 {count} 个文件到新目录 {root}。");
        return count;
    }

    private static async Task<string> FetchChecksumAsync(ReleaseAsset asset, CancellationToken ct)
    {
        using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 65536) throw new IOException("KVMem 校验文件异常过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[65537];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0) return new string(buffer, 0, length);
            length += read;
        }
        throw new IOException("KVMem 校验文件异常过大。");
    }

    /// <summary>The outcome of an update check: whether it installed, and which versions were seen.</summary>
    public sealed record EngineUpdateResult(
        bool Updated, string? InstalledVersion, string? LatestVersion, string? EngineDir)
    {
        public string? PackageRoot { get; init; }
        public ReleaseInfo? Release { get; init; }
        public IReadOnlyList<BuiltInInstallation.InstalledDownload> Downloads { get; init; } = [];
        public string? Detail { get; init; }
    }

    public sealed record EngineMaintenanceResult(string BackendId, EngineUpdateResult? Update, string? Error)
    {
        public bool Ok => Error == null;
    }

    /// <summary>One shared GUI/CLI entry point for every registered downloadable built-in, including missing engines.</summary>
    public static async Task<IReadOnlyList<EngineMaintenanceResult>> UpdateAllAsync(ILogSink log, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var results = new List<EngineMaintenanceResult>();
        foreach (var backend in BackendRegistry.All().Where(b => b.Descriptor.BuiltIn && CanAcquire(b.Descriptor.Id)))
        {
            ct.ThrowIfCancellationRequested();
            var id = backend.Descriptor.Id;
            log.Write("\n=== 更新引擎与匹配运行库：" + id + " ===");
            try { results.Add(new(id, await UpdateAsync(id, log, ct).ConfigureAwait(false), null)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.Write(id + " 更新未提交：" + ex.Message);
                results.Add(new(id, null, ex.Message));
            }
        }
        return results;
    }

    /// <summary>
    /// Updates an already-installed engine to the published release when it is behind.
    /// </summary>
    /// <remarks>
    /// Actual native/package identities are compared with the newest complete compatible release.
    /// Missing engines and known-version runtime failures install into a new immutable sibling.
    /// Unreadable versions and optional/manual variants report a failure, never a blind reinstall.
    /// </remarks>
    public static async Task<EngineUpdateResult> UpdateAsync(string backendId, ILogSink log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanAcquire(backendId)) throw new InvalidOperationException($"{backendId} 不支持自动下载；可选变体须手动管理。");
        var installed = await ProbeInstalledAsync(backendId, ct).ConfigureAwait(false);
        if (installed.Variant) throw new InvalidOperationException(installed.Detail);
        if (installed.Exists && !BuiltInInstallation.IsComparableVersion(backendId, installed.Version))
            throw new InvalidOperationException(installed.Detail + " 无法比较实际发布版本，拒绝盲目覆盖/重复下载；请明确选择可校验的官方版本目录。");
        var selected = await LatestPlanAsync(backendId, log, ct).ConfigureAwait(false);
        var latest = selected.Release.Tag;
        var current = installed.Version;
        if (current != null && BuiltInInstallation.IsComparableVersion(backendId, current) && !DoctorService.IsOutdated(current, latest))
        {
            var newerLocal = DoctorService.IsOutdated(latest, current);
            if (installed.Available)
            {
                // Retry deferred cleanup even without a new release, but never adopt a manual package.
                if (installed.PackageRoot is { } package &&
                    File.Exists(Path.Combine(package, EngineVersionRetention.ManifestFileName)) &&
                    BuiltInInstallation.ReadReleaseMetadata(package) is { } installedMetadata)
                    await Task.Run(() => RegisterOwnedAndPrune(EngineCatalog.EngineRoot(ConfigService.GetManagerRoot(), backendId), installedMetadata, log, ct)).ConfigureAwait(false);
                var detail = newerLocal ? $"本机 {current} 新于远端适用版本 {latest}，不降级。" : $"本机 {current} 与远端 {latest} 相同，原生引擎/运行库可用，不重复下载。";
                log.Write(backendId + "：" + detail);
                return new EngineUpdateResult(false, current, latest, null) { PackageRoot = installed.PackageRoot, Detail = detail };
            }
            if (newerLocal)
                throw new InvalidOperationException($"本机 {current} 新于远端适用版本 {latest}，但运行时不可用；不以旧版本自动覆盖，请修复该版本依赖。" + installed.Detail);
            log.Write(backendId + "：已知版本的运行库/源码不可用，在新独立目录修复；旧目录不改。" + installed.Detail);
        }
        else log.Write($"{backendId}：{(installed.Exists ? "发现更新" : "引擎缺失")}，本机 {current ?? "未安装"}，最新适用 {latest}；安装配套运行库…");
        // Reuse the already selected release/plan: no second query can install a different tag than the comparison.
        var packageRoot = await InstallSelectedAsync(selected.Plan, selected.Release, log, ct).ConfigureAwait(false);
        var metadata = BuiltInInstallation.ReadReleaseMetadata(packageRoot);
        return new EngineUpdateResult(true, current, latest,
            backendId == "polystrata" ? Path.Combine(packageRoot, "engine-poly") : packageRoot)
        {
            PackageRoot = packageRoot, Release = selected.Release, Downloads = metadata?.Downloads ?? [],
            Detail = current != null && !DoctorService.IsOutdated(current, latest) ? "在新版本目录修复了同版本运行库/源码；旧版本可回退。" : "引擎与匹配运行库已验证后原子提交路径。",
        };
    }

    public static Task<BuiltInInstallation.ProbeResult> ProbeInstalledAsync(string backendId, CancellationToken ct = default) =>
        Task.Run(() => BuiltInInstallation.ProbeAsync(backendId, ConfigService.ReadConfig(), ct), ct);

    /// <summary>The actual installed identity shared by doctor/update; unreadable is not synonymous with missing.</summary>
    public static async Task<string?> InstalledVersionAsync(string backendId, CancellationToken ct = default) =>
        (await ProbeInstalledAsync(backendId, ct).ConfigureAwait(false)).Version;

    /// <summary>Legacy entry point retained only to report why arbitrary in-place cudart mixing is no longer safe.</summary>
    public static Task AcquireCudartToAsync(string destinationDir, ILogSink log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Retain the old public signature, but never mix the newest llama runtime into an arbitrary
        // PolyStrata/manual/running executable directory. Matching libraries are installed together
        // with a selected engine release in InstallManagedAsync's new immutable sibling.
        throw new NotSupportedException("不再向已有引擎目录就地混装 cudart。请使用内置引擎更新/初始化：llama.cpp 使用同发布 cudart ZIP，PolyStrata 使用其 BUILD.cuda 与同发布 exact NVIDIA wheel pins。");
    }

    private static async Task<string> FetchAsync(ReleaseAsset asset, string downloadDir, ILogSink log, CancellationToken ct)
    {
        if (Path.GetFileName(asset.Name) != asset.Name || asset.Name.Contains(':'))
            throw new IOException("发布下载文件名包含不安全路径：" + asset.Name);
        var target = Path.Combine(downloadDir, asset.Name);
        if (asset.Sha256 != null && File.Exists(target))
        {
            var cached = await EngineDownloadService.Sha256Async(target, ct).ConfigureAwait(false);
            if (cached.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log.Write("已验证下载缓存：" + target);
                return target;
            }
            log.Write("下载缓存 SHA-256 不匹配，重新下载；旧安装路径保持不变。");
        }
        long lastReport = 0;
        await EngineDownloadService.DownloadAsync(asset.Url, target, asset.Sha256, log.Write, ct, (received, total) =>
        {
            if (received - lastReport < (16 << 20)) return; // log about every 16 MiB
            lastReport = received;
            log.Write(total is { } t && t > 0
                ? $"  {EngineDownloadService.FormatBytes(received)} / {EngineDownloadService.FormatBytes(t)}（{received * 100 / t}%）"
                : $"  {EngineDownloadService.FormatBytes(received)}");
        }).ConfigureAwait(false);
        return target;
    }

    /// <summary>
    /// GitHub source archives wrap everything in a single <c>&lt;repo&gt;-&lt;tag&gt;/</c> folder.
    /// This strips that prefix so <c>serve/</c> lands directly in the package root.
    /// </summary>
    public static int ExtractFlattened(string archive, string destination, ILogSink log)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(archive);
        var prefix = TopLevelPrefix(zip);
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.Length == 0) continue;
            var name = prefix != null && entry.FullName.StartsWith(prefix, StringComparison.Ordinal)
                ? entry.FullName[prefix.Length..]
                : entry.FullName;
            if (name.Length == 0 || name.EndsWith('/')) continue;
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("压缩包包含越界路径：" + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }
        log.Write($"已解压源码 {count} 个文件到 {root}。");
        return count;
    }

    private static string? TopLevelPrefix(ZipArchive zip)
    {
        foreach (var entry in zip.Entries)
        {
            var slash = entry.FullName.IndexOf('/');
            if (slash <= 0) return null;
            var top = entry.FullName[..(slash + 1)];
            // Every entry must share this one top-level folder for it to be a wrapper.
            return zip.Entries.All(e => e.FullName.StartsWith(top, StringComparison.Ordinal)) ? top : null;
        }
        return null;
    }

}
