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
/// The official unified KVMem package is a complete, architecture-specific installation, not a
/// replacement strata-poly.exe. These paths deliberately never resolve through the legacy
/// PolyStrata settings, a global Python installation, or a launcher-created venv.
/// </summary>
public static class KvmemInstallation
{
    public const string BackendId = "kvmem";
    public const string WorkerRelativePath = "workers/polystrata/strata-poly.exe";
    public const string ServeRootRelativePath = "backends/polystrata";
    public const string ServerRelativePath = "backends/polystrata/serve/server.py";
    public const string PythonRelativePath = "runtime/python/python.exe";
    public const string CudaRelativePath = "runtime/cuda";
    public const string VersionRelativePath = "console-build.json";
    public const string ArchitectureRelativePath = "provenance/architecture-build.json";
    public const string PinnedVersion = "v0.19.1";
    public const string PendingMarker = ".llama-manager-pending";
    public const string PinnedRtx30Sha256 = "9dbfdcf1e893cd60ba05a7f8b5a60522a472c1a2a5772bcbcfcf6a2650c0ea9c";

    /// <summary>Required launch assets; extraction still preserves every file in the unified zip.</summary>
    public static IReadOnlyList<string> RequiredRelativeFiles { get; } =
    [
        WorkerRelativePath, ServerRelativePath, PythonRelativePath, VersionRelativePath,
        ArchitectureRelativePath, "workers/ninfer/ninfer-kvmem-server.exe",
        "backends/polystrata/tools/gguf_reader.py", "backends/polystrata/tools/strata_tokenizer.py",
        "tools/polystrata/assets.py", "tools/polystrata/server.py",
    ];

    public sealed record ValidationResult(bool Ok, string Detail, string? Version, int? Architecture);

    /// <summary>
    /// Finds a complete installed version without a subprocess or network request. With multiple
    /// GPU architectures installed, an explicit packageRoot is required rather than guessing one.
    /// Staging directories are excluded, so failed/cancelled extraction cannot become the default.
    /// </summary>
    public static string? DefaultPackageRoot(string? managerRoot = null)
    {
        var root = EngineCatalog.EngineRoot(managerRoot ?? ConfigService.GetManagerRoot(), BackendId);
        if (!Directory.Exists(root)) return null;
        try
        {
            var directories = Directory.EnumerateDirectories(root, "v*", SearchOption.TopDirectoryOnly)
                .Take(65).ToArray();
            if (directories.Length > 64) return null; // bounded discovery; never pick from a truncated set
            var candidates = new List<(string Root, ValidationResult Validation, DateTime Modified)>();
            foreach (var directory in directories)
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                    File.Exists(Path.Combine(directory, PendingMarker))) continue;
                var validation = Validate(directory);
                if (validation.Ok)
                    candidates.Add((directory, validation, Directory.GetLastWriteTimeUtc(directory)));
            }
            if (candidates.Select(c => c.Validation.Architecture).Distinct().Count() != 1) return null;
            (string Root, ValidationResult Validation, DateTime Modified)? newest = null;
            foreach (var candidate in candidates)
            {
                if (newest == null || DoctorService.IsOutdated(newest.Value.Validation.Version!, candidate.Validation.Version!) ||
                    (string.Equals(newest.Value.Validation.Version, candidate.Validation.Version, StringComparison.OrdinalIgnoreCase) &&
                     candidate.Modified > newest.Value.Modified))
                    newest = candidate;
            }
            return newest?.Root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Only this backend's explicit root, or the complete versioned KVMem installation.</summary>
    public static string? ResolvePackageRoot(ManagerConfig config)
    {
        var settings = config.Engines.GetValueOrDefault(BackendId);
        if (settings != null && settings.Parameters.TryGetValue("packageRoot", out var root) &&
            !string.IsNullOrWhiteSpace(root))
            return root;
        return DefaultPackageRoot();
    }

    /// <summary>Whether optional KVMem health/update checks are relevant for this installation.</summary>
    public static bool IsInstalledOrConfigured(ManagerConfig config, string? managerRoot = null)
    {
        if (config.ActiveBackendId == BackendId) return true;
        if (config.Engines.TryGetValue(BackendId, out var settings) &&
            (!string.IsNullOrWhiteSpace(settings.ModelPath) || !string.IsNullOrWhiteSpace(settings.ExecutablePath) ||
             settings.Parameters.Any(p => !string.IsNullOrWhiteSpace(p.Value)))) return true;
        var root = EngineCatalog.EngineRoot(managerRoot ?? ConfigService.GetManagerRoot(), BackendId);
        if (!Directory.Exists(root)) return false;
        try
        {
            return Directory.EnumerateDirectories(root, "v*", SearchOption.TopDirectoryOnly).Take(65)
                .Any(d => !File.Exists(Path.Combine(d, PendingMarker)) &&
                    (File.Exists(Path.Combine(d, VersionRelativePath)) || File.Exists(Path.Combine(d, WorkerRelativePath))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Reads the console/package version; the native worker does not implement --version.</summary>
    public static string? ReadVersion(string packageRoot)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, VersionRelativePath)));
            if (!doc.RootElement.TryGetProperty("version", out var value) || value.ValueKind != JsonValueKind.String)
                return null;
            var version = value.GetString()?.Trim();
            return version != null && version.Length <= 128 &&
                Regex.IsMatch(version, @"^v?\d{1,6}\.\d{1,6}\.\d{1,6}(?:[-.][A-Za-z0-9]{1,24})*$") ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static int? ReadArchitecture(string packageRoot)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, ArchitectureRelativePath)));
            if (!doc.RootElement.TryGetProperty("architecture", out var value)) return null;
            return int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var architecture)
                ? architecture : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Read-only layout and release/architecture validation, with no GPU/model loading.</summary>
    public static ValidationResult Validate(string packageRoot, int? expectedArchitecture = null, string? expectedVersion = null)
    {
        string? version = null;
        int? architecture = null;
        try
        {
            var root = Path.GetFullPath(packageRoot);
            if (!Directory.Exists(root)) return new(false, "KVMem 包目录不存在：" + root, null, null);
            foreach (var relative in RequiredRelativeFiles)
            {
                var path = Path.Combine(root, relative);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    return new(false, "KVMem unified 包不完整，缺少或为空：" + relative, version, architecture);
                EnsureNoReparsePoints(path);
            }
            var cuda = Path.Combine(root, CudaRelativePath);
            if (!Directory.Exists(cuda) || !Directory.EnumerateFiles(cuda, "cublas64_*.dll").Any() ||
                !Directory.EnumerateFiles(cuda, "cublasLt64_*.dll").Any())
                return new(false, "KVMem unified 包缺少 runtime/cuda 的 cuBLAS 运行时；请重新安装完整官方包。", version, architecture);
            EnsureNoReparsePoints(cuda);
            version = ReadVersion(root);
            architecture = ReadArchitecture(root);
            if (version == null)
                return new(false, "console-build.json 没有可读的 KVMem version。", null, architecture);
            if (architecture == null || EngineCatalog.KvmemGpuFamily(architecture.Value) == null)
                return new(false, "provenance/architecture-build.json 没有受支持的精确 CUDA architecture。", version, architecture);
            if (expectedArchitecture.HasValue && architecture != expectedArchitecture)
                return new(false, $"KVMem 包为 sm{architecture}，本机/下载计划要求 sm{expectedArchitecture}，拒绝跨架构安装。", version, architecture);
            if (expectedVersion != null && !string.Equals(version.TrimStart('v', 'V'), expectedVersion.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase))
                return new(false, $"KVMem 包版本 {version} 与发布版本 {expectedVersion} 不一致。", version, architecture);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, VersionRelativePath)));
            if (!manifest.RootElement.TryGetProperty("supported_backends", out var backends) ||
                backends.ValueKind != JsonValueKind.Array ||
                !backends.EnumerateArray().Any(b => b.ValueKind == JsonValueKind.String && b.GetString() == "polystrata") ||
                !backends.EnumerateArray().Any(b => b.ValueKind == JsonValueKind.String && b.GetString() == "ninfer"))
                return new(false, "console-build.json 不是包含 PolyStrata + NInfer 的完整 unified 包。", version, architecture);
            return new(true, $"KVMem {version} unified / sm{architecture} 布局完整。", version, architecture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new(false, "KVMem 包检查失败：" + ex.Message, version, architecture);
        }
    }

    /// <summary>Read-only, bounded subprocess seam for runtime/capability tests.</summary>
    public static Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<string>> RuntimeProbe { get; set; } = RunReadOnlyProbeAsync;

    /// <summary>
    /// Uses bundled Python and CUDA only; no pip, venv, downloads, service start or model selection.
    /// -B and PYTHONDONTWRITEBYTECODE prevent imports from writing into the installation.
    /// </summary>
    public static async Task<ValidationResult> ValidateRuntimeAsync(string packageRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var layout = Validate(packageRoot);
        if (!layout.Ok) return layout;
        try
        {
            var root = Path.GetFullPath(packageRoot);
            var python = CreateProbeStart(root, Path.Combine(root, PythonRelativePath));
            python.WorkingDirectory = Path.Combine(root, ServeRootRelativePath);
            python.ArgumentList.Add("-B");
            python.ArgumentList.Add("-s");
            python.ArgumentList.Add("-c");
            python.ArgumentList.Add("import sys; assert sys.version_info >= (3, 10); import numpy, regex, jinja2, psutil, yaml, requests, tqdm; from PIL import Image; import serve.server; print('KVMem bundled imports OK')");
            await RuntimeProbe(python, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var native = CreateProbeStart(root, Path.Combine(root, WorkerRelativePath));
            native.ArgumentList.Add("--help");
            var help = await RuntimeProbe(native, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (!help.Contains("--kvmem-budget", StringComparison.Ordinal) ||
                !help.Contains("--kvmem-history-reuse", StringComparison.Ordinal))
                return layout with { Ok = false, Detail = "原生 worker --help 缺少 --kvmem-budget / --kvmem-history-reuse；这不是兼容的 KVMem 内核。" };
            var capabilities = CreateProbeStart(root, Path.Combine(root, WorkerRelativePath));
            capabilities.ArgumentList.Add("--kvmem-capabilities");
            var capabilityJson = await RuntimeProbe(capabilities, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (!SupportsCapabilities(capabilityJson))
                return layout with { Ok = false, Detail = "原生 --kvmem-capabilities 不满足 unified PolyStrata 契约（schema=1、query revision>=2、independent/signed LRFU、block 32/64）。" };
            using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ArchitectureRelativePath)));
            if (provenance.RootElement.TryGetProperty("polystrata", out var poly) &&
                poly.TryGetProperty("worker_sha256", out var hash))
            {
                var expected = hash.ValueKind == JsonValueKind.String ? EngineCatalog.ParseDigest(hash.GetString()) : null;
                if (expected == null)
                    return layout with { Ok = false, Detail = "provenance 的 PolyStrata worker SHA-256 无效。" };
                var actual = await EngineDownloadService.Sha256Async(Path.Combine(root, WorkerRelativePath), ct).ConfigureAwait(false);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return layout with { Ok = false, Detail = "原生 PolyStrata worker SHA-256 与 provenance 不一致。" };
            }
            ct.ThrowIfCancellationRequested();
            return layout with { Detail = layout.Detail + " 包内 Python imports、native help/capabilities 和可用 provenance hash 检查通过（未加载模型）。" };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return layout with { Ok = false, Detail = "KVMem 运行时检查失败：" + ex.Message };
        }
    }

    /// <summary>Pure validation of the native, read-only capability handshake.</summary>
    public static bool SupportsCapabilities(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.TryGetProperty("schema_version", out var schema) && schema.TryGetInt32(out var schemaVersion) && schemaVersion == 1 &&
                root.TryGetProperty("backend", out var backend) && backend.ValueKind == JsonValueKind.String && backend.GetString() == "polystrata" &&
                root.TryGetProperty("query_scoring_revision", out var revision) && revision.TryGetInt32(out var rev) && rev >= 2 &&
                root.TryGetProperty("independent_graph_lrfu", out var independent) && independent.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("signed_lrfu_hits", out var signed) && signed.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("block_tokens", out var blocks) && blocks.ValueKind == JsonValueKind.Object &&
                blocks.TryGetProperty("default", out var defaultBlock) && defaultBlock.TryGetInt32(out var block) && block == 32 &&
                blocks.TryGetProperty("startup_fixed", out var fixedBlock) && fixedBlock.ValueKind == JsonValueKind.True &&
                blocks.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.Array &&
                supported.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n == 32) &&
                supported.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n == 64);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    private static ProcessStartInfo CreateProbeStart(string root, string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["PATH"] = string.Join(Path.PathSeparator,
            Path.Combine(root, CudaRelativePath), Path.GetDirectoryName(Path.Combine(root, WorkerRelativePath)),
            Path.GetDirectoryName(Path.Combine(root, PythonRelativePath)), Environment.GetEnvironmentVariable("PATH") ?? "");
        start.Environment["CUDA_VISIBLE_DEVICES"] = "-1"; // health/setup probes must never load a GPU/model
        start.Environment.Remove("PYTHONHOME");
        start.Environment["PYTHONPATH"] = Path.Combine(root, ServeRootRelativePath);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["PYTHONNOUSERSITE"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        return start;
    }

    private static async Task<string> RunReadOnlyProbeAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 KVMem 检查进程。");
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = string.Join("\n", await Task.WhenAll(stdout, stderr).ConfigureAwait(false));
            // The bundled PolyStrata worker intentionally prints --help with exit code 2.
            var helpExit = process.ExitCode == 2 && start.ArgumentList.Count == 1 && start.ArgumentList[0] == "--help" &&
                output.Contains("--kvmem-budget", StringComparison.Ordinal) && output.Contains("--kvmem-history-reuse", StringComparison.Ordinal);
            if (process.ExitCode != 0 && !helpExit)
                throw new IOException($"{Path.GetFileName(start.FileName)} 检查退出码 {process.ExitCode}：" +
                    (output.Length > 4096 ? output[^4096..] : output).Trim());
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{Path.GetFileName(start.FileName)} 检查超过 {timeout.TotalSeconds:0} 秒。");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(reap.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        }
    }

    /// <summary>Rejects symlink/junction paths before package validation, extraction, rename or cleanup.</summary>
    internal static void EnsureNoReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("KVMem 安装路径包含符号链接/目录联接：" + current);
            current = Path.GetDirectoryName(current);
        }
    }
}
