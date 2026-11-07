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

/// <summary>Real small ZIPs and SHA-256, with all network/native probes replaced by bounded test seams.</summary>
internal static class KvmemAcquisitionRegression
{
    private const string Tag = "v0.19.2";
    private const string AssetName = "kvmem-v0.19.2-windows-x64-nvidia-30-unified.zip";
    private const string Capabilities = """
        {"schema_version":1,"backend":"polystrata","query_scoring_revision":2,
         "independent_graph_lrfu":true,"signed_lrfu_hits":true,
         "block_tokens":{"default":32,"supported":[32,64],"startup_fixed":true}}
        """;

    public static async Task RunInstallTransactionsAsync(string root)
    {
        AssertIsolatedRoot(root);
        var work = NewWork(root, "transactions");
        var engineRoot = EngineCatalog.EngineRoot(root, "kvmem");
        var originalDirectories = DirectorySnapshot(engineRoot);
        var original = ConfigService.ReadConfig();
        var configPath = Path.Combine(root, "llama-manager.config.json");
        var store = new ConfigStore(configPath);
        var backupPath = configPath + ".bak";
        var originalBackup = File.Exists(backupPath) ? File.ReadAllBytes(backupPath) : null;
        using var hooks = new HookScope();
        try
        {
            var modelA = WriteFile(work, "models/selected A.gguf", Fixtures.StrataGguf());
            var modelB = WriteFile(work, "models/concurrently selected B.gguf", Fixtures.StrataGguf(mtp: true));
            var oldBundle = Path.Combine(work, "previous installation");
            var seed = SeedConfig(work, modelA, oldBundle);
            store.Save(seed);
            var archive = CompleteArchive(work, "complete");
            ManagerConfig? expectedAtCommit = null;
            var concurrentEditDone = false;
            var beforeDefault = KvmemInstallation.DefaultPackageRoot(root);
            var counters = hooks.Configure(root, archive, checksumOnly: true, observe: async (start, ct) =>
            {
                AssertPendingExcluded(root, start, beforeDefault);
                if (!start.ArgumentList.Contains("--kvmem-capabilities") || concurrentEditDone) return;
                concurrentEditDone = true;
                // A deterministic concurrent UI transaction completes after download/extraction,
                // but before the install's path-only commit. It must not be overwritten by a stale snapshot.
                await Task.Run(() =>
                {
                    var latest = ConfigService.ReadConfig();
                    var settings = latest.Engines["kvmem"];
                    settings.ModelPath = modelB;
                    settings.LastStats = "newer concurrent stats";
                    settings.ModelPresets[PathIdentity.Normalize(modelB)] = new() { ["kvmemBudget"] = "4096", ["note"] = "newer model preset" };
                    ConfigService.SaveEngineSettings(latest, "kvmem", settings);
                    ConfigService.SaveEngineParameters("kvmem", new Dictionary<string, string>
                    {
                        ["kvmemBudget"] = "16384", ["concurrentSetting"] = "newer UI value",
                    });
                    ConfigService.SaveEngineParameters("polystrata", new Dictionary<string, string> { ["tailSkip"] = "2" });
                    ConfigService.SaveActiveBackend(latest, "llamacpp");
                    ConfigService.SaveSessionSlot(latest, "kvmem", Session(work, modelB, int.MaxValue - 3));
                }, ct);
                expectedAtCommit = ConfigService.ReadConfig();
            });
            var stageSeen = false;
            var log = new DelegateLogSink(_ =>
            {
                if (!Directory.Exists(engineRoot)) return;
                foreach (var stage in Directory.EnumerateDirectories(engineRoot, ".install-*"))
                {
                    stageSeen = true;
                    Check(!PathIdentity.Equals(stage, KvmemInstallation.DefaultPackageRoot(root)), "unpublished staging folder became the default package");
                }
            });
            var first = await EngineAcquisitionService.AcquireAsync("kvmem", log, CancellationToken.None);
            Check(stageSeen && concurrentEditDone && expectedAtCommit != null, "successful install missed its staging/concurrent-edit boundary");
            Equal(1, counters.ReleaseCalls);
            Equal(1, counters.ChecksumCalls);
            Equal(1, counters.ArchiveCalls);
            Equal(3, counters.RuntimeCalls);
            AssertNarrowCommit(expectedAtCommit!, ConfigService.ReadConfig(), first);
            Check(KvmemInstallation.Validate(first, 86, Tag).Ok, "installed synthetic bundle failed final layout validation");
            Check(!File.Exists(Path.Combine(first, KvmemInstallation.PendingMarker)), "successful commit retained its pending marker");
            Equal(archive.NativeDigest, await EngineDownloadService.Sha256Async(Path.Combine(first, KvmemInstallation.WorkerRelativePath), CancellationToken.None));

            var sentinel = WriteFile(first, "user-sentinel.txt", "immutable original version");
            var originalWorker = File.ReadAllBytes(Path.Combine(first, KvmemInstallation.WorkerRelativePath));
            var secondExpected = ConfigService.ReadConfig();
            beforeDefault = KvmemInstallation.DefaultPackageRoot(root);
            counters = hooks.Configure(root, archive, observe: (start, _) =>
            {
                AssertPendingExcluded(root, start, beforeDefault);
                return Task.CompletedTask;
            });
            var second = await EngineAcquisitionService.AcquireAsync("kvmem", new DelegateLogSink(_ => { }), CancellationToken.None);
            Check(!PathIdentity.Equals(first, second), "same-version install overwrote the existing installation directory");
            Equal("immutable original version", File.ReadAllText(sentinel));
            Check(originalWorker.SequenceEqual(File.ReadAllBytes(Path.Combine(first, KvmemInstallation.WorkerRelativePath))), "same-version repair changed the original worker");
            Check(!File.Exists(Path.Combine(second, "user-sentinel.txt")), "same-version repair reused the old directory");
            AssertNarrowCommit(secondExpected, ConfigService.ReadConfig(), second);

            async Task Refuses<T>(SyntheticArchive input, Action<HookCounters>? alter = null,
                Func<ProcessStartInfo, CancellationToken, Task>? observe = null, bool checksumOnly = false,
                string? digestOverride = null, string? checksumText = null, CancellationToken token = default) where T : Exception
            {
                var before = ConfigService.ReadConfig();
                var directories = DirectorySnapshot(engineRoot);
                var expectedDefault = KvmemInstallation.DefaultPackageRoot(root);
                var current = hooks.Configure(root, input, checksumOnly, digestOverride, checksumText, async (start, ct) =>
                {
                    AssertPendingExcluded(root, start, expectedDefault);
                    if (observe != null) await observe(start, ct);
                });
                alter?.Invoke(current);
                await ExpectThrowsAsync<T>(() => EngineAcquisitionService.AcquireAsync("kvmem", new DelegateLogSink(_ => { }), token));
                if (current.ExpectNoCalls)
                    Check(current.ReleaseCalls == 0 && current.ChecksumCalls == 0 && current.ArchiveCalls == 0 && current.RuntimeCalls == 0,
                        "pre-cancelled installation invoked acquisition/runtime hooks");
                AssertUnchanged(before, ConfigService.ReadConfig());
                Check(directories.SetEquals(DirectorySnapshot(engineRoot)), "failed/cancelled install left a stage or uncommitted version directory");
                SamePath(expectedDefault, KvmemInstallation.DefaultPackageRoot(root), "failed install changed default discovery");
            }

            await Refuses<EngineDownloadService.ChecksumMismatchException>(archive, digestOverride: new string('0', 64));
            var unsafeArchive = CompleteArchive(work, "unsafe", extra: [new ZipMember("../escaped.txt", Bytes("escape"))]);
            await Refuses<IOException>(unsafeArchive);
            Check(!File.Exists(Path.Combine(root, "engines", "escaped.txt")), "unsafe package escaped its extraction root");
            var missing = CompleteArchive(work, "missing server", omit: KvmemInstallation.ServerRelativePath);
            await Refuses<InvalidDataException>(missing);
            await Refuses<InvalidDataException>(archive, observe: (_, _) => throw new IOException("fixture runtime dependency failure"));
            await Refuses<InvalidOperationException>(archive, checksumOnly: true, checksumText: archive.Digest + "  wrong-asset.zip");
            await Refuses<IOException>(archive, alter: c => c.FailChecksumFetch = true, checksumOnly: true);
            await Refuses<IOException>(archive, alter: c => c.FailArchiveFetch = true);
            using (var preCancelled = new CancellationTokenSource())
            {
                preCancelled.Cancel();
                await Refuses<OperationCanceledException>(archive, alter: c => c.ExpectNoCalls = true, token: preCancelled.Token);
            }
            using (var beforeCommit = new CancellationTokenSource())
            {
                var finalProbeSeen = false;
                await Refuses<OperationCanceledException>(archive, observe: (start, _) =>
                {
                    if (start.ArgumentList.Contains("--kvmem-capabilities"))
                    {
                        finalProbeSeen = true;
                        beforeCommit.Cancel(); // relocated bundle exists, but the settings commit has not run
                    }
                    return Task.CompletedTask;
                }, token: beforeCommit.Token);
                Check(finalProbeSeen, "commit-boundary cancellation happened before the relocated runtime handshake");
            }
        }
        finally
        {
            hooks.Dispose();
            try
            {
                store.Save(original);
                if (originalBackup != null) File.WriteAllBytes(backupPath, originalBackup);
                else if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            finally { CleanupNewInstallations(root, engineRoot, originalDirectories); }
        }
    }

    public static async Task RunSafeExtractionAsync(string root)
    {
        AssertIsolatedRoot(root);
        var work = NewWork(root, "safe extraction");
        var complete = CompleteArchive(work, "wrapped complete");
        var destination = Path.Combine(work, "new destination");
        var count = await EngineAcquisitionService.ExtractKvmemBundleAsync(complete.Path, destination, new DelegateLogSink(_ => { }));
        Equal(complete.Members.Count, count);
        Check(KvmemInstallation.Validate(destination, 86, Tag).Ok, "flattened synthetic bundle did not retain its complete layout");
        Check(!Directory.Exists(Path.Combine(destination, "release-wrapper")), "outer ZIP wrapper was not removed");
        Equal("extra UI component", File.ReadAllText(Path.Combine(destination, "ui", "dist", "index.html")));
        var flat = CreateArchive(work, "already flat", complete.Members, wrapper: false);
        var flatDestination = Path.Combine(work, "flat destination");
        Equal(flat.Members.Count, await EngineAcquisitionService.ExtractKvmemBundleAsync(flat.Path, flatDestination, new DelegateLogSink(_ => { })));
        Check(KvmemInstallation.Validate(flatDestination, 86, Tag).Ok, "already-flat unified bundle was damaged by flattening");

        var cases = new (string Label, ZipMember[] Members)[]
        {
            ("parent traversal", [Member("good.txt"), Member("../escape.txt")]),
            ("nested traversal", [Member("good.txt"), Member("a/../../escape.txt")]),
            ("backslash traversal", [Member("good.txt"), Member("a\\..\\..\\escape.txt")]),
            ("absolute path", [Member("good.txt"), Member("/absolute.txt")]),
            ("drive path", [Member("good.txt"), Member("C:/absolute.txt")]),
            ("UNC path", [Member("good.txt"), Member("\\\\server\\share\\absolute.txt")]),
            ("ADS", [Member("good.txt"), Member("file.txt:stream")]),
            ("trailing dot", [Member("good.txt"), Member("file.txt.")]),
            ("trailing space", [Member("good.txt"), Member("directory /file.txt")]),
            ("exact duplicate", [Member("same.txt"), Member("same.txt")]),
            ("case duplicate", [Member("same.txt"), Member("SAME.TXT")]),
            ("directory duplicate", [new ZipMember("same/", []), new ZipMember("SAME/", [])]),
            ("Unix symlink", [Member("good.txt"), new ZipMember("link", Bytes("../outside"), unchecked((int)0xa1ff0000u))]),
            ("Windows reparse", [Member("good.txt"), new ZipMember("junction", Bytes("outside"), (int)FileAttributes.ReparsePoint)]),
            ("reserved pending marker", [Member("good.txt"), Member(KvmemInstallation.PendingMarker)]),
        };
        foreach (var (label, members) in cases)
        {
            // A good first member proves preflight checks inspect the entire archive
            // before writing even its harmless first file.
            var bad = CreateArchive(work, label, members, wrapper: false);
            var rejected = Path.Combine(work, "rejected-" + Guid.NewGuid().ToString("N"));
            await ExpectThrowsAsync<IOException>(() => EngineAcquisitionService.ExtractKvmemBundleAsync(bad.Path, rejected, new DelegateLogSink(_ => { })));
            Check(!Directory.Exists(rejected) || !Directory.EnumerateFileSystemEntries(rejected).Any(), "unsafe archive wrote members before preflight rejection: " + label);
        }
        var sentinel = WriteFile(destination, "keep.txt", "do not overwrite");
        await ExpectThrowsAsync<IOException>(() => EngineAcquisitionService.ExtractKvmemBundleAsync(complete.Path, destination, new DelegateLogSink(_ => { })));
        Equal("do not overwrite", File.ReadAllText(sentinel));
        var onlyMarker = Path.Combine(work, "not-empty pending destination");
        WriteFile(onlyMarker, KvmemInstallation.PendingMarker, "not owned by the public extractor");
        await ExpectThrowsAsync<IOException>(() => EngineAcquisitionService.ExtractKvmemBundleAsync(complete.Path, onlyMarker, new DelegateLogSink(_ => { })));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledDestination = Path.Combine(work, "pre-cancelled destination");
        await ExpectThrowsAsync<OperationCanceledException>(() => EngineAcquisitionService.ExtractKvmemBundleAsync(complete.Path, cancelledDestination, new DelegateLogSink(_ => { }), cancelled.Token));
        Check(!Directory.Exists(cancelledDestination), "pre-cancelled extraction created a destination");
    }

    private sealed record ZipMember(string Name, byte[] Data, int Attributes = 0);
    private sealed record SyntheticArchive(string Path, string Digest, string NativeDigest, IReadOnlyList<ZipMember> Members);

    private static SyntheticArchive CompleteArchive(string work, string label, string? omit = null, ZipMember[]? extra = null)
    {
        var files = KvmemInstallation.RequiredRelativeFiles.ToDictionary(p => p, p => Bytes("fixture " + p));
        var native = Bytes("synthetic PolyStrata KVMem worker v0.19.2");
        files[KvmemInstallation.WorkerRelativePath] = native;
        files[KvmemInstallation.VersionRelativePath] = Bytes(JsonSerializer.Serialize(new { version = "0.19.2", supported_backends = new[] { "ninfer", "polystrata" } }));
        files[KvmemInstallation.ArchitectureRelativePath] = Bytes(JsonSerializer.Serialize(new { architecture = 86, polystrata = new { worker_sha256 = Hash(native) } }));
        files["runtime/cuda/cublas64_13.dll"] = Bytes("synthetic cuBLAS");
        files["runtime/cuda/cublasLt64_13.dll"] = Bytes("synthetic cuBLASLt");
        files["ui/dist/index.html"] = Bytes("extra UI component");
        if (omit != null) files.Remove(omit);
        var members = files.Select(p => new ZipMember(p.Key, p.Value)).Concat(extra ?? []).ToArray();
        return CreateArchive(work, label, members) with { NativeDigest = Hash(native) };
    }

    private static SyntheticArchive CreateArchive(string work, string label, IReadOnlyList<ZipMember> members, bool wrapper = true)
    {
        var path = Path.Combine(work, label + "-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var member in members)
            {
                var entry = zip.CreateEntry((wrapper ? "release-wrapper/" : "") + member.Name, CompressionLevel.Fastest);
                entry.ExternalAttributes = member.Attributes;
                if (member.Name.EndsWith('/')) continue;
                using var stream = entry.Open();
                stream.Write(member.Data);
            }
        }
        return new(path, Hash(File.ReadAllBytes(path)), "", members);
    }

    private static ZipMember Member(string name) => new(name, Bytes("member"));
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ManagerConfig SeedConfig(string work, string model, string oldBundle)
    {
        var config = new ManagerConfig
        {
            ActiveBackendId = "polystrata", ModelPath = model, Args = "--port 8080 -c 4096 --user-main-option preserved",
            ModelsDirectory = Path.GetDirectoryName(model)!, AutoStartEnabled = true, AutoOpenBrowser = true,
            CreatedAt = "2026-11-01T00:00:00Z", UpdatedAt = "2026-11-01T00:00:00Z",
        };
        config.ModelPresets[PathIdentity.Normalize(model)] = new ModelPreset { Args = "-c 8192", Signature = "main preset signature" };
        config.Engines["kvmem"] = new EngineSettings
        {
            ModelPath = model, ExecutablePath = Path.Combine(work, "user executable.exe"), Port = 8199, LastStats = "old stats",
            Parameters = new()
            {
                ["packageRoot"] = oldBundle, ["serveRoot"] = Path.Combine(oldBundle, "backends", "polystrata"),
                ["bundlePython"] = Path.Combine(oldBundle, "runtime", "python", "python.exe"),
                ["pythonPath"] = Path.Combine(work, "user Python.exe"), ["kvmemBudget"] = "8192", ["userSetting"] = "preserved",
            },
            ModelPresets = new() { [PathIdentity.Normalize(model)] = new() { ["kvmemBudget"] = "2048", ["note"] = "original preset" } },
        };
        config.Engines["polystrata"] = new EngineSettings { ModelPath = model, Parameters = new() { ["tailSkip"] = "6", ["packageRoot"] = Path.Combine(work, "stock poly") } };
        config.SetRunning(ManagerConfig.SlotMain, Session(work, model, int.MaxValue - 1));
        config.SetRunning("polystrata", Session(work, model, int.MaxValue - 2));
        config.SetRunning("kvmem", Session(work, model, int.MaxValue - 4));
        config.Normalize();
        return config;
    }

    private static ServerSession Session(string work, string model, int pid) => new()
    {
        ProcessId = pid, StartTimeUtcTicks = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
        EnginePath = Path.Combine(work, "never-started fixture.exe"), ModelPath = model, Port = 8199, Args = "fixture identity",
    };

    private static void AssertNarrowCommit(ManagerConfig expected, ManagerConfig actual, string installed)
    {
        var parameters = actual.Engines["kvmem"].Parameters;
        SamePath(installed, parameters.GetValueOrDefault("packageRoot"), "install did not commit its package root");
        SamePath(Path.Combine(installed, KvmemInstallation.ServeRootRelativePath), parameters.GetValueOrDefault("serveRoot"), "install did not commit its prepared serve path");
        SamePath(Path.Combine(installed, KvmemInstallation.PythonRelativePath), parameters.GetValueOrDefault("bundlePython"), "install did not commit bundled Python metadata");
        JsonObject Projection(ManagerConfig config)
        {
            var node = JsonSerializer.SerializeToNode(config)!.AsObject();
            node.Remove("UpdatedAt");
            var values = node["Engines"]!["kvmem"]!["Parameters"]!.AsObject();
            foreach (var key in new[] { "packageRoot", "serveRoot", "bundlePython" }) values.Remove(key);
            return node;
        }
        Check(JsonNode.DeepEquals(Projection(expected), Projection(actual)), "install path commit overwrote model/active backend/presets/session/concurrent user settings");
    }

    private static void AssertUnchanged(ManagerConfig expected, ManagerConfig actual) =>
        Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected), JsonSerializer.SerializeToNode(actual)), "failed/cancelled install changed persisted settings or sessions");

    private static void AssertPendingExcluded(string managerRoot, ProcessStartInfo start, string? expectedDefault)
    {
        var bundle = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(start.FileName)!, "..", ".."));
        Check(File.Exists(Path.Combine(bundle, KvmemInstallation.PendingMarker)), "relocated package was discoverable before its settings commit");
        Check(!PathIdentity.Equals(bundle, KvmemInstallation.DefaultPackageRoot(managerRoot)), "pending relocated package became the default installation");
        SamePath(expectedDefault, KvmemInstallation.DefaultPackageRoot(managerRoot), "pending package displaced the prior default");
    }

    private static HashSet<string> DirectorySnapshot(string directory) => !Directory.Exists(directory) ? new(StringComparer.OrdinalIgnoreCase) :
        Directory.EnumerateDirectories(directory).Select(PathIdentity.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void CleanupNewInstallations(string managerRoot, string engineRoot, HashSet<string> original)
    {
        AssertIsolatedRoot(managerRoot);
        if (!Directory.Exists(engineRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(engineRoot).ToArray())
        {
            if (original.Contains(PathIdentity.Normalize(directory))) continue;
            var full = Path.GetFullPath(directory);
            Check(PathIdentity.Equals(Path.GetDirectoryName(full), engineRoot) &&
                (Path.GetFileName(full).StartsWith("v0.19.2-nvidia-30", StringComparison.Ordinal) || Path.GetFileName(full).StartsWith(".install-", StringComparison.Ordinal)),
                "refusing to clean an unrelated installation directory");
            Directory.Delete(full, recursive: true);
        }
    }

    private static void AssertIsolatedRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var name = Path.GetFileName(full);
        Check(PathIdentity.Equals(full, ConfigService.GetManagerRoot()) && name.StartsWith("regression-", StringComparison.Ordinal) &&
            Guid.TryParseExact(name[(name.LastIndexOf('-') + 1)..], "N", out _), "install regression requires the console runner's isolated root; never modify a real installation");
    }

    private static string NewWork(string root, string label)
    {
        var work = Path.Combine(root, "kvmem-acquisition", label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    private static string WriteFile(string root, string relative, string text) => WriteFile(root, relative, Bytes(text));
    private static string WriteFile(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void SamePath(string? expected, string? actual, string message) =>
        Check((expected == null && actual == null) || PathIdentity.Equals(expected, actual), message + ": " + actual);

    private static async Task ExpectThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class HookCounters
    {
        public int ReleaseCalls, ChecksumCalls, ArchiveCalls, RuntimeCalls;
        public bool FailChecksumFetch, FailArchiveFetch, ExpectNoCalls;
    }

    private sealed class HookScope : IDisposable
    {
        private readonly Func<string, CancellationToken, Task<JsonArray?>> _releases = EngineAcquisitionService.ReleasesFetcher;
        private readonly Func<ReleaseAsset, string, ILogSink, CancellationToken, Task<string>> _archives = EngineAcquisitionService.ArchiveFetcher;
        private readonly Func<ReleaseAsset, CancellationToken, Task<string>> _checksums = EngineAcquisitionService.ChecksumFetcher;
        private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<string>> _runtime = KvmemInstallation.RuntimeProbe;
        private readonly Func<double> _capability = EngineAcquisitionService.DetectComputeCapability;

        public HookCounters Configure(string managerRoot, SyntheticArchive archive, bool checksumOnly = false,
            string? digestOverride = null, string? checksumText = null, Func<ProcessStartInfo, CancellationToken, Task>? observe = null)
        {
            var counters = new HookCounters();
            var digest = digestOverride ?? archive.Digest;
            EngineAcquisitionService.DetectComputeCapability = () => { Check(!counters.ExpectNoCalls, "pre-cancelled install probed hardware"); return 8.6; };
            EngineAcquisitionService.ReleasesFetcher = (repo, ct) =>
            {
                Check(!counters.ExpectNoCalls, "pre-cancelled install fetched releases");
                ct.ThrowIfCancellationRequested();
                counters.ReleaseCalls++;
                Equal(EngineCatalog.KvmemRepo, repo);
                var asset = new JsonObject { ["name"] = AssetName, ["browser_download_url"] = "https://fixture.invalid/" + AssetName };
                if (!checksumOnly) asset["digest"] = "sha256:" + digest;
                var assets = new JsonArray(asset);
                if (checksumOnly) assets.Add(new JsonObject { ["name"] = AssetName + ".sha256", ["browser_download_url"] = "https://fixture.invalid/checksum" });
                return Task.FromResult<JsonArray?>(new JsonArray(new JsonObject { ["tag_name"] = Tag, ["assets"] = assets }));
            };
            EngineAcquisitionService.ChecksumFetcher = (asset, ct) =>
            {
                Check(!counters.ExpectNoCalls, "pre-cancelled install fetched a checksum");
                ct.ThrowIfCancellationRequested();
                counters.ChecksumCalls++;
                if (counters.FailChecksumFetch) throw new IOException("fixture checksum network failure");
                Equal(AssetName + ".sha256", asset.Name);
                return Task.FromResult(checksumText ?? digest + "  " + AssetName + "\n");
            };
            EngineAcquisitionService.ArchiveFetcher = (asset, directory, _, ct) =>
            {
                Check(!counters.ExpectNoCalls, "pre-cancelled install fetched an archive");
                ct.ThrowIfCancellationRequested();
                counters.ArchiveCalls++;
                if (counters.FailArchiveFetch) throw new IOException("fixture archive network failure");
                Equal(AssetName, asset.Name);
                Equal(digest, asset.Sha256);
                SamePath(Path.Combine(managerRoot, "downloads", "kvmem"), directory, "archive fetch was routed outside the KVMem download cache");
                return Task.FromResult(archive.Path); // production independently hashes this real ZIP
            };
            KvmemInstallation.RuntimeProbe = async (start, timeout, ct) =>
            {
                Check(!counters.ExpectNoCalls, "pre-cancelled install ran a runtime probe");
                ct.ThrowIfCancellationRequested();
                counters.RuntimeCalls++;
                Check(timeout > TimeSpan.Zero && timeout <= TimeSpan.FromSeconds(20), "runtime verification was not bounded");
                Equal("-1", start.Environment["CUDA_VISIBLE_DEVICES"]);
                Check(!start.ArgumentList.Contains("--serve") && !start.ArgumentList.Contains("--model"), "install attempted GPU/model startup");
                if (observe != null) await observe(start, ct);
                return start.ArgumentList.Contains("--kvmem-capabilities") ? Capabilities :
                    start.ArgumentList.Contains("--help") ? "--kvmem-budget --kvmem-history-reuse" : "bundled imports OK";
            };
            return counters;
        }

        public void Dispose()
        {
            EngineAcquisitionService.ReleasesFetcher = _releases;
            EngineAcquisitionService.ArchiveFetcher = _archives;
            EngineAcquisitionService.ChecksumFetcher = _checksums;
            KvmemInstallation.RuntimeProbe = _runtime;
            EngineAcquisitionService.DetectComputeCapability = _capability;
        }
    }
}
