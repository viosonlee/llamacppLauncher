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

        var downloadDir = Path.Combine(ConfigService.GetManagerRoot(), "downloads");
        Directory.CreateDirectory(downloadDir);

        // Engine archives first: these carry the executable the backend probes for.
        foreach (var asset in plan.EngineArchives)
        {
            var archive = await FetchAsync(asset, downloadDir, log, ct).ConfigureAwait(false);
            var extracted = EngineDownloadService.ExtractZip(archive, plan.EngineDir);
            log.Write($"已解压 {extracted} 个文件到 {plan.EngineDir}。");
        }

        // Then the source archive (serve/server.py + requirements.txt), flattened to the root.
        if (plan.SourceArchive != null && plan.SourceDir != null)
        {
            var archive = await FetchAsync(plan.SourceArchive, downloadDir, log, ct).ConfigureAwait(false);
            ExtractFlattened(archive, plan.SourceDir, log);
        }

        RecordPaths(backendId, plan, log);
        return plan.EngineDir;
    }

    /// <summary>The outcome of an update check: whether it installed, and which versions were seen.</summary>
    public sealed record EngineUpdateResult(
        bool Updated, string? InstalledVersion, string? LatestVersion, string? EngineDir);

    /// <summary>
    /// Updates an already-installed engine to the published release when it is behind.
    /// </summary>
    /// <remarks>
    /// <see cref="AcquireAsync"/> downloads unconditionally, but the bootstrap only called it when the
    /// engine was missing, so an installed-but-outdated engine never moved and the doctor's
    /// "运行初始化环境更新" advice did nothing. This compares the installed version with the newest
    /// release that has a usable asset for this machine and reinstalls only when it is behind.
    /// Engines whose version cannot be read (llama.cpp nightlies) are left to the caller.
    /// </remarks>
    public static async Task<EngineUpdateResult> UpdateAsync(string backendId, ILogSink log, CancellationToken ct)
    {
        var repo = RepoFor(backendId)
            ?? throw new InvalidOperationException($"{backendId} 不支持自动下载。");
        var installed = await InstalledVersionAsync(backendId, ct).ConfigureAwait(false);
        if (installed == null)
        {
            // Nothing readable to compare against: install, which is also the repair path.
            var fresh = await AcquireAsync(backendId, log, ct).ConfigureAwait(false);
            return new EngineUpdateResult(true, null, null, fresh);
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

        log.Write($"{backendId}：发现新版本 {latest}（本机 {installed}），开始更新…");
        var engineDir = await AcquireAsync(backendId, log, ct).ConfigureAwait(false);
        return new EngineUpdateResult(true, installed, latest, engineDir);
    }

    /// <summary>The version of an installed engine, or null when this backend has no readable one.</summary>
    private static async Task<string?> InstalledVersionAsync(string backendId, CancellationToken ct)
    {
        if (backendId != "polystrata") return null;
        var profile = StrataBackend.PolyProfile;
        var engine = StrataBackend.ResolveEngine(ConfigService.ReadConfig(), profile);
        return engine == null ? null : await DoctorService.ReadStrataVersionAsync(engine, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Acquires the official cudart zip from llama.cpp releases and unpacks it into <paramref name="destinationDir"/>.
    /// Used by Strata family engines when CUDA DLLs (cublas64_*.dll, cudart64_*.dll) are missing.
    /// </summary>
    public static async Task AcquireCudartToAsync(string destinationDir, ILogSink log, CancellationToken ct)
    {
        var repo = EngineCatalog.LlamaCppRepo;
        var releases = await ReleasesFetcher(repo, ct).ConfigureAwait(false);
        if (releases == null) return;
        var cuda = DetectComputeCapability() > 0 && DetectComputeCapability() < 8.0 ? "12.4" : "13.4";
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
        var extracted = EngineDownloadService.ExtractZip(archive, destinationDir);
        log.Write($"已解压 CUDA 运行时 {extracted} 个文件到 {destinationDir}。");
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
