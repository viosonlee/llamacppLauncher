using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using LlamaManager.Engines;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>
/// One-click health report ("doctor"): for every registered engine, whether it is installed,
/// whether a newer engine is published, and what to do about it. Read-only - it never downloads
/// or changes anything, so it is safe to run at any time.
/// </summary>
public static class DoctorService
{
    public sealed record Finding(
        string Engine,
        string Status,          // "ok" | "missing" | "outdated" | "unavailable" | "manual"
        string Detail,
        string? Action = null);

    public sealed record Report(IReadOnlyList<Finding> Findings, string Summary)
    {
        public bool Healthy => Findings.All(f => f.Status is "ok" or "manual");
    }

    /// <summary>Remote version lookup seam for tests.</summary>
    public static Func<string, CancellationToken, Task<ReleaseInfo?>> LatestRelease { get; set; } =
        async (repo, ct) =>
        {
            var backendId = repo switch
            {
                EngineCatalog.LlamaCppRepo => "llamacpp",
                EngineCatalog.PolyStrataRepo => "polystrata",
                _ => "",
            };
            var releases = await EngineAcquisitionService.ReleasesFetcher(repo, ct).ConfigureAwait(false);
            if (releases == null) return null;
            foreach (var node in releases)
            {
                if (node == null) continue;
                var release = EngineCatalog.ParseRelease(node);
                if (backendId.Length > 0 &&
                    EngineAcquisitionService.PlanFor(backendId, release, ConfigService.GetManagerRoot()) != null)
                    return release;
            }
            return null;
        };

    /// <summary>Runs every check. Network failures degrade that engine to "unavailable", not a crash.</summary>
    public static async Task<Report> RunAsync(bool checkUpdates, CancellationToken ct)
    {
        var findings = new List<Finding>();

        // ---- llama.cpp ----
        var llamaExe = ConfigService.GetLlamaServerPath();
        findings.Add(File.Exists(llamaExe)
            ? new Finding("llamacpp", "ok", llamaExe)
            : new Finding("llamacpp", "missing", "未找到 llama-server.exe。",
                EngineAcquisitionService.CanAcquire("llamacpp") ? "运行初始化环境自动下载。" : null));

        // ---- Prism (ternary-quantized llama.cpp) ----
        findings.Add(PrismFinding());

        // ---- Strata family ----
        foreach (var profile in new[] { StrataBackend.PolyProfile })
        {
            var config = ConfigService.ReadConfig();
            var engine = StrataBackend.ResolveEngine(config, profile);
            var server = StrataBackend.ResolveServer(config, profile);
            if (engine == null)
            {
                findings.Add(new Finding(profile.Id, "missing", $"未找到 {profile.EngineExeName}。",
                    BootstrapService.CanDownload(profile.Id) ? "运行初始化环境自动下载。" : null));
                continue;
            }
            var status = await BackendRegistry.ServerById(profile.Id)!.ProbeAsync(ct).ConfigureAwait(false);
            if (!status.Available)
            {
                findings.Add(new Finding(profile.Id, "unavailable", status.Problem ?? "探测失败。"));
                continue;
            }
            findings.Add(new Finding(profile.Id, "ok", engine));

            var version = await ReadStrataVersionAsync(engine, ct).ConfigureAwait(false);
            if (checkUpdates && version != null)
            {
                try
                {
                    var latest = await LatestRelease(EngineAcquisitionService.RepoFor(profile.Id)!, ct).ConfigureAwait(false);
                    if (latest != null && IsOutdated(version, latest.Tag))
                        findings.Add(new Finding(profile.Id, "outdated",
                            $"本机 {version}，远端 {latest.Tag}。", "运行初始化环境更新。"));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or InvalidOperationException or TimeoutException)
                {
                    // A network failure must not take the whole report down (REQ-040).
                    findings.Add(new Finding(profile.Id, "manual", $"无法查询更新（{ex.Message}）；本机 {version}。"));
                }
            }
        }

        // ---- Python for the Strata family ----
        findings.Add(PythonFinding());

        var problems = findings.Count(f => f.Status is "missing" or "unavailable");
        var outdated = findings.Count(f => f.Status == "outdated");
        var summary = problems == 0 && outdated == 0
            ? "一切正常。"
            : $"{problems} 项缺失/不可用，{outdated} 项有更新。";
        return new Report(findings, summary);
    }

    private static Finding PrismFinding()
    {
        var path = ConfigService.GetPrismServerPath();
        if (!ConfigService.HasPrismServer())
            return new Finding("prism", "missing",
                $"未找到 Prism 引擎（{path}）。三元量化模型（如 Bonsai TQ1_0）需要它。",
                "把 Prism 发行包解压到 llama.cpp\\engines\\prism\\（无公开下载源，需手动安装）。");
        var version = PrismVersion(path);
        return new Finding("prism", "manual",
            version != null ? $"{path}（{version}）" : path,
            "更新需手动替换发行包（无公开下载源）。");
    }

    private static Finding PythonFinding()
    {
        var config = ConfigService.ReadConfig();
        var python = StrataBackend.ResolvePython(config, StrataBackend.PolyProfile);
        var venvs = new[] { "polystrata" }
            .Count(id => File.Exists(Path.Combine(ConfigService.GetManagerRoot(), "venvs", id, "Scripts", "python.exe")));
        return DescribePython(python, venvs);
    }

    /// <summary>The Python finding, given the resolved interpreter (null when absent) and the venv count.</summary>
    public static Finding DescribePython(string? python, int venvs)
    {
        if (python == null)
            return new Finding("python", "missing",
                "未找到 Python 3.10+；Strata 家族的 HTTP 服务需要它。",
                "点「初始化环境」即会自动安装（winget 优先，回退 python.org）；也可手动安装 Windows x64 Python 3.10+。");
        return new Finding("python", "ok", $"{python}（{venvs}/1 个引擎 venv 已就绪）");
    }

    /// <summary>Prism builds report "prism-b10743-adfffbe" style versions on stderr; the launcher's llama.cpp does not.</summary>
    private static string? PrismVersion(string exe)
    {
        try
        {
            var output = ProcessProbe.RunAsync(exe, "--version", TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return Regex.Match(output, @"prism-b\d+[0-9a-z-]*", RegexOptions.IgnoreCase) is { Success: true } m
                ? m.Value : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Strata-family engines report their version through BUILD.json (engine zip) or --version.</summary>
    public static async Task<string?> ReadStrataVersionAsync(string engineExe, CancellationToken ct)
    {
        var build = Path.Combine(Path.GetDirectoryName(engineExe)!, "BUILD.json");
        if (File.Exists(build))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(build));
                return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        }
        try
        {
            var output = await ProcessProbe.RunAsync(engineExe, "--version", TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return output.Trim().Split('\n')[0].Trim() is { Length: > 0 } line ? line : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Version ordering across the mixed schemes in play: "b11671" (llama.cpp),
    /// "0.1.44.1-apex.1" (Strata fork), "v0.1.44.1-poly.5" (PolyStrata). Versions are compared
    /// as alternating runs of digits and other characters - digits numerically, the rest
    /// lexically - so "b9" &lt; "b11671" and "1.2" &lt; "1.10". Schemes that do not line up
    /// (a digit run meeting a letter run) are never reported as updates.
    /// </summary>
    public static bool IsOutdated(string local, string remote)
    {
        var a = Normalize(local);
        var b = Normalize(remote);
        if (a.Length == 0 || b.Length == 0) return false;
        var i = 0;
        var j = 0;
        while (i < a.Length && j < b.Length)
        {
            var digitA = char.IsDigit(a[i]);
            var digitB = char.IsDigit(b[j]);
            if (digitA != digitB) return false; // schemes diverge: incomparable
            if (digitA)
            {
                var endA = i; while (endA < a.Length && char.IsDigit(a[endA])) endA++;
                var endB = j; while (endB < b.Length && char.IsDigit(b[endB])) endB++;
                var na = long.Parse(a[i..endA]);
                var nb = long.Parse(b[j..endB]);
                if (na != nb) return na < nb;
                i = endA; j = endB;
            }
            else
            {
                if (a[i] != b[j]) return a[i] < b[j];
                i++; j++;
            }
        }
        return false; // one side is a prefix of the other ("1.2" vs "1.2.1"): not clearly older
    }

    /// <summary>Lower-cases and strips a leading "v" so "v0.1.44.1-poly.5" compares with "0.1.44.1-poly.5".</summary>
    private static string Normalize(string version) =>
        version.Trim().ToLowerInvariant().TrimStart('v');
}
