using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

/// <summary>
/// Repository routing is a remote concern, not a migration of installed identities or ownership.
/// Only fake files/hashes and injected probes; no native process, network, GPU, pip or model load.
/// The parent console runner registers the two public methods.
/// </summary>
internal static class PolyRepositoryMigrationRegression
{
    private const string CanonicalRepo = "simplexai-labs/PolyStrata";
    private const string LegacyRepo = "VecSzn/PolyStrata";
    private const string OldTag = "v0.1.45-poly.7";
    private const string LatestTag = "v0.1.45-poly.9";
    // Observed via bounded release metadata, not a downloaded/executed native archive.
    private const string LatestAsset = "strata-poly-windows-x64-66af087edd486c6d.zip";
    private const string LatestDigest = "68b344cc4cbaa4afedbe292d3b26ad778801de2ac9c2fbc0cff531b58e4bae92";

    public static async Task RunCanonicalCatalogAsync(string root)
    {
        Equal(CanonicalRepo, EngineCatalog.PolyStrataRepo);
        Equal(CanonicalRepo, EngineAcquisitionService.RepoFor("polystrata"));
        Equal("ggml-org/llama.cpp", EngineAcquisitionService.RepoFor("llamacpp"));
        Equal("kvmem/kvmem", EngineAcquisitionService.RepoFor("kvmem"));
        Check(EngineAcquisitionService.RepoFor("prism") == null && EngineAcquisitionService.RepoFor("unknown") == null,
            "repository rename introduced a backend alias");

        var release = EngineCatalog.ParseRelease(ReleaseNode(LatestTag));
        foreach (var plan in new[]
        {
            EngineCatalog.PolyStrataPlan(release, root),
            EngineCatalog.ManagedPolyStrataPlan(release, root),
            EngineAcquisitionService.PlanFor("polystrata", release, root, 8.6),
            EngineAcquisitionService.ManagedPlanFor("polystrata", release, root, 8.6, 13.4),
        })
        {
            Check(plan != null && plan.SourceArchive != null, "poly.9 hash-named Windows asset was not mapped");
            Equal("polystrata", plan!.BackendId);
            Equal(1, plan.EngineArchives.Count);
            Equal(LatestAsset, plan.EngineArchives[0].Name);
            Equal(LatestDigest, plan.EngineArchives[0].Sha256);
            Equal($"https://github.com/{CanonicalRepo}/releases/download/{LatestTag}/{LatestAsset}", plan.EngineArchives[0].Url);
            Equal($"https://github.com/{CanonicalRepo}/archive/refs/tags/{LatestTag}.zip", plan.SourceArchive!.Url);
            Equal(LatestTag + ".zip", plan.SourceArchive.Name);
            Check(plan.SourceArchive.Sha256 == null, "invented an upstream source digest");
            Check(PathIdentity.Equals(Path.Combine(plan.SourceDir!, "engine-poly"), plan.EngineDir), "repository rename moved the package layout");
        }
        Equal($"https://github.com/{CanonicalRepo}/archive/refs/tags/{OldTag}.zip",
            EngineCatalog.ManagedPolyStrataPlan(EngineCatalog.ParseRelease(ReleaseNode(OldTag)), root)!.SourceArchive!.Url);
        // A caller explicitly asking for historical provenance must not be silently rewritten.
        Equal($"https://github.com/{LegacyRepo}/archive/refs/tags/{OldTag}.zip", EngineCatalog.SourceArchive(LegacyRepo, OldTag).Url);
        Check(EngineAcquisitionService.ManagedPlanFor("llamacpp", release, root, 8.6, 13.4) == null &&
            EngineAcquisitionService.ManagedPlanFor("kvmem", release, root, 8.6, 13.4) == null,
            "PolyStrata asset became eligible for another backend");
        var llama = new ReleaseInfo("b12125", [
            new("llama-b12125-bin-win-cuda-13.4-x64.zip", "https://github.com/ggml-org/llama.cpp/releases/download/b12125/llama-b12125-bin-win-cuda-13.4-x64.zip", new string('a', 64)),
            new("cudart-llama-bin-win-cuda-13.4-x64.zip", "https://github.com/ggml-org/llama.cpp/releases/download/b12125/cudart-llama-bin-win-cuda-13.4-x64.zip", new string('b', 64)),
        ]);
        Equal("13.4", EngineCatalog.ManagedLlamaCppPlan(llama, root, 8.6, 13.4)!.CudaVersion);
        var kvmem = new ReleaseInfo("v0.19.1", [
            new("kvmem-v0.19.1-windows-x64-nvidia-30-unified.zip", "https://fixture.invalid/kvmem.zip", new string('c', 64)),
        ]);
        var kvPlan = EngineCatalog.KvmemPlan(kvmem, root, 8.6);
        Check(kvPlan != null && kvPlan.SourceArchive == null && kvPlan.KvmemArchitecture == 86, "KVMem unified layout changed");

        // Exact literal lines from v0.1.45-poly.9/setup.py:213,221. make_poly_release.py:35-37
        // requires CUDA 13.0, not llama.cpp's 13.4. PolyStrata's CUDA12 support is source-built;
        // that list does not imply a second Windows PolyStrata asset. Never execute setup.py.
        const string source = """
            raise RuntimeError('never execute release source')
            CUDA_WHEELS = ["nvidia-cublas==13.0.2.14", "nvidia-cuda-runtime==13.0.96"]
            CUDA12_WHEELS = ["nvidia-cublas-cu12==12.9.1.4", "nvidia-cuda-runtime-cu12==12.9.79"]
            """;
        Check(BuiltInInstallation.ParseCudaWheelPins(source, "13.0").SequenceEqual(
            new[] { "nvidia-cublas==13.0.2.14", "nvidia-cuda-runtime==13.0.96" }), "poly.9 exact CUDA13 pins rejected");
        Check(BuiltInInstallation.ParseCudaWheelPins(source, "12.9").SequenceEqual(
            new[] { "nvidia-cublas-cu12==12.9.1.4", "nvidia-cuda-runtime-cu12==12.9.79" }), "CUDA12 source pins mixed with CUDA13");
        Equal(0, BuiltInInstallation.ParseCudaWheelPins(source, "13.4").Count);
        Equal(0, BuiltInInstallation.ParseCudaWheelPins(source, "12.4").Count);

        var savedReleases = EngineAcquisitionService.ReleasesFetcher;
        var savedCapability = EngineAcquisitionService.DetectComputeCapability;
        var savedDriver = EngineAcquisitionService.DetectCudaVersion;
        var queries = 0;
        try
        {
            EngineAcquisitionService.DetectComputeCapability = () => 8.6;
            EngineAcquisitionService.DetectCudaVersion = () => throw new Exception("PolyStrata query must not select llama.cpp CUDA runtime");
            EngineAcquisitionService.ReleasesFetcher = (repo, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Equal(CanonicalRepo, repo);
                queries++;
                return Task.FromResult<JsonArray?>(new JsonArray(ReleaseNode(OldTag), ReleaseNode(LatestTag), ReleaseNode("v0.1.45-poly.8")));
            };
            Equal(LatestTag, (await EngineAcquisitionService.LatestCompatibleReleaseAsync("polystrata"))!.Tag);
            Equal(1, queries);
        }
        finally
        {
            EngineAcquisitionService.ReleasesFetcher = savedReleases;
            EngineAcquisitionService.DetectComputeCapability = savedCapability;
            EngineAcquisitionService.DetectCudaVersion = savedDriver;
        }
    }

    public static async Task RunLegacyOwnedReuseAsync(string root)
    {
        using var fixture = new LegacyFixture(root);
        Check(!File.Exists(Path.Combine(fixture.Package, EngineVersionRetention.ManifestFileName)),
            "live poly.7 fixture must start with release provenance only, not deletion ownership");
        var metadata = BuiltInInstallation.ReadReleaseMetadata(fixture.Package);
        Check(metadata != null, "legacy repository made owned release metadata unreadable");
        Equal(LegacyRepo, metadata!.Repository);
        Equal(OldTag, metadata.ReleaseTag);
        Equal("0.1.45-poly.7", metadata.Version);
        Equal(2, metadata.Downloads.Count);
        Equal($"https://github.com/{LegacyRepo}/archive/refs/tags/{OldTag}.zip", metadata.Downloads[1].Url);
        Check(metadata.Downloads[0].Url.StartsWith($"https://github.com/{LegacyRepo}/releases/download/{OldTag}/", StringComparison.Ordinal),
            "historical binary URL was rewritten");
        Equal("0.1.45", BuiltInInstallation.ReadPolyBuild(fixture.Executable)!.Version);
        Equal("0.1.45-poly.7", await BuiltInInstallation.ReadPolyVersionAsync(fixture.Executable));
        Equal(0, fixture.NativeCalls); // Hash-verified metadata, not a guessed revision/native fallback.

        var probe = await EngineAcquisitionService.ProbeInstalledAsync("polystrata");
        Check(probe.Exists && probe.Available && !probe.Variant && probe.HasReleaseMetadata,
            "legacy-owned official CUDA package became unavailable/a variant: " + probe.Detail);
        Equal("0.1.45-poly.7", probe.Version);
        Equal("13.0", probe.CudaVersion);
        Equal("0.1.45-poly.7", await EngineAcquisitionService.InstalledVersionAsync("polystrata"));
        var configBefore = File.ReadAllBytes(fixture.ConfigPath);
        var result = await EngineAcquisitionService.UpdateAsync("polystrata", fixture.Log, CancellationToken.None);
        Check(!result.Updated && PathIdentity.Equals(fixture.Package, result.PackageRoot), "same release was not reused");
        Equal("0.1.45-poly.7", result.InstalledVersion);
        Equal(OldTag, result.LatestVersion);
        Equal(1, fixture.ReleaseCalls);
        Equal(0, fixture.ArchiveCalls);
        Check(configBefore.SequenceEqual(File.ReadAllBytes(fixture.ConfigPath)), "read-only reuse rewrote settings/other backends");
        fixture.AssertUnchanged();
        Check(!File.Exists(Path.Combine(fixture.Package, EngineVersionRetention.ManifestFileName)),
            "same-version reuse silently adopted a release-metadata-only installation");

        // Separately model older installations that also have explicit ownership receipts.
        fixture.AddOwnershipFixture();
        // Read the same marker/receipt through actual retention validation, not only JSON deserialization.
        // Protect all sibling tests; no process probes or deletions are allowed in this regression.
        var retained = EngineVersionRetention.Run(new(fixture.EngineRoot, "polystrata")
        {
            CurrentRoots = [fixture.Package], DownloadRoot = fixture.DownloadRoot,
            ProtectedRoots = [Path.Combine(root, "engines"), Path.Combine(root, "downloads")],
        }, hooks: new()
        {
            ProbeProcess = _ => throw new Exception("fixture must not inspect a real process"),
            DeleteDirectory = _ => throw new Exception("fixture must not prune a package"),
            DeleteFile = _ => throw new Exception("fixture must not prune a download"),
        });
        Check(retained.Kept.Any(e => PathIdentity.Equals(e.Path, fixture.Package) && e.Reason == "current"), "legacy ownership marker was not usable");
        foreach (var cache in new[] { fixture.Cache, fixture.SourceCache })
            Check(retained.Kept.Any(e => e.Download && PathIdentity.Equals(e.Path, cache) && e.Reason == "current-download"),
                "legacy hash-verified download receipt was not usable: " + cache);
        Equal(0, retained.Pruned.Count);
        fixture.AssertUnchanged();

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var nativeCalls = fixture.NativeCalls;
        await ThrowsAsync<OperationCanceledException>(() => EngineAcquisitionService.UpdateAsync("polystrata", fixture.Log, stop.Token));
        Equal(nativeCalls, fixture.NativeCalls);
        Equal(1, fixture.ReleaseCalls);
        fixture.AssertUnchanged();

        // Preserving provenance must not weaken the executable hash boundary. A changed binary
        // cannot fall through to BUILD's base version or the configured releaseVersion.
        var originalExe = File.ReadAllBytes(fixture.Executable);
        try
        {
            var modified = (byte[])originalExe.Clone();
            modified[0] ^= 1; // same length, different SHA-256
            File.WriteAllBytes(fixture.Executable, modified);
            Check(await BuiltInInstallation.ReadPolyVersionAsync(fixture.Executable) == null, "modified executable reused historical metadata identity");
            await ThrowsAsync<InvalidOperationException>(() => EngineAcquisitionService.UpdateAsync("polystrata", fixture.Log, CancellationToken.None));
            Equal(1, fixture.ReleaseCalls); // reject before querying or acquiring anything
            Equal(0, fixture.ArchiveCalls);
        }
        finally { File.WriteAllBytes(fixture.Executable, originalExe); }
        Equal("0.1.45-poly.7", await BuiltInInstallation.ReadPolyVersionAsync(fixture.Executable));
        Check(configBefore.SequenceEqual(File.ReadAllBytes(fixture.ConfigPath)), "cancelled/rejected update changed settings");
        fixture.AssertUnchanged();
    }

    private static JsonObject ReleaseNode(string tag) => new()
    {
        ["tag_name"] = tag,
        ["assets"] = new JsonArray(new JsonObject
        {
            ["name"] = tag == LatestTag ? LatestAsset : "strata-poly-windows-x64-abcd1234.zip",
            ["browser_download_url"] = $"https://github.com/{CanonicalRepo}/releases/download/{tag}/" +
                (tag == LatestTag ? LatestAsset : "strata-poly-windows-x64-abcd1234.zip"),
            ["digest"] = "sha256:" + (tag == LatestTag ? LatestDigest : new string('a', 64)),
        }),
    };

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class LegacyFixture : IDisposable
    {
        private readonly Func<string, CancellationToken, Task<JsonArray?>> _releases = EngineAcquisitionService.ReleasesFetcher;
        private readonly Func<double> _capability = EngineAcquisitionService.DetectComputeCapability;
        private readonly Func<double> _driver = EngineAcquisitionService.DetectCudaVersion;
        private readonly Func<ReleaseAsset, string, ILogSink, CancellationToken, Task<string>> _archives = EngineAcquisitionService.ArchiveFetcher;
        private readonly Func<string, CancellationToken, Task<ReleaseAsset?>> _wheels = EngineAcquisitionService.RuntimeWheelFetcher;
        private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<BuiltInInstallation.ProbeOutput>> _setup = EngineAcquisitionService.SetupProcessRunner;
        private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<BuiltInInstallation.ProbeOutput>> _native = BuiltInInstallation.NativeProbe;
        private readonly Dictionary<string, byte[]> _unchanged = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _work, _cacheDirectory, _backupPath;
        private readonly byte[]? _config, _backup;
        private readonly bool _cacheDirectoryExisted;
        public string ConfigPath { get; }
        public string EngineRoot { get; }
        public string DownloadRoot { get; }
        public string Package { get; }
        public string Executable { get; }
        public string Cache { get; }
        public string SourceCache { get; }
        public ILogSink Log { get; } = new DelegateLogSink(_ => { });
        public int ReleaseCalls, ArchiveCalls, NativeCalls;

        public LegacyFixture(string root)
        {
            var full = Path.GetFullPath(root);
            var leaf = Path.GetFileName(full);
            Check(PathIdentity.Equals(full, ConfigService.GetManagerRoot()) && leaf.StartsWith("regression-", StringComparison.Ordinal) &&
                Guid.TryParseExact(leaf["regression-".Length..], "N", out _), "migration test requires the runner's isolated root, never a user installation");
            var id = Guid.NewGuid().ToString("N");
            _work = Path.Combine(full, "poly-repository-migration", id);
            EngineRoot = Path.Combine(full, "engines", "polystrata");
            DownloadRoot = Path.Combine(full, "downloads", "polystrata");
            Package = Path.Combine(EngineRoot, OldTag + "-cuda-13.0-migration-" + id);
            _cacheDirectory = Path.Combine(DownloadRoot, OldTag);
            _cacheDirectoryExisted = Directory.Exists(_cacheDirectory);
            ConfigPath = Path.Combine(full, "llama-manager.config.json");
            _backupPath = ConfigPath + ".bak";
            _config = File.Exists(ConfigPath) ? File.ReadAllBytes(ConfigPath) : null;
            _backup = File.Exists(_backupPath) ? File.ReadAllBytes(_backupPath) : null;
            Executable = Write(Package, "engine-poly/strata-poly.exe", "fake poly.7 native bytes, never executable");
            Write(Package, "engine-poly/BUILD.json", """{"version":"0.1.45","cuda":"13.0","source":"release","archs":[75,80,86,89,120]}""");
            foreach (var name in new[] { "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll" })
                Write(Package, "engine-poly/" + name, "fake same-release runtime " + name);
            Write(Package, "serve/server.py", "# fake same-release source; never imported");
            var python = Write(Package, "runtime/venv/Scripts/python.exe", "fake private interpreter, never executed");
            Cache = Write(_cacheDirectory, "migration-" + id + ".zip", "fake historical archive bytes, never extracted");
            SourceCache = Write(_cacheDirectory, "migration-source-" + id + ".zip", "fake historical source archive bytes, never extracted");
            var sourceDownload = new BuiltInInstallation.InstalledDownload(Path.GetFileName(SourceCache),
                $"https://github.com/{LegacyRepo}/archive/refs/tags/{OldTag}.zip", SourceCache, Hash(SourceCache), new FileInfo(SourceCache).Length);
            var legacyAssetUrl = $"https://github.com/{LegacyRepo}/releases/download/{OldTag}/strata-poly-windows-x64-abcd1234.zip";
            var download = new BuiltInInstallation.InstalledDownload(Path.GetFileName(Cache), legacyAssetUrl, Cache, Hash(Cache), new FileInfo(Cache).Length);
            var metadata = new BuiltInInstallation.ReleaseMetadata(1, "LlamaManager", "polystrata", LegacyRepo, OldTag,
                "0.1.45-poly.7", "x64", Package, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                Path.GetRelativePath(Package, Executable), Hash(Executable), [download, sourceDownload])
            { CudaVersion = "13.0", PythonRelativePath = Path.GetRelativePath(Package, python), RuntimePackages = ["nvidia-cublas==13.0.2.14", "nvidia-cuda-runtime==13.0.96"] };
            BuiltInInstallation.WriteReleaseMetadata(Package, metadata);

            var llama = Write(_work, "llamacpp/llama-server.exe", "unrelated llama.cpp bytes");
            var kvmem = Write(_work, "kvmem/workers/polystrata/strata-poly.exe", "unrelated KVMem worker bytes");
            var config = new ManagerConfig { ActiveBackendId = "llamacpp" };
            config.Engines["polystrata"] = new EngineSettings
            {
                ExecutablePath = Executable, Parameters = new()
                {
                    ["packageRoot"] = Package, ["serveRoot"] = Package, ["pythonPath"] = python,
                    ["releaseVersion"] = OldTag, ["runtimeCuda"] = "13.0", ["context"] = "8192",
                },
            };
            config.Engines["llamacpp"] = new EngineSettings
            {
                ExecutablePath = llama, Parameters = new() { ["runtimeCuda"] = "13.4", ["context"] = "4096" },
                // Same-version UpdateAsync retries retention. Protect every sibling test/cache,
                // including orphan receipts, while still validating this fixture's real ownership.
                ModelPresets = new()
                {
                    [Path.Combine(_work, "protected-fixtures.gguf")] = new()
                    { ["packageRoot"] = Path.Combine(full, "engines"), ["serveRoot"] = Path.Combine(full, "downloads") },
                },
            };
            config.Engines["kvmem"] = new EngineSettings
            {
                ExecutablePath = kvmem, Parameters = new() { ["packageRoot"] = Path.Combine(_work, "kvmem"), ["context"] = "65536" },
            };
            config.Normalize();
            foreach (var file in Directory.EnumerateFiles(Package, "*", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories))
                .Concat(new[] { Cache, SourceCache }))
                _unchanged[file] = File.ReadAllBytes(file);
            // If saving fails, restore even though construction never completed.
            try { new ConfigStore(ConfigPath).Save(config); }
            catch { Dispose(); throw; }

            EngineAcquisitionService.DetectComputeCapability = () => 8.6;
            EngineAcquisitionService.DetectCudaVersion = () => throw new Exception("reuse must not fetch a new CUDA runtime");
            EngineAcquisitionService.ReleasesFetcher = (repo, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Equal(CanonicalRepo, repo);
                ReleaseCalls++;
                return Task.FromResult<JsonArray?>(new JsonArray(ReleaseNode(OldTag)));
            };
            EngineAcquisitionService.ArchiveFetcher = (_, _, _, _) => { ArchiveCalls++; throw new Exception("legacy same-version reuse attempted acquisition"); };
            EngineAcquisitionService.RuntimeWheelFetcher = (_, _) => throw new Exception("fixture must not fetch wheels");
            EngineAcquisitionService.SetupProcessRunner = (_, _, _) => throw new Exception("fixture must not create venv/run pip");
            BuiltInInstallation.NativeProbe = (start, timeout, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                NativeCalls++;
                Equal("-1", start.Environment["CUDA_VISIBLE_DEVICES"]);
                Check(timeout <= TimeSpan.FromSeconds(20), "unbounded fixture probe");
                if (PathIdentity.Equals(start.FileName, Executable))
                {
                    Check(start.ArgumentList.SequenceEqual(new[] { "--help" }), "metadata identity fell back to a native version/model probe");
                    return Task.FromResult(new BuiltInInstallation.ProbeOutput(2, "usage: strata-poly --max-context N"));
                }
                if (PathIdentity.Equals(start.FileName, python))
                {
                    Check(start.ArgumentList.Contains("-B") && start.ArgumentList.Contains("-c") && PathIdentity.Equals(start.WorkingDirectory, Package),
                        "Python probe did not target the legacy private source/runtime");
                    return Task.FromResult(new BuiltInInstallation.ProbeOutput(0, "PolyStrata imports OK"));
                }
                throw new Exception("unexpected process/other-backend probe: " + start.FileName);
            };
        }

        public void AddOwnershipFixture()
        {
            var metadata = BuiltInInstallation.ReadReleaseMetadata(Package)!;
            Equal(0, EngineVersionRetention.WriteOwnedManifest(EngineRoot, new()
            {
                BackendId = "polystrata", Version = metadata.Version, Architecture = "x64", Root = Package,
                InstalledAtUtc = new DateTimeOffset(metadata.InstalledUtc),
                Downloads = metadata.Downloads.Select(d => new EngineVersionRetention.OwnedDownload(_cacheDirectory, d.DownloadPath, d.Sha256, d.Length)).ToArray(),
            }, DownloadRoot).Count);
            foreach (var file in new[] { Path.Combine(Package, EngineVersionRetention.ManifestFileName),
                Cache + EngineVersionRetention.DownloadManifestSuffix, SourceCache + EngineVersionRetention.DownloadManifestSuffix })
                _unchanged[file] = File.ReadAllBytes(file);
        }

        public void AssertUnchanged()
        {
            foreach (var (path, bytes) in _unchanged)
                Check(File.Exists(path) && bytes.SequenceEqual(File.ReadAllBytes(path)), "migration rewrote installed provenance/ownership/payload: " + path);
        }

        public void Dispose()
        {
            EngineAcquisitionService.ReleasesFetcher = _releases;
            EngineAcquisitionService.DetectComputeCapability = _capability;
            EngineAcquisitionService.DetectCudaVersion = _driver;
            EngineAcquisitionService.ArchiveFetcher = _archives;
            EngineAcquisitionService.RuntimeWheelFetcher = _wheels;
            EngineAcquisitionService.SetupProcessRunner = _setup;
            BuiltInInstallation.NativeProbe = _native;
            try { Restore(ConfigPath, _config); Restore(_backupPath, _backup); }
            finally
            {
                Directory.Delete(Package, recursive: true);
                Directory.Delete(_work, recursive: true);
                File.Delete(Cache);
                File.Delete(Cache + EngineVersionRetention.DownloadManifestSuffix);
                File.Delete(SourceCache);
                File.Delete(SourceCache + EngineVersionRetention.DownloadManifestSuffix);
                if (!_cacheDirectoryExisted && !Directory.EnumerateFileSystemEntries(_cacheDirectory).Any()) Directory.Delete(_cacheDirectory);
            }
        }

        private static string Write(string directory, string relative, string text)
        {
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }
        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        private static void Restore(string path, byte[]? bytes)
        {
            if (bytes == null) File.Delete(path);
            else File.WriteAllBytes(path, bytes);
        }
    }
}
