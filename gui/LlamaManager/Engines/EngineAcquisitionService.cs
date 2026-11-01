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

    private static async Task<JsonArray?> FetchReleasesAsync(string repo, CancellationToken ct)
    {
        // llama.cpp publishes its Windows binaries as prereleases (nightly builds), so the plain
        // /releases/latest is not enough: scan the list and pick the first release that matches.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{repo}/releases?per_page=30");
        request.Headers.UserAgent.ParseAdd("LlamaManager");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(text) as JsonArray;
    }

    /// <summary>Repositories that support one-click download, by backend id.</summary>
    public static string? RepoFor(string backendId) => backendId switch
    {
        "llamacpp" => EngineCatalog.LlamaCppRepo,
        "polystrata" => EngineCatalog.PolyStrataRepo,
        _ => null,
    };

    /// <summary>Builds the install plan for a backend from a release, or null when unsupported.</summary>
    public static EngineInstallPlan? PlanFor(string backendId, ReleaseInfo release, string managerRoot) => backendId switch
    {
        "llamacpp" => EngineCatalog.LlamaCppPlan(release, managerRoot, DetectComputeCapability()),
        "polystrata" => EngineCatalog.PolyStrataPlan(release, managerRoot),
        _ => null,
    };

    /// <summary>
    /// The GPU's compute capability (e.g. 7.5 for Turing), via nvidia-smi; 0 when unknown.
    /// Decides which CUDA build of llama.cpp to fetch - the 13.4 nightlies no longer carry
    /// kernels for pre-Ampere cards.
    /// </summary>
    public static Func<double> DetectComputeCapability { get; set; } = () =>
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
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            LogService.Write("nvidia-smi 探测失败（按 CUDA 13.4 选择）：" + ex.Message);
            return 0;
        }
    };

    /// <summary>Whether the launcher can download this backend's engine automatically.</summary>
    public static bool CanAcquire(string backendId) => RepoFor(backendId) != null;

    /// <summary>
    /// Downloads and installs a backend's engine. Returns the engine directory on success.
    /// The caller decides whether to run this (the GUI/CLI bootstrap does, after a probe fails).
    /// </summary>
    public static async Task<string> AcquireAsync(string backendId, ILogSink log, CancellationToken ct)
    {
        EnsureNotRunning(backendId);
        var repo = RepoFor(backendId)
            ?? throw new InvalidOperationException($"{backendId} 不支持自动下载。");
        log.Write($"查询 {repo} 的最新版本…");
        var releases = await ReleasesFetcher(repo, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"无法获取 {repo} 的发布信息（网络或仓库不可用）。");
        // The newest release is not always the one with binaries (llama.cpp ships nightlies as
        // prereleases), so take the first release that actually yields a usable plan.
        EngineInstallPlan? plan = null;
        ReleaseInfo? chosen = null;
        foreach (var node in releases)
        {
            if (node == null) continue;
            var release = EngineCatalog.ParseRelease(node);
            var candidate = PlanFor(backendId, release, ConfigService.GetManagerRoot());
            if (candidate == null) continue;
            plan = candidate;
            chosen = release;
            break;
        }
        if (plan == null || chosen == null)
            throw new InvalidOperationException($"{repo} 的近期发布没有适用于本机的下载文件。");
        log.Write($"选用版本：{chosen.Tag}");
        return await InstallPlanAsync(plan, chosen.Tag, log, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads and transactionally installs one already-selected plan. Exposed separately so
    /// the complete download-to-config path can be exercised against a local HTTP release fixture.
    /// </summary>
    public static async Task<string> InstallPlanAsync(
        EngineInstallPlan plan,
        string version,
        ILogSink log,
        CancellationToken ct)
    {
        EnsureNotRunning(plan.BackendId);
        var downloadDir = Path.Combine(ConfigService.GetManagerRoot(), "downloads");
        Directory.CreateDirectory(downloadDir);

        // Download every archive before touching the installation. Extract into a same-volume
        // staging tree, then publish with rollback so a failed second download/extract cannot
        // leave the existing engine partly replaced.
        var staged = Path.Combine(downloadDir, $".install-{plan.BackendId}-{Guid.NewGuid():N}");
        var engineStage = Path.Combine(staged, "engine");
        var sourceStage = Path.Combine(staged, "source");
        var backup = Path.Combine(staged, "backup");
        Directory.CreateDirectory(engineStage);
        try
        {
            var downloads = new Dictionary<ReleaseAsset, string>();
            foreach (var asset in plan.AllArchives)
                downloads[asset] = await FetchAsync(asset, downloadDir, log, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            // The user can start the engine while archives download; never replace files beneath
            // a live process. The same check runs again immediately before publishing.
            EnsureNotRunning(plan.BackendId);

            foreach (var asset in plan.EngineArchives)
            {
                var extracted = EngineDownloadService.ExtractZip(downloads[asset], engineStage);
                log.Write($"已暂存 {extracted} 个引擎文件。");
            }

            if (plan.SourceArchive != null && plan.SourceDir != null)
                ExtractFlattened(downloads[plan.SourceArchive], sourceStage, log);

            EnsureNotRunning(plan.BackendId);
            var roots = new List<(string StagingRoot, string DestinationRoot)> { (engineStage, plan.EngineDir) };
            if (plan.SourceDir != null && Directory.Exists(sourceStage)) roots.Add((sourceStage, plan.SourceDir));
            var published = EngineDownloadService.PublishStagedDirectories(roots, backup);
            log.Write($"已安全安装 {published} 个文件。");

            if (!string.IsNullOrWhiteSpace(version))
                UpdateBuildVersion(plan.EngineDir, version);

            RecordPaths(plan.BackendId, plan, log);
            return plan.EngineDir;
        }
        finally
        {
            try { if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true); } catch { }
        }
    }

    private static void EnsureNotRunning(string backendId)
    {
        if (!EngineArbiter.IsBackendRunning(backendId)) return;
        throw new InvalidOperationException(
            $"{EngineArbiter.DisplayName(backendId)} 正在运行，已拒绝替换引擎文件。" +
            "请先停止服务，再运行初始化环境或更新。");
    }

    private static void UpdateBuildVersion(string engineDir, string tag)
    {
        var buildPath = Path.Combine(engineDir, "BUILD.json");
        if (!File.Exists(buildPath)) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(buildPath))?.AsObject();
            if (node == null) return;
            node["version"] = tag.TrimStart('v');
            File.WriteAllText(buildPath,
                node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>The outcome of an update check: whether it installed, and which versions were seen.</summary>
    public sealed record EngineUpdateResult(
        bool Updated,
        string? InstalledVersion,
        string? LatestVersion,
        string? EngineDir,
        string? SkippedReason = null);

    /// <summary>
    /// Updates an already-installed engine to the published release when it is behind.
    /// </summary>
    /// <remarks>
    /// <see cref="AcquireAsync"/> downloads unconditionally, but the bootstrap only called it when the
    /// engine was missing, so an installed-but-outdated engine never moved and the doctor's
    /// "运行初始化环境更新" advice did nothing. This compares the installed version with the newest
    /// release that has a usable asset for this machine and reinstalls only when it is behind.
    /// A missing engine is acquired as a repair. An installed engine whose version cannot be read
    /// is never replaced blindly; the result explains why the update was skipped.
    /// </remarks>
    public static async Task<EngineUpdateResult> UpdateAsync(string backendId, ILogSink log, CancellationToken ct)
    {
        var repo = RepoFor(backendId)
            ?? throw new InvalidOperationException($"{backendId} 不支持自动下载。");
        var (exists, installed) = await ReadInstalledVersionAsync(backendId, ct).ConfigureAwait(false);
        if (!exists)
        {
            // Missing engine: install, which is also the repair path.
            var fresh = await AcquireAsync(backendId, log, ct).ConfigureAwait(false);
            return new EngineUpdateResult(true, null, null, fresh);
        }
        if (installed == null)
        {
            var reason = $"{backendId}：已安装，但无法读取 build/版本号；为避免全量覆盖用户安装，已跳过自动更新。";
            log.Write(reason);
            return new EngineUpdateResult(false, null, null, null, reason);
        }

        var releases = await ReleasesFetcher(repo, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"无法获取 {repo} 的发布信息（网络或仓库不可用）。");
        string? latest = null;
        foreach (var node in releases)
        {
            if (node == null) continue;
            var release = EngineCatalog.ParseRelease(node);
            if (PlanFor(backendId, release, ConfigService.GetManagerRoot()) == null) continue;
            latest = release.Tag;
            break;
        }
        if (latest == null)
            throw new InvalidOperationException($"{repo} 的近期发布没有适用于本机的下载文件。");

        if (!DoctorService.IsOutdated(installed, latest))
        {
            log.Write($"{backendId}：已是最新（本机 {installed}，远端 {latest}）。");
            return new EngineUpdateResult(false, installed, latest, null);
        }
        if (EngineArbiter.IsBackendRunning(backendId))
        {
            var reason = $"{EngineArbiter.DisplayName(backendId)} 正在运行；为避免替换正在使用的引擎文件，已跳过更新。请先停止服务。";
            log.Write(reason);
            return new EngineUpdateResult(false, installed, latest, null, reason);
        }

        log.Write($"{backendId}：发现新版本 {latest}（本机 {installed}），开始更新…");
        var engineDir = await AcquireAsync(backendId, log, ct).ConfigureAwait(false);
        return new EngineUpdateResult(true, installed, latest, engineDir);
    }

    /// <summary>Whether the engine exists, and its readable version when available.</summary>
    private static async Task<(bool Exists, string? Version)> ReadInstalledVersionAsync(
        string backendId,
        CancellationToken ct)
    {
        if (backendId == "llamacpp")
        {
            var engine = ConfigService.GetLlamaServerPath();
            return File.Exists(engine)
                ? (true, await DoctorService.ReadLlamaCppVersionAsync(engine, ct).ConfigureAwait(false))
                : (false, null);
        }
        if (backendId == "polystrata")
        {
            var profile = StrataBackend.PolyProfile;
            var engine = StrataBackend.ResolveEngine(ConfigService.ReadConfig(), profile);
            return engine == null
                ? (false, null)
                : (true, await DoctorService.ReadStrataVersionAsync(engine, ct).ConfigureAwait(false));
        }
        return (false, null);
    }

    /// <summary>
    /// Acquires the official cudart zip from llama.cpp releases and unpacks it into <paramref name="destinationDir"/>.
    /// Used by Strata family engines when CUDA DLLs (cublas64_*.dll, cudart64_*.dll) are missing.
    /// </summary>
    public static async Task AcquireCudartToAsync(string destinationDir, ILogSink log, CancellationToken ct,
        string? backendId = null)
    {
        if (backendId != null) EnsureNotRunning(backendId);
        var repo = EngineCatalog.LlamaCppRepo;
        var releases = await ReleasesFetcher(repo, ct).ConfigureAwait(false);
        if (releases == null) return;
        var capability = DetectComputeCapability();
        var cuda = capability > 0 && capability < 8.0 ? "12.4" : "13.4";
        ReleaseAsset? cudart = null;
        foreach (var node in releases)
        {
            if (node == null) continue;
            var release = EngineCatalog.ParseRelease(node);
            cudart = EngineCatalog.Find(release.Assets, $@"^cudart-llama-bin-win-cuda-{System.Text.RegularExpressions.Regex.Escape(cuda)}-x64\.zip$");
            if (cudart != null) break;
        }
        if (cudart == null) return;
        var downloadDir = Path.Combine(ConfigService.GetManagerRoot(), "downloads");
        Directory.CreateDirectory(downloadDir);
        var archive = await FetchAsync(cudart, downloadDir, log, ct).ConfigureAwait(false);
        var staged = Path.Combine(downloadDir, $".cudart-{Guid.NewGuid():N}");
        try
        {
            var extracted = EngineDownloadService.ExtractZip(archive, staged);
            if (backendId != null) EnsureNotRunning(backendId);
            var published = EngineDownloadService.PublishStagedDirectories(
                [(staged, destinationDir)], Path.Combine(staged, "backup"));
            log.Write($"已安装 CUDA 运行时 {published} 个文件到 {destinationDir}（暂存 {extracted} 个）。");
        }
        finally
        {
            try { if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true); } catch { }
        }
    }

    private static async Task<string> FetchAsync(ReleaseAsset asset, string downloadDir, ILogSink log, CancellationToken ct)
    {
        var target = Path.Combine(downloadDir, asset.Name);
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
        log.Write($"已暂存 {count} 个源码文件。");
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

    /// <summary>Writes the installed paths into the backend's settings so the backend can launch.</summary>
    private static void RecordPaths(string backendId, EngineInstallPlan plan, ILogSink log)
    {
        var config = ConfigService.ReadConfig();
        var settings = config.Engines.GetValueOrDefault(backendId) ?? new EngineSettings();
        switch (backendId)
        {
            case "polystrata":
                settings.Parameters["packageRoot"] = plan.SourceDir ?? plan.EngineDir;
                settings.Parameters["serveRoot"] = plan.SourceDir ?? plan.EngineDir;
                break;
        }
        config.Engines[backendId] = settings;
        ConfigService.SaveEngineSettings(config, backendId, settings);
        log.Write($"已记录 {backendId} 的引擎路径。");
    }
}
