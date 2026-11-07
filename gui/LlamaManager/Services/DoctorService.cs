using System.IO;
using System.Text.RegularExpressions;
using LlamaManager.Engines;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>
/// Read-only health/update report. Built-ins share the actual identity and compatible-release
/// probes used by installation; an unknown version is not silently called "latest" or replaced.
/// </summary>
public static class DoctorService
{
    public sealed record Finding(string Engine, string Status, string Detail, string? Action = null);

    public sealed record Report(IReadOnlyList<Finding> Findings, string Summary)
    {
        public bool Healthy => Findings.All(f => f.Status is "ok" or "manual");
    }

    /// <summary>Remote version lookup seam; the production selector requires the same complete compatible assets as update.</summary>
    public static Func<string, CancellationToken, Task<ReleaseInfo?>> LatestRelease { get; set; } = (repo, ct) =>
    {
        var backendId = repo switch
        {
            EngineCatalog.LlamaCppRepo => "llamacpp",
            EngineCatalog.PolyStrataRepo => "polystrata",
            EngineCatalog.KvmemRepo => "kvmem",
            _ => "",
        };
        return backendId.Length == 0 ? Task.FromResult<ReleaseInfo?>(null) : EngineAcquisitionService.LatestCompatibleReleaseAsync(backendId, ct);
    };

    public static async Task<Report> RunAsync(bool checkUpdates, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var findings = new List<Finding>();
        foreach (var backend in BackendRegistry.All().Where(b => b.Descriptor.BuiltIn && EngineAcquisitionService.CanAcquire(b.Descriptor.Id)))
        {
            ct.ThrowIfCancellationRequested();
            var id = backend.Descriptor.Id;
            if (id == "kvmem" && !KvmemInstallation.IsInstalledOrConfigured(ConfigService.ReadConfig()))
            {
                findings.Add(new Finding(id, "manual", "可选 KVMem unified 尚未安装；不影响已有 llama.cpp / PolyStrata。",
                    "需要时 --bootstrap --backend kvmem；明确 --update --all 会处理全部注册内置引擎。"));
                continue;
            }
            BuiltInInstallation.ProbeResult probe;
            try { probe = await EngineAcquisitionService.ProbeInstalledAsync(id, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or TimeoutException)
            {
                findings.Add(new Finding(id, "unavailable", "本机只读版本/运行时检查失败：" + ex.Message,
                    "修复路径/权限/匹配运行库后重试；本次没有更改配置或引擎。"));
                continue;
            }
            if (!probe.Exists)
            {
                findings.Add(new Finding(id, "missing", probe.Detail, "运行初始化环境，或 --bootstrap --backend " + id + " 安装完整引擎及匹配运行库。"));
                continue;
            }
            if (probe.Variant)
            {
                findings.Add(new Finding(id, "manual", probe.Detail, "保留手工/可选变体；若需要官方内置引擎，请明确选择官方版本目录。"));
                continue;
            }
            findings.Add(new Finding(id, probe.Available ? "ok" : "unavailable",
                probe.Detail + (probe.ExecutablePath == null ? "" : " " + probe.ExecutablePath),
                probe.Available ? null : "修复已知版本的匹配运行库/源码；更新只安装到新版本目录，不覆盖运行中的 exe。"));
            if (!checkUpdates) continue;
            if (!BuiltInInstallation.IsComparableVersion(id, probe.Version))
            {
                findings.Add(new Finding(id, "manual", $"本机实际发布版本无法可靠比较（读到 {probe.Version ?? "未知"}）；未宣称已最新，也不会盲目覆盖。",
                    "请明确提供可校验的官方发布目录；PolyStrata 仅基础 BUILD 版本不能确定 poly 修订。"));
                continue;
            }
            try
            {
                var latest = await LatestRelease(EngineAcquisitionService.RepoFor(id)!, ct).ConfigureAwait(false);
                if (latest == null || !BuiltInInstallation.IsComparableVersion(id, latest.Tag))
                    findings.Add(new Finding(id, "manual", $"没有可比较、带匹配运行库的远端发布；本机 {probe.Version} 保留。"));
                else if (IsOutdated(probe.Version!, latest.Tag))
                    findings.Add(new Finding(id, "outdated", $"本机 {probe.Version}，最新本机适用发布 {latest.Tag}。",
                        "点击更新全部内置引擎，或 --update --backend " + id + "；新版本验证后原子切换路径。"));
                else
                    findings.Add(new Finding(id, "ok", $"版本已比较：本机 {probe.Version}，远端适用 {latest.Tag}；不重复下载、不降级。"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or TimeoutException)
            {
                findings.Add(new Finding(id, "manual", $"无法查询更新（{ex.Message}）；本机 {probe.Version} 及安装路径保持不变。"));
            }
        }
        findings.Add(await PrismFindingAsync(ct).ConfigureAwait(false));
        findings.Add(PythonFinding());
        var problems = findings.Count(f => f.Status is "missing" or "unavailable");
        var outdated = findings.Count(f => f.Status == "outdated");
        var manual = findings.Count(f => f.Status == "manual");
        var summary = problems == 0 && outdated == 0
            ? manual == 0 ? "一切正常。" : $"未发现已安装引擎的缺失或可确认更新；另有 {manual} 项可选/需手动检查。"
            : $"{problems} 项缺失/不可用，{outdated} 项有更新，{manual} 项可选/需手动检查。";
        return new Report(findings, summary);
    }

    private static async Task<Finding> PrismFindingAsync(CancellationToken ct)
    {
        var path = ConfigService.GetPrismServerPath();
        if (!ConfigService.HasPrismServer())
        {
            var config = ConfigService.ReadConfig();
            var required = ConfigService.IsPrismModel(config.ModelPath) || ConfigService.IsPrismModel(config.EmbeddingModelPath);
            return new Finding("prism", required ? "missing" : "manual",
                $"可选 Prism 变体未安装（{path}）；仅三元量化模型需要，不属于 registry 内置更新列表。",
                "若需要，请从 https://github.com/PrismML-Eng/llama.cpp/releases 选择经过确认的匹配发布；没有可靠资产时不编造一键支持。");
        }
        string? version = null;
        try
        {
            var output = await BuiltInInstallation.NativeProbe(BuiltInInstallation.CreateProbeStart(path, "--version"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var match = Regex.Match(output.Output, @"prism-b\d+[0-9a-z-]*", RegexOptions.IgnoreCase);
            if (output.ExitCode == 0 && match.Success) version = match.Value;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException) { }
        return new Finding("prism", "manual", version == null ? path + "（变体版本未确认）" : path + "（" + version + "）",
            "可选变体手动更新；官方 llama.cpp 的安装/更新不会覆盖此目录。");
    }

    private static Finding PythonFinding()
    {
        var config = ConfigService.ReadConfig();
        var python = StrataBackend.ResolvePython(config, StrataBackend.PolyProfile);
        var configured = config.Engines.GetValueOrDefault("polystrata")?.Parameters.GetValueOrDefault("pythonPath");
        var privateReady = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured);
        var legacyReady = File.Exists(Path.Combine(ConfigService.GetManagerRoot(), "venvs", "polystrata", "Scripts", "python.exe"));
        return DescribePython(python, privateReady || legacyReady ? 1 : 0);
    }

    public static Finding DescribePython(string? python, int venvs)
    {
        if (python == null) return new Finding("python", "missing", "未找到 Python 3.10+；PolyStrata 的 HTTP 服务需要它。",
            "初始化环境会准备解释器与发布私有 venv；不升级/覆盖正在服务的旧 venv，KVMem 使用包内运行时。");
        return new Finding("python", "ok", $"{python}（{venvs}/1 个 PolyStrata 解释器/venv 已记录；实际 imports 由引擎体检检查）");
    }

    /// <summary>Actual BUILD/version or hash-verified release provenance, never arbitrary stderr or a rewritten stamp.</summary>
    public static Task<string?> ReadStrataVersionAsync(string engineExe, CancellationToken ct) =>
        BuiltInInstallation.ReadPolyVersionAsync(engineExe, ct);

    /// <summary>
    /// Natural ordering retained for legacy mixed versions. Numeric runs are compared as strings
    /// after trimming zeros, so untrusted metadata cannot overflow long.Parse. Callers must first
    /// verify a backend-compatible scheme; incomparable values must not be labelled "latest".
    /// </summary>
    public static bool IsOutdated(string local, string remote)
    {
        var a = Normalize(local);
        var b = Normalize(remote);
        if (a.Length == 0 || b.Length == 0 || a.Length > 256 || b.Length > 256) return false;
        var i = 0;
        var j = 0;
        while (i < a.Length && j < b.Length)
        {
            var digitA = char.IsDigit(a[i]);
            var digitB = char.IsDigit(b[j]);
            if (digitA != digitB) return false;
            if (digitA)
            {
                var endA = i; while (endA < a.Length && char.IsDigit(a[endA])) endA++;
                var endB = j; while (endB < b.Length && char.IsDigit(b[endB])) endB++;
                var na = a[i..endA].TrimStart('0');
                var nb = b[j..endB].TrimStart('0');
                if (na.Length != nb.Length) return na.Length < nb.Length;
                var comparison = string.CompareOrdinal(na, nb);
                if (comparison != 0) return comparison < 0;
                i = endA; j = endB;
            }
            else
            {
                if (a[i] != b[j]) return a[i] < b[j];
                i++; j++;
            }
        }
        if (i == a.Length && j < b.Length)
        {
            var remainder = b[j..];
            if (remainder.StartsWith("-poly.", StringComparison.Ordinal) || remainder.StartsWith('.')) return true;
        }
        return false;
    }

    private static string Normalize(string version) => version.Trim().ToLowerInvariant().TrimStart('v');
}
