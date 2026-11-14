using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

/// <summary>Focused built-in update contracts. The parent runner registers these six methods.</summary>
internal static class BuiltInUpdateRegression
{
    private const string Old = "b9901000", New = "b9901001", Next = "b9901002";

    public static void RunVersionParsing()
    {
        foreach (var (output, expected) in new[]
        {
            ("version: 0.9.0-dev (build 12125, commit 7d516e25e)\n", "b12125"),
            ("version:0.9.0-dev(build12125)", "b12125"), // compact compatibility boundary, not the real-machine sample
            ("unrelated loader output\nversion: 0.9.0-dev (build 0012125, commit abcd)", "b12125"),
            ("version: 12125 (abcd)", "b12125"), ("llama.cpp version: 0012125 (abcd)", "b12125"),
            ("llama-server version: b12125", "b12125"), ("b12125 (abcd)", "b12125"), ("VERSION: b000001", "b1"),
        }) Equal(expected, BuiltInInstallation.ParseLlamaVersion(output));
        foreach (var output in new[]
        {
            "", "unknown", "loader mentions b12125", "version: 0.9.0-dev", "version: -12125", "version: b12125junk",
            "version: 999999999999999999999999999", "version: prism-b12125-custom", "Prism b12125\nversion: 12125",
            new string('x', 65537) + "\nversion: 12125",
        }) Check(BuiltInInstallation.ParseLlamaVersion(output) == null, "untrusted/variant version accepted: " + output[..Math.Min(80, output.Length)]);
        Check(BuiltInInstallation.IsComparableVersion("llamacpp", "b12125"), "normalized llama build tag is not comparable");
        Check(!BuiltInInstallation.IsComparableVersion("llamacpp", "0.9.0-dev") &&
            !BuiltInInstallation.IsComparableVersion("llamacpp", "prism-b12125"), "semver/variant was treated as an official build tag");
        Equal(13.4, EngineAcquisitionService.ParseDriverCudaVersion("NVIDIA-SMI 617.42 | CUDA Version: 13.4 |"));
        Equal(13.4, EngineAcquisitionService.ParseDriverCudaVersion("NVIDIA-SMI 617.42 | CUDA UMD Version: 13.4 |"));
        foreach (var invalid in new[] { "", "CUDA Version: unknown", "CUDA UMD Version: NaN", "driver 617.42", "CUDA Version: -1" })
            Equal(0d, EngineAcquisitionService.ParseDriverCudaVersion(invalid));
    }

    public static void RunManagedPlans(string root)
    {
        var release = Names("b12125",
            "llama-b12124-bin-win-cuda-13.4-x64.zip", "cudart-llama-b12124-bin-win-cuda-13.4-x64.zip",
            "llama-b12125-bin-win-cuda-13.5-x64.zip", "cudart-llama-b12125-bin-win-cuda-13.5-x64.zip",
            "llama-b12125-bin-win-cuda-13.4-x64.zip", "cudart-llama-bin-win-cuda-13.4-x64.zip",
            "llama-b12125-bin-win-cuda-12.4-x64.zip", "cudart-llama-b12125-bin-win-cuda-12.4-x64.zip");
        var plan = EngineCatalog.ManagedLlamaCppPlan(release, root, 8.6, 13.4);
        Check(plan != null, "complete driver-compatible llama pair was not selected");
        Equal(2, plan!.EngineArchives.Count);
        Equal("13.4", plan.CudaVersion);
        Equal("llama-b12125-bin-win-cuda-13.4-x64.zip", plan.EngineArchives[0].Name);
        Equal("cudart-llama-bin-win-cuda-13.4-x64.zip", plan.EngineArchives[1].Name);
        SamePath(Path.Combine(root, "engines", "llamacpp", "b12125-cuda-13.4"), plan.PackageRoot);
        SamePath(plan.PackageRoot!, plan.EngineDir);
        var fallback = Names("b12125", "llama-b12125-bin-win-cuda-13.5-x64.zip", "cudart-llama-bin-win-cuda-13.5-x64.zip",
            "llama-b12125-bin-win-cuda-12.4-x64.zip", "cudart-llama-bin-win-cuda-12.4-x64.zip");
        Equal("12.4", EngineCatalog.ManagedLlamaCppPlan(fallback, root, 8.6, 13.4)!.CudaVersion);
        Equal("12.4", EngineCatalog.ManagedLlamaCppPlan(release, root, 7.5, 13.5)!.CudaVersion);
        Equal("12.4", EngineCatalog.ManagedLlamaCppPlan(release, root, 8.6, 0)!.CudaVersion);
        foreach (var missing in new[]
        {
            Names("b12125", "llama-b12125-bin-win-cuda-13.4-x64.zip"),
            Names("b12125", "llama-b12125-bin-win-cuda-13.4-x64.zip", "cudart-llama-bin-win-cuda-12.4-x64.zip"),
            Names("b12125", "llama-b12124-bin-win-cuda-13.4-x64.zip", "cudart-llama-bin-win-cuda-13.4-x64.zip"),
            Names("b12125", "llama-b12125-bin-win-cuda-13.4-x64.zip", "cudart-llama-b12124-bin-win-cuda-13.4-x64.zip"),
        }) Check(EngineCatalog.ManagedLlamaCppPlan(missing, root, 8.6, 13.4) == null, "cross-tag/minor or incomplete runtime pair accepted");
        foreach (var unsafeTag in new[] { "latest", "b12125/../../escape", "b12125;run", "b999999999999999999999999" })
            Check(EngineCatalog.ManagedLlamaCppPlan(release with { Tag = unsafeTag }, root, 8.6, 13.4) == null, "unsafe managed target tag accepted");
        var poly = EngineCatalog.ManagedPolyStrataPlan(Names("v0.1.45-poly.6", "strata-poly-windows-x64-abcd1234.zip"), root);
        Check(poly?.SourceArchive != null, "managed PolyStrata lost its exact-tag source archive");
        Check(poly!.SourceArchive!.Url.Contains("v0.1.45-poly.6", StringComparison.Ordinal), "PolyStrata source tag did not match the binary release");
        SamePath(Path.Combine(poly.PackageRoot!, "engine-poly"), poly.EngineDir);
        Check(EngineCatalog.ManagedPolyStrataPlan(Names("../v0.1.45-poly.6", "strata-poly-windows-x64-abcd1234.zip"), root) == null, "unsafe PolyStrata target tag accepted");
    }

    public static void RunCudaWheelPins()
    {
        const string pins13 = "CUDA_WHEELS = ['nvidia-cublas==13.4.1.2', 'nvidia-cuda-runtime==13.4.78']";
        const string pins12 = "CUDA12_WHEELS = [\"nvidia-cublas-cu12==12.4.5.8\", \"nvidia-cuda-runtime-cu12==12.4.127\"]";
        var source = "raise RuntimeError('source must never execute')\n" + pins13 + "\n" + pins12;
        Check(BuiltInInstallation.ParseCudaWheelPins(source, "13.4").SequenceEqual(new[] { "nvidia-cublas==13.4.1.2", "nvidia-cuda-runtime==13.4.78" }), "same-release exact CUDA13 literals were not read");
        Check(BuiltInInstallation.ParseCudaWheelPins(source, "12.4").SequenceEqual(new[] { "nvidia-cublas-cu12==12.4.5.8", "nvidia-cuda-runtime-cu12==12.4.127" }), "CUDA12 literals were not kept separate");
        foreach (var invalid in new[]
        {
            "CUDA_WHEELS = get_packages()", pins13 + "; __import__('os').system('do-not-run')",
            "CUDA_WHEELS = ['nvidia-cublas==13.4.1.2', __import__('os').system('do-not-run')]",
            "CUDA_WHEELS = ['nvidia-cublas>=13.4', 'nvidia-cuda-runtime==13.4.78']",
            "CUDA_WHEELS = ['nvidia-cublas==13.5.1', 'nvidia-cuda-runtime==13.4.78']",
            "CUDA_WHEELS = ['nvidia-cublas==13.4.1.2', 'nvidia-cublas==13.4.1.2']",
            "CUDA_WHEELS = ['requests==13.4.1', 'nvidia-cuda-runtime==13.4.78']",
            "CUDA_WHEELS = ['nvidia-cublas==13.4.1.2']", new string('x', (2 << 20) + 1) + "\n" + pins13,
        }) Equal(0, BuiltInInstallation.ParseCudaWheelPins(invalid, "13.4").Count);
        Equal(0, BuiltInInstallation.ParseCudaWheelPins(source, "13.5").Count);
        Equal(0, BuiltInInstallation.ParseCudaWheelPins(source, "unknown").Count);
    }

    public static async Task RunVisionProbeAsync()
    {
        var original = BuiltInInstallation.NativeProbe;
        try
        {
            BuiltInInstallation.NativeProbe = (start, timeout, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Equal(0, start.ArgumentList.Count); // Actual strata-vision rejects --help.
                Equal("-1", start.Environment["CUDA_VISIBLE_DEVICES"]);
                Check(timeout <= TimeSpan.FromSeconds(10), "vision probe unbounded");
                return Task.FromResult(new BuiltInInstallation.ProbeOutput(2, "usage: strata-vision --mmproj <mmproj.gguf> --model <model.gguf>"));
            };
            Check(await BuiltInInstallation.ValidateVisionAsync(Path.Combine(ConfigService.GetManagerRoot(), "strata-vision.exe")) == null, "actual no-argument vision usage was rejected");
            BuiltInInstallation.NativeProbe = (_, _, _) => Task.FromResult(new BuiltInInstallation.ProbeOutput(2, "unknown argument --help"));
            Check(await BuiltInInstallation.ValidateVisionAsync("strata-vision.exe") != null, "unknown option was treated as healthy vision");
            BuiltInInstallation.NativeProbe = (_, _, _) => Task.FromResult(new BuiltInInstallation.ProbeOutput(unchecked((int)0xC0000135), ""));
            Check(await BuiltInInstallation.ValidateVisionAsync("strata-vision.exe") != null, "missing dependency accepted");
        }
        finally { BuiltInInstallation.NativeProbe = original; }
    }

    public static async Task RunLatestAndManualAsync(string root)
    {
        using var s = new Scenario(root, "latest-manual");
        s.Releases = Releases(Names("b9900999", PairNames("b9900999", "13.4")), Names(Old, PairNames(Old, "13.4")),
            Names("b9901010", PairNames("b9901010", "13.5")));
        var before = ConfigService.ReadConfig();
        var same = await EngineAcquisitionService.UpdateAsync("llamacpp", s.Log, CancellationToken.None);
        Check(!same.Updated && same.LatestVersion == Old, "same-version/driver filtering selected a downgrade or incompatible release");
        Equal(0, s.ArchiveCalls);
        Check(JsonNode.DeepEquals(Node(before), Node(ConfigService.ReadConfig())), "latest-version check changed settings");
        Equal(Old, (await EngineAcquisitionService.LatestCompatibleReleaseAsync("llamacpp"))!.Tag);
        s.OldOutput = "version: " + Next;
        Check(!(await EngineAcquisitionService.UpdateAsync("llamacpp", s.Log, CancellationToken.None)).Updated, "newer local version was downgraded");
        foreach (var unknown in new[] { "no trusted version", "version: 0.9.0-dev", "version: prism-b9901000-custom" })
        {
            s.OldOutput = unknown;
            var releaseCalls = s.ReleaseCalls;
            await ThrowsAsync<InvalidOperationException>(() => EngineAcquisitionService.UpdateAsync("llamacpp", s.Log, CancellationToken.None));
            Equal(releaseCalls, s.ReleaseCalls);
            Equal(0, s.ArchiveCalls);
        }
        s.OldOutput = "version: " + Old;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var probes = s.NativeCalls;
        await ThrowsAsync<OperationCanceledException>(() => EngineAcquisitionService.UpdateAsync("llamacpp", s.Log, cancelled.Token));
        Equal(probes, s.NativeCalls);
        Equal(0, s.ArchiveCalls);
    }

    public static async Task RunLlamaTransactionsAsync(string root)
    {
        using var s = new Scenario(root, "transactions");
        s.Releases = Releases(s.ZipRelease(New));
        ManagerConfig? atCommit = null;
        s.OnPendingProbe = async (_, ct) =>
        {
            await Task.Run(() => ConfigService.SaveEngineParameters("llamacpp", new Dictionary<string, string> { ["concurrentUserValue"] = "latest" }), ct);
            atCommit = ConfigService.ReadConfig();
        };
        var updated = await EngineAcquisitionService.UpdateAsync("llamacpp", s.Log, CancellationToken.None);
        Check(updated.Updated && updated.LatestVersion == New && updated.PackageRoot != null, "verified synthetic release was not committed");
        var installed = updated.PackageRoot!;
        SamePath(Path.Combine(root, "engines", "llamacpp", New + "-cuda-13.4"), installed);
        Check(atCommit != null && !PathIdentity.Equals(installed, s.OldRoot), "update skipped its new-directory verification boundary");
        AssertOnlyInstallationChanges(atCommit!, ConfigService.ReadConfig(), installed, New, s.OldRoot);
        Check(File.ReadAllText(s.OldExe) == "old manual worker", "update overwrote the old executable");
        Check(!File.Exists(Path.Combine(installed, BuiltInInstallation.PendingMarker)), "successful installation retained a pending marker");
        Equal(2, updated.Downloads.Count);
        var metadata = BuiltInInstallation.ReadReleaseMetadata(installed);
        Check(metadata != null && metadata.ReleaseTag == New && metadata.CudaVersion == "13.4", "installed release/runtime provenance was not retained");
        var downloads = s.ArchiveCalls;
        s.OnPendingProbe = null;
        Check(!(await EngineAcquisitionService.UpdateAsync("llamacpp", s.Log, CancellationToken.None)).Updated, "same update reinstalled the just-verified version");
        Equal(downloads, s.ArchiveCalls);

        async Task Reject<T>(ReleaseInfo release, BuiltInInstallation.ProbeOutput output, bool cancel = false) where T : Exception
        {
            s.Releases = Releases(release);
            s.NewOutput = output;
            var before = ConfigService.ReadConfig();
            var directories = Directories(s.EngineRoot);
            using var stop = new CancellationTokenSource();
            s.OnPendingProbe = cancel ? (_, _) => { stop.Cancel(); return Task.CompletedTask; } : null;
            try { await ThrowsAsync<T>(() => EngineAcquisitionService.UpdateAsync("llamacpp", s.Log, stop.Token)); }
            finally { s.OnPendingProbe = null; }
            Check(JsonNode.DeepEquals(Node(before), Node(ConfigService.ReadConfig())), "failed/cancelled update changed active/presets/session/install selection");
            Check(directories.SetEquals(Directories(s.EngineRoot)), "failed/cancelled update left a stage or uncommitted version");
        }
        await Reject<EngineDownloadService.ChecksumMismatchException>(s.ZipRelease(Next, badSha: true), new(0, "version: " + Next));
        var unsigned = s.ZipRelease(Next);
        await Reject<InvalidDataException>(unsigned with { Assets = [unsigned.Assets[0] with { Sha256 = null }, unsigned.Assets[1]] }, new(0, "version: " + Next));
        await Reject<InvalidDataException>(s.ZipRelease(Next, missingExe: true), new(0, "version: " + Next));
        await Reject<InvalidDataException>(s.ZipRelease(Next, missingDll: "cudart64_13.dll"), new(0, "version: " + Next));
        await Reject<InvalidDataException>(s.ZipRelease(Next), new(0, "version: b123"));
        await Reject<InvalidDataException>(s.ZipRelease(Next), new(unchecked((int)0xc0000135u), "missing native runtime DLL"));
        await Reject<OperationCanceledException>(s.ZipRelease(Next), new(0, "version: " + Next), cancel: true);
    }

    public static async Task RunUpdateAllAsync(string root)
    {
        using var s = new Scenario(root, "update-all");
        var ids = BackendRegistry.All().Where(b => b.Descriptor.BuiltIn && EngineAcquisitionService.CanAcquire(b.Descriptor.Id)).Select(b => b.Descriptor.Id).ToArray();
        var visited = new List<string>();
        EngineAcquisitionService.ReleasesFetcher = (repo, _) => { visited.Add(repo); throw new IOException("isolated failure for " + repo); };
        var before = ConfigService.ReadConfig();
        var results = await EngineAcquisitionService.UpdateAllAsync(s.Log);
        Check(results.Select(r => r.BackendId).SequenceEqual(ids), "UpdateAll did not visit every registered downloadable built-in in order");
        Check(results.All(r => !r.Ok && r.Error != null), "individual update failures were hidden");
        Check(ids.All(id => visited.Contains(EngineAcquisitionService.RepoFor(id)!)), "an individual failure prevented the next backend from being visited");
        Equal(0, s.ArchiveCalls);
        Check(JsonNode.DeepEquals(Node(before), Node(ConfigService.ReadConfig())), "failed UpdateAll changed settings");
        using var stop = new CancellationTokenSource();
        visited.Clear();
        EngineAcquisitionService.ReleasesFetcher = (repo, _) => { visited.Add(repo); stop.Cancel(); return Task.FromCanceled<JsonArray?>(stop.Token); };
        await ThrowsAsync<OperationCanceledException>(() => EngineAcquisitionService.UpdateAllAsync(s.Log, stop.Token));
        Equal(1, visited.Count);
        Equal(0, s.ArchiveCalls);
    }

    private static ReleaseInfo Names(string tag, params string[] names) => new(tag,
        names.Select(n => new ReleaseAsset(n, $"https://github.com/{EngineCatalog.LlamaCppRepo}/releases/download/{tag}/{n}", new string('a', 64))).ToArray());
    private static string[] PairNames(string tag, string cuda) =>
        [$"llama-{tag}-bin-win-cuda-{cuda}-x64.zip", $"cudart-llama-{tag}-bin-win-cuda-{cuda}-x64.zip"];
    private static JsonArray Releases(params ReleaseInfo[] releases) => new(releases.Select(r => (JsonNode)new JsonObject
    {
        ["tag_name"] = r.Tag, ["assets"] = new JsonArray(r.Assets.Select(a => (JsonNode)new JsonObject
        { ["name"] = a.Name, ["browser_download_url"] = a.Url, ["digest"] = a.Sha256 == null ? null : "sha256:" + a.Sha256 }).ToArray()),
    }).ToArray());
    private static JsonNode Node(ManagerConfig config) => JsonSerializer.SerializeToNode(config)!;
    private static void SamePath(string expected, string? actual) => Check(PathIdentity.Equals(expected, actual), "unexpected path: " + actual);
    private static HashSet<string> Directories(string root) => Directory.Exists(root)
        ? Directory.EnumerateDirectories(root).Select(PathIdentity.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    private static void AssertOnlyInstallationChanges(ManagerConfig expected, ManagerConfig actual, string installed, string tag, string oldRoot)
    {
        var wanted = Node(expected);
        wanted["UpdatedAt"] = actual.UpdatedAt;
        var engine = wanted["Engines"]!["llamacpp"]!;
        engine["ExecutablePath"] = Path.Combine(installed, "llama-server.exe");
        engine["Parameters"]!["packageRoot"] = installed;
        engine["Parameters"]!["releaseVersion"] = tag;
        engine["Parameters"]!["runtimeCuda"] = "13.4";
        foreach (var preset in engine["ModelPresets"]!.AsObject().Select(p => p.Value!.AsObject()))
            if (preset["packageRoot"]?.GetValue<string>() is { } path && PathIdentity.Equals(path, oldRoot)) preset["packageRoot"] = installed;
        Check(JsonNode.DeepEquals(wanted, Node(actual)), "installation commit changed model/tuning/manual presets/session/active backend or dropped concurrent settings");
    }

    /// <summary>Small shared fixture only; no process is ever started. All process/network/driver seams are restored.</summary>
    private sealed class Scenario : IDisposable
    {
        private readonly ManagerConfig _original;
        private readonly ConfigStore _store;
        private readonly byte[]? _backup;
        private readonly string _backupPath, _downloadRoot;
        private readonly HashSet<string> _engineDirectories, _downloadDirectories;
        private readonly Func<string, CancellationToken, Task<JsonArray?>> _releases = EngineAcquisitionService.ReleasesFetcher;
        private readonly Func<ReleaseAsset, string, ILogSink, CancellationToken, Task<string>> _archives = EngineAcquisitionService.ArchiveFetcher;
        private readonly Func<double> _capability = EngineAcquisitionService.DetectComputeCapability, _driver = EngineAcquisitionService.DetectCudaVersion;
        private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<BuiltInInstallation.ProbeOutput>> _native = BuiltInInstallation.NativeProbe;
        private readonly Func<string, CancellationToken, Task<ReleaseAsset?>> _wheels = EngineAcquisitionService.RuntimeWheelFetcher;
        private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<BuiltInInstallation.ProbeOutput>> _setup = EngineAcquisitionService.SetupProcessRunner;
        private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<string>> _kvmem = KvmemInstallation.RuntimeProbe;
        private readonly Dictionary<string, string> _zipPaths = new(StringComparer.OrdinalIgnoreCase);
        public string Work { get; }
        public string EngineRoot { get; }
        public string OldRoot { get; }
        public string OldExe { get; }
        public ILogSink Log { get; } = new DelegateLogSink(_ => { });
        public JsonArray Releases { get; set; } = [];
        public string OldOutput { get; set; } = "version: " + Old;
        public BuiltInInstallation.ProbeOutput NewOutput { get; set; } = new(0, "version: " + New);
        public Func<ProcessStartInfo, CancellationToken, Task>? OnPendingProbe { get; set; }
        public int ArchiveCalls, ReleaseCalls, NativeCalls;

        public Scenario(string root, string label)
        {
            var full = Path.GetFullPath(root);
            var leaf = Path.GetFileName(full);
            Check(PathIdentity.Equals(full, ConfigService.GetManagerRoot()) && leaf.StartsWith("regression-", StringComparison.Ordinal) &&
                Guid.TryParseExact(leaf[(leaf.LastIndexOf('-') + 1)..], "N", out _), "built-in update tests must never use a real installation root");
            Work = Path.Combine(root, "builtin-update", label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Work);
            EngineRoot = EngineCatalog.EngineRoot(root, "llamacpp");
            _downloadRoot = Path.Combine(root, "downloads", "llamacpp");
            _engineDirectories = Directories(EngineRoot); _downloadDirectories = Directories(_downloadRoot);
            _original = ConfigService.ReadConfig();
            _store = new ConfigStore(Path.Combine(root, "llama-manager.config.json"));
            _backupPath = Path.Combine(root, "llama-manager.config.json.bak");
            _backup = File.Exists(_backupPath) ? File.ReadAllBytes(_backupPath) : null;
            OldRoot = Path.Combine(Work, "manual old engine");
            OldExe = Write(OldRoot, "llama-server.exe", Encoding.UTF8.GetBytes("old manual worker"));
            var model = Write(Work, "model.gguf", Fixtures.Gguf(moe: false));
            var config = new ManagerConfig { ActiveBackendId = "polystrata", ModelPath = model, Args = "-c 4096 --user-setting untouched", CreatedAt = "2026-11-01T00:00:00Z" };
            config.Engines["llamacpp"] = new EngineSettings
            {
                ExecutablePath = OldExe, ModelPath = model, Port = 8123, LastStats = "preserved stats",
                Parameters = new() { ["packageRoot"] = OldRoot, ["userSetting"] = "preserved" },
                ModelPresets = new()
                {
                    [PathIdentity.Normalize(model)] = new() { ["packageRoot"] = OldRoot, ["context"] = "8192" },
                    [PathIdentity.Normalize(Path.Combine(Work, "other model.gguf"))] = new() { ["packageRoot"] = Path.Combine(Work, "per-model manual override"), ["context"] = "16384" },
                },
            };
            config.Engines["polystrata"] = new EngineSettings { Parameters = new() { ["packageRoot"] = Path.Combine(Work, "missing poly") } };
            config.Engines["kvmem"] = new EngineSettings { Parameters = new() { ["packageRoot"] = Path.Combine(Work, "missing kvmem") } };
            config.ModelPresets[PathIdentity.Normalize(model)] = new ModelPreset { Args = "-c 8192", Signature = "preserve signature" };
            config.SetRunning(ManagerConfig.SlotMain, new ServerSession { ProcessId = int.MaxValue - 1, StartTimeUtcTicks = 1, EnginePath = OldExe, ModelPath = model, Args = "fixture identity", Port = 8123 });
            config.Normalize(); _store.Save(config);
            EngineAcquisitionService.DetectComputeCapability = () => 8.6;
            EngineAcquisitionService.DetectCudaVersion = () => 13.4;
            EngineAcquisitionService.ReleasesFetcher = (_, ct) => { ct.ThrowIfCancellationRequested(); ReleaseCalls++; return Task.FromResult<JsonArray?>((JsonArray)Releases.DeepClone()); };
            EngineAcquisitionService.ArchiveFetcher = (asset, directory, _, ct) =>
            {
                ct.ThrowIfCancellationRequested(); ArchiveCalls++;
                if (!_zipPaths.TryGetValue(asset.Name, out var source)) throw new IOException("unexpected archive fetch: " + asset.Name);
                var copy = Path.Combine(directory, asset.Name); Directory.CreateDirectory(directory); File.Copy(source, copy, overwrite: true);
                return Task.FromResult(copy);
            };
            BuiltInInstallation.NativeProbe = async (start, timeout, ct) =>
            {
                ct.ThrowIfCancellationRequested(); NativeCalls++;
                Equal("-1", start.Environment["CUDA_VISIBLE_DEVICES"]);
                Check(timeout <= TimeSpan.FromSeconds(20) && !start.ArgumentList.Contains("--model") && !start.ArgumentList.Contains("--serve"), "update attempted unbounded/model/GPU startup");
                if (PathIdentity.Equals(start.FileName, OldExe)) return new(0, OldOutput);
                if (Path.GetFileName(start.FileName) != "llama-server.exe") throw new IOException("unexpected native probe: " + start.FileName);
                var package = Path.GetDirectoryName(Path.GetFullPath(start.FileName))!;
                if (File.Exists(Path.Combine(package, BuiltInInstallation.PendingMarker)))
                {
                    Check(!PathIdentity.Equals(start.FileName, ConfigService.ReadConfig().Engines["llamacpp"].ExecutablePath),
                        "unverified pending executable was selected before its commit");
                    if (OnPendingProbe != null) await OnPendingProbe(start, ct);
                    return NewOutput;
                }
                // A later failed repair must not alter the already-committed worker's reported identity.
                var metadata = BuiltInInstallation.ReadReleaseMetadata(package);
                return new(0, "version: " + (metadata?.ReleaseTag ?? New));
            };
            EngineAcquisitionService.RuntimeWheelFetcher = (_, _) => throw new IOException("focused llama tests must not fetch Python wheels");
            EngineAcquisitionService.SetupProcessRunner = (_, _, _) => throw new IOException("focused tests must not run pip/venv");
            KvmemInstallation.RuntimeProbe = (_, _, _) => throw new IOException("missing optional KVMem must not start its runtime");
        }

        public ReleaseInfo ZipRelease(string tag, bool badSha = false, bool missingExe = false, string? missingDll = null)
        {
            var names = PairNames(tag, "13.4");
            var directory = Path.Combine(Work, "archives", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var binary = new Dictionary<string, byte[]> { ["ggml.dll"] = Encoding.UTF8.GetBytes("fixture native dependency") };
            if (!missingExe) binary["llama-server.exe"] = Encoding.UTF8.GetBytes("new synthetic llama " + tag);
            var runtime = new Dictionary<string, byte[]>();
            foreach (var dll in new[] { "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll" })
                if (dll != missingDll) runtime[dll] = Encoding.UTF8.GetBytes("synthetic runtime " + dll);
            var a = Zip(Path.Combine(directory, names[0]), binary); var b = Zip(Path.Combine(directory, names[1]), runtime);
            _zipPaths[names[0]] = a; _zipPaths[names[1]] = b;
            return new ReleaseInfo(tag,
            [new(names[0], $"https://github.com/{EngineCatalog.LlamaCppRepo}/releases/download/{tag}/{names[0]}", badSha ? new string('0', 64) : Hash(a)),
             new(names[1], $"https://github.com/{EngineCatalog.LlamaCppRepo}/releases/download/{tag}/{names[1]}", Hash(b))]);
        }

        public void Dispose()
        {
            EngineAcquisitionService.ReleasesFetcher = _releases; EngineAcquisitionService.ArchiveFetcher = _archives;
            EngineAcquisitionService.DetectComputeCapability = _capability; EngineAcquisitionService.DetectCudaVersion = _driver;
            BuiltInInstallation.NativeProbe = _native; EngineAcquisitionService.RuntimeWheelFetcher = _wheels;
            EngineAcquisitionService.SetupProcessRunner = _setup; KvmemInstallation.RuntimeProbe = _kvmem;
            try { _store.Save(_original); if (_backup != null) File.WriteAllBytes(_backupPath, _backup); else if (File.Exists(_backupPath)) File.Delete(_backupPath); }
            finally { Cleanup(EngineRoot, _engineDirectories); Cleanup(_downloadRoot, _downloadDirectories); }
        }

        private static void Cleanup(string parent, HashSet<string> original)
        {
            if (!Directory.Exists(parent)) return;
            foreach (var directory in Directory.EnumerateDirectories(parent).ToArray())
            {
                if (original.Contains(PathIdentity.Normalize(directory))) continue;
                var full = Path.GetFullPath(directory); var name = Path.GetFileName(full);
                Check(PathIdentity.Equals(Path.GetDirectoryName(full), parent) && (name.StartsWith("b99010", StringComparison.Ordinal) || name.StartsWith(".install-", StringComparison.Ordinal)), "refusing unrelated fixture cleanup");
                Directory.Delete(full, recursive: true);
            }
        }
        private static string Write(string parent, string relative, byte[] bytes)
        { var path = Path.Combine(parent, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); return path; }
        private static string Zip(string path, Dictionary<string, byte[]> files)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, data) in files) { using var output = zip.CreateEntry(name).Open(); output.Write(data); }
            return path;
        }
        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }
}
