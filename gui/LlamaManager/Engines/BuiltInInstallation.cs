using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager.Engines;

/// <summary>
/// Read-only, bounded identity/runtime checks shared by setup, update and doctor. A filename,
/// folder name or arbitrary first line of stderr is never an installed release identity.
/// Newly acquired packages keep release provenance separately; upstream BUILD.json is immutable.
/// </summary>
public static class BuiltInInstallation
{
    public const string ReleaseMetadataName = ".llama-manager-release.json";
    public const string PendingMarker = KvmemInstallation.PendingMarker;
    private const int MaxMetadataBytes = 1 << 20;
    private const int MaxProbeCharacters = 65536;

    public sealed record InstalledDownload(string Name, string Url, string DownloadPath, string Sha256, long Length);
    public sealed record ShippedPackageFile(string RelativePath, string Sha256, long Length);

    public sealed record ReleaseMetadata(
        int SchemaVersion, string Owner, string BackendId, string Repository, string ReleaseTag,
        string Version, string Architecture, string Root, DateTime InstalledUtc,
        string ExecutableRelativePath, string ExecutableSha256,
        IReadOnlyList<InstalledDownload> Downloads)
    {
        public string? CudaVersion { get; init; }
        public string? PythonRelativePath { get; init; }
        public IReadOnlyList<string> RuntimePackages { get; init; } = [];
        public IReadOnlyList<ShippedPackageFile> ShippedLogs { get; init; } = [];
    }

    public sealed record ProbeOutput(int ExitCode, string Output);

    public sealed record ProbeResult(
        bool Exists, bool Available, string? ExecutablePath, string? PackageRoot,
        string? Version, string Detail)
    {
        public bool Variant { get; init; }
        public string? CudaVersion { get; init; }
        public bool HasReleaseMetadata { get; init; }
    }

    public sealed record PolyBuild(string? Version, string? CudaVersion, string? Source,
        string? Backend, IReadOnlyList<int> Architectures);

    /// <summary>Injectable process seam. Production closes stdin and kills only its own probe on cancellation/timeout.</summary>
    public static Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ProbeOutput>> NativeProbe { get; set; } = RunProbeAsync;

    public static bool IsComparableVersion(string backendId, string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 128) return false;
        var text = version.Trim().TrimStart('v', 'V');
        return backendId switch
        {
            "llamacpp" => Regex.IsMatch(text, @"^b\d{1,18}$", RegexOptions.IgnoreCase),
            "polystrata" => Regex.IsMatch(text, @"^\d{1,6}(?:\.\d{1,6}){2,3}-poly\.\d{1,6}(?:[.-][A-Za-z0-9]{1,24})*$", RegexOptions.IgnoreCase),
            "kvmem" => Regex.IsMatch(text, @"^\d{1,6}\.\d{1,6}\.\d{1,6}(?:[-.][A-Za-z0-9]{1,24})*$"),
            _ => false,
        };
    }

    /// <summary>Normalizes both old numeric --version output and modern semver/build output to the actual build tag.</summary>
    public static string? ParseLlamaVersion(string output)
    {
        if (output.Length > MaxProbeCharacters || Regex.IsMatch(output, @"\bprism[- ]b?\d+", RegexOptions.IgnoreCase)) return null;
        foreach (var line in output.Split('\n'))
        {
            var modern = Regex.Match(line, @"^\s*(?:version:\s*)?v?\d{1,6}\.\d{1,6}\.\d{1,6}(?:[-.][A-Za-z0-9]+)*\s*\(\s*build\s*(\d{1,18})(?:\s|[,)]|$)", RegexOptions.IgnoreCase);
            if (modern.Success) return "b" + modern.Groups[1].Value.TrimStart('0').PadLeft(1, '0');
            var old = Regex.Match(line, @"^\s*(?:llama(?:\.cpp|-server)?\s+)?version:\s*b?(\d{1,18})(?:\s|\(|$)", RegexOptions.IgnoreCase);
            if (old.Success) return "b" + old.Groups[1].Value.TrimStart('0').PadLeft(1, '0');
            var tag = Regex.Match(line.Trim(), @"^b(\d{1,18})(?:\s*\([^\r\n]*\))?$", RegexOptions.IgnoreCase);
            if (tag.Success) return "b" + tag.Groups[1].Value.TrimStart('0').PadLeft(1, '0');
        }
        return null;
    }

    public static string? ParsePolyVersion(string text)
    {
        if (text.Length > MaxProbeCharacters) return null;
        var match = Regex.Match(text, @"(?im)^\s*(?:strata-poly\s+|version:\s*)?(v?\d{1,6}(?:\.\d{1,6}){2,3}-poly\.\d{1,6}(?:[.-][A-Za-z0-9]{1,24})*)\s*$");
        return match.Success ? match.Groups[1].Value.TrimStart('v', 'V') : null;
    }

    /// <summary>Bounded JSON reads, including non-string/malformed version values; never rewrites a stamp.</summary>
    public static PolyBuild? ReadPolyBuild(string executable)
    {
        try
        {
            using var document = ReadJson(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "BUILD.json"));
            if (document == null) return null;
            var root = document.RootElement;
            var version = StringProperty(root, "version");
            if (version != null && (version.Length > 128 || !Regex.IsMatch(version, @"^v?\d{1,6}(?:\.\d{1,6}){2,3}(?:-poly\.\d{1,6}(?:[.-][A-Za-z0-9]{1,24})*)?$"))) version = null;
            var cuda = StringProperty(root, "cuda");
            if (cuda != null && !Regex.IsMatch(cuda, @"^\d{1,2}\.\d{1,2}$")) cuda = null;
            var architectures = new List<int>();
            if (root.TryGetProperty("archs", out var archs) && archs.ValueKind == JsonValueKind.Array)
                foreach (var value in archs.EnumerateArray().Take(64))
                    if (value.TryGetInt32(out var arch)) architectures.Add(arch);
            return new PolyBuild(version?.TrimStart('v', 'V'), cuda, StringProperty(root, "source"),
                StringProperty(root, "backend"), architectures);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static ReleaseMetadata? ReadReleaseMetadata(string packageRoot)
    {
        try
        {
            var root = Path.GetFullPath(packageRoot);
            using var document = ReadJson(Path.Combine(root, ReleaseMetadataName));
            if (document == null) return null;
            var metadata = document.RootElement.Deserialize<ReleaseMetadata>();
            if (metadata == null || metadata.SchemaVersion != 1 || metadata.Owner != "LlamaManager" ||
                !PathIdentity.Equals(metadata.Root, root) || !IsComparableVersion(metadata.BackendId, metadata.Version) ||
                EngineCatalog.ParseDigest(metadata.ExecutableSha256) == null || metadata.Downloads == null ||
                metadata.Downloads.Count > 64 || string.IsNullOrWhiteSpace(metadata.ExecutableRelativePath)) return null;
            var executable = Path.GetFullPath(Path.Combine(root, metadata.ExecutableRelativePath));
            if (!executable.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            return metadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    public static void WriteReleaseMetadata(string packageRoot, ReleaseMetadata metadata)
    {
        var root = Path.GetFullPath(packageRoot);
        if (!PathIdentity.Equals(root, metadata.Root)) throw new IOException("安装 provenance 的 root 与最终版本目录不一致。");
        KvmemInstallation.EnsureNoReparsePoints(root);
        var path = Path.Combine(root, ReleaseMetadataName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, metadata, new JsonSerializerOptions { WriteIndented = true });
        stream.Flush(flushToDisk: true);
    }

    public static async Task<ProbeResult> ProbeAsync(string backendId, ManagerConfig config, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (backendId == "kvmem")
        {
            var bundle = KvmemInstallation.ResolvePackageRoot(config);
            if (bundle == null) return new(false, false, null, null, null, "未找到完整 KVMem unified 版本目录。");
            var executable = Path.Combine(bundle, KvmemInstallation.WorkerRelativePath);
            var version = KvmemInstallation.ReadVersion(bundle);
            var capability = await EngineAcquisitionService.DetectComputeCapabilityAsync(ct).ConfigureAwait(false);
            var architecture = EngineCatalog.KvmemArchitecture(capability);
            if (architecture == null) return new(File.Exists(executable), false, executable, bundle, version,
                "无法确定受支持的精确 KVMem GPU 架构；拒绝跨架构修复/更新。");
            var layout = KvmemInstallation.Validate(bundle, architecture);
            if (!layout.Ok) return new(File.Exists(executable), false, executable, bundle, version, layout.Detail);
            var runtime = await KvmemInstallation.ValidateRuntimeAsync(bundle, ct).ConfigureAwait(false);
            return new(true, runtime.Ok, executable, bundle, runtime.Version, runtime.Detail);
        }
        if (backendId == "llamacpp") return await ProbeLlamaAsync(ConfigService.GetLlamaServerPathFor(config), ct).ConfigureAwait(false);
        if (backendId != "polystrata") return new(false, false, null, null, null, "此后端没有内置发布获取协议。");
        var package = Parameter(config, backendId, "packageRoot") ?? config.Strata.PackageRoot;
        var engine = StrataBackend.ResolveEngine(config, StrataBackend.PolyProfile);
        if (engine == null) return new(false, false, null, package, null, "未找到 strata-poly.exe；尚未验证原生版本。");
        return await ProbePolyAsync(engine, package!, config, ct).ConfigureAwait(false);
    }

    public static async Task<ProbeResult> ProbeLlamaAsync(string executable, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(executable)) return new(false, false, executable, Path.GetDirectoryName(executable), null, "未找到 llama-server.exe。");
        try
        {
            var probe = await NativeProbe(CreateProbeStart(executable, "--version"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var variant = Regex.IsMatch(probe.Output, @"\bprism[- ]b?\d+", RegexOptions.IgnoreCase);
            var version = probe.ExitCode == 0 ? ParseLlamaVersion(probe.Output) : null;
            var root = Path.GetDirectoryName(Path.GetFullPath(executable))!;
            var metadata = ReadReleaseMetadata(root);
            var libraries = metadata?.BackendId == "llamacpp" && metadata.CudaVersion != null
                ? ValidateCudaLibraries(root, metadata.CudaVersion) : null;
            return new(true, probe.ExitCode == 0 && version != null && libraries == null, executable, root, version,
                variant ? "当前 llama-server 是 Prism 变体，不得用官方 llama.cpp 发布自动替换。" :
                version == null ? "无法读取可信 llama.cpp build；拒绝盲目重装。" + ProbeDetail(probe) :
                libraries ?? $"原生 --version 已验证 {version}：{probe.Output.Trim()}")
            { Variant = variant, CudaVersion = metadata?.CudaVersion, HasReleaseMetadata = metadata != null };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return new(true, false, executable, Path.GetDirectoryName(executable), null, "llama.cpp 原生版本/运行时探测失败，未更改安装：" + ex.Message);
        }
    }

    public static async Task<string?> ReadPolyVersionAsync(string executable, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(executable)) return null;
        var engineDir = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var package = Path.GetDirectoryName(engineDir)!;
        var metadata = ReadReleaseMetadata(package);
        if (metadata?.BackendId == "polystrata" &&
            PathIdentity.Equals(Path.Combine(package, metadata.ExecutableRelativePath), executable))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var digest = await EngineDownloadService.Sha256Async(executable, deadline.Token).ConfigureAwait(false);
                return digest.Equals(metadata.ExecutableSha256, StringComparison.OrdinalIgnoreCase) ? metadata.Version : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        }
        var build = ReadPolyBuild(executable);
        if (build?.Version != null) return build.Version;
        try
        {
            var probe = await NativeProbe(CreateProbeStart(executable, "--version"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return probe.ExitCode == 0 ? ParsePolyVersion(probe.Output) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException) { return null; }
    }

    private static async Task<ProbeResult> ProbePolyAsync(string executable, string package, ManagerConfig config, CancellationToken ct)
    {
        var version = await ReadPolyVersionAsync(executable, ct).ConfigureAwait(false);
        var build = ReadPolyBuild(executable);
        var metadata = ReadReleaseMetadata(package);
        var variant = build?.Backend is not (null or "cuda") || build?.Source == "local";
        try
        {
            var native = await NativeProbe(CreateProbeStart(executable, "--help"), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            variant |= native.Output.Contains("--kvmem-budget", StringComparison.Ordinal);
            var ready = (native.ExitCode is 0 or 2) && native.Output.Contains("usage: strata-poly", StringComparison.OrdinalIgnoreCase) &&
                native.Output.Contains("--max-context", StringComparison.Ordinal);
            var detail = variant ? "当前 PolyStrata 为手工构建/其它后端/KVMem 变体；不以官方 CUDA 发布自动替换。" :
                !ready ? "PolyStrata 原生 --help/运行库验证失败。" + ProbeDetail(native) :
                $"原生 strata-poly --help 已验证；本机版本 {version ?? "未知"}。";
            if (variant) return new(true, ready, executable, package, version, detail) { Variant = true, CudaVersion = build?.CudaVersion };
            if (ready && metadata?.CudaVersion != null)
            {
                var missing = ValidateCudaLibraries(Path.GetDirectoryName(executable)!, metadata.CudaVersion);
                if (missing != null) { ready = false; detail += " " + missing; }
            }
            var server = StrataBackend.ResolveServer(config, StrataBackend.PolyProfile);
            if (server == null) { ready = false; detail += " 缺少同版本 serve/server.py。"; }
            if (ready && server != null)
            {
                var python = StrataBackend.ResolvePython(config, StrataBackend.PolyProfile);
                if (python == null) { ready = false; detail += " 缺少 Python 3.10+。"; }
                else
                {
                    var sourceRoot = Path.GetDirectoryName(Path.GetDirectoryName(server))!;
                    var pythonCheck = await ValidatePythonAsync(python, sourceRoot, ct).ConfigureAwait(false);
                    if (pythonCheck != null) { ready = false; detail += " " + pythonCheck; }
                }
            }
            if (!IsComparableVersion("polystrata", version))
                detail += " BUILD 只有基础版本或版本未知，不能确定 poly 发布修订；请明确安装一个可校验的官方版本，不会盲目覆盖。";
            return new(true, ready, executable, package, version, detail)
            { CudaVersion = build?.CudaVersion, HasReleaseMetadata = metadata != null };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return new(true, false, executable, package, version, "PolyStrata 原生/运行时探测失败，安装路径未更改：" + ex.Message)
            { Variant = variant, CudaVersion = build?.CudaVersion };
        }
    }

    /// <summary>Must run against the final relocated private interpreter; imports do not write pyc or load a model.</summary>
    public static async Task<string?> ValidatePythonAsync(string python, string sourceRoot, CancellationToken ct = default)
    {
        try
        {
            var start = CreateProbeStart(python, "-B", "-s", "-c",
                "import sys; assert sys.version_info >= (3,10); import numpy, regex, jinja2, psutil, yaml, requests, tqdm; from PIL import Image; import serve.server; print('PolyStrata imports OK')");
            start.WorkingDirectory = sourceRoot;
            start.Environment["PYTHONPATH"] = sourceRoot;
            var probe = await NativeProbe(start, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            return probe.ExitCode == 0 && probe.Output.Contains("PolyStrata imports OK", StringComparison.Ordinal)
                ? null : "Python 服务依赖/imports 不可用。" + ProbeDetail(probe);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return "Python imports 检查失败：" + ex.Message;
        }
    }

    /// <summary>strata-vision has no --help flag; no arguments print usage before any model/CUDA initialization.</summary>
    public static async Task<string?> ValidateVisionAsync(string executable, CancellationToken ct = default)
    {
        var probe = await NativeProbe(CreateProbeStart(executable), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return probe.ExitCode is 0 or 2 && probe.Output.Contains("usage: strata-vision --mmproj", StringComparison.OrdinalIgnoreCase) &&
            probe.Output.Contains("--model", StringComparison.Ordinal)
            ? null : "视觉组件的无参数用法/运行库检查失败（未加载模型）：" + ProbeDetail(probe);
    }

    /// <summary>Only reads a literal list of NVIDIA's exact-version wheel pins; never executes downloaded setup.py.</summary>
    public static IReadOnlyList<string> ParseCudaWheelPins(string source, string cudaVersion)
    {
        if (source.Length > 2 * MaxMetadataBytes || !Version.TryParse(cudaVersion, out var cuda)) return [];
        var variable = cuda.Major == 12 ? "CUDA12_WHEELS" : "CUDA_WHEELS";
        var list = Regex.Match(source, @"(?m)^" + variable + @"\s*=\s*\[([^\]\r\n]{1,1024})\]\s*(?:#.*)?$");
        if (!list.Success) return [];
        var pins = new List<string>();
        foreach (var token in list.Groups[1].Value.Split(','))
        {
            var match = Regex.Match(token.Trim(), "^[\"'](nvidia-(?:cublas|cuda-runtime)(?:-cu12)?==[0-9]+(?:\\.[0-9]+){1,4})[\"']$");
            if (!match.Success) return [];
            var pin = match.Groups[1].Value;
            if (!pin.Split("==")[1].StartsWith(cudaVersion + ".", StringComparison.Ordinal)) return [];
            pins.Add(pin);
        }
        return pins.Count == 2 && pins.Any(p => p.StartsWith("nvidia-cublas", StringComparison.Ordinal)) &&
            pins.Any(p => p.StartsWith("nvidia-cuda-runtime", StringComparison.Ordinal)) ? pins : [];
    }

    public static string? ValidateCudaLibraries(string engineDirectory, string cudaVersion)
    {
        if (!Version.TryParse(cudaVersion, out var cuda)) return "无法确定引擎发布的 CUDA 运行库版本。";
        var required = new[] { $"cublas64_{cuda.Major}.dll", $"cublasLt64_{cuda.Major}.dll", $"cudart64_{cuda.Major}.dll" };
        foreach (var name in required)
        {
            var path = Path.Combine(engineDirectory, name);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return $"缺少同发布 CUDA {cudaVersion} 运行库：{name}（不会从其它引擎/未知版本目录混拷）。";
            KvmemInstallation.EnsureNoReparsePoints(path);
        }
        return null;
    }

    public static ProcessStartInfo CreateProbeStart(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["CUDA_VISIBLE_DEVICES"] = "-1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PYTHONNOUSERSITE"] = "1";
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment.Remove("PYTHONHOME");
        start.Environment.Remove("PYTHONPATH");
        return start;
    }

    private static async Task<ProbeOutput> RunProbeAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var process = Process.Start(start) ?? throw new IOException("无法启动内置引擎验证进程。");
        process.StandardInput.Close();
        var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
            if (output.Length > MaxProbeCharacters) throw new IOException("版本/运行时探测输出超出 64 KiB 上限。");
            return new(process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{Path.GetFileName(start.FileName)} 探测超时（{timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} 秒），仅结束本次检查进程。");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or InvalidOperationException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) return result.ToString();
            if (result.Length + read > MaxProbeCharacters / 2) throw new IOException("版本/运行时探测输出异常过大。");
            result.Append(buffer, 0, read);
        }
    }

    private static JsonDocument? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        KvmemInstallation.EnsureNoReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length is <= 0 or > MaxMetadataBytes) return null;
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static string? Parameter(ManagerConfig config, string backendId, string name) =>
        config.Engines.TryGetValue(backendId, out var settings) && settings.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : null;

    private static string ProbeDetail(ProbeOutput probe)
    {
        var output = probe.Output.Trim();
        if (output.Length > 2000) output = output[^2000..];
        return $"（退出码 {probe.ExitCode} / 0x{unchecked((uint)probe.ExitCode):X8}）{output}";
    }
}
