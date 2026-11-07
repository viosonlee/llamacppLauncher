using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

/// <summary>
/// Model/GPU/download-free checks for the actual v0.19.1 PolyStrata launch contract.
/// A generated GGUF and a tiny complete-looking unified bundle exercise the launcher;
/// none of the fixture executables are started and no real installation is changed.
/// </summary>
internal static class KvmemRegression
{
    private const string Rtx30Archive = "kvmem-v0.19.1-windows-x64-nvidia-30-unified.zip";
    private const string Rtx30Digest = "9dbfdcf1e893cd60ba05a7f8b5a60522a472c1a2a5772bcbcfcf6a2650c0ea9c";
    private const string NativeCapabilities = """
        {"schema_version":1,"backend":"polystrata","query_scoring_revision":2,
         "independent_graph_lrfu":true,"signed_lrfu_hits":true,
         "block_tokens":{"default":32,"supported":[32,64],"startup_fixed":true}}
        """;
    private static readonly string[] LegacyKeys =
        ["kvmem", "kvmemBudget", "kvmemGenReserve", "kvmemHostMib", "kvmemDiskMib", "kvmemDiskPath", "kvmemSessions"];

    public static void RunRegistryAndSchema()
    {
        var backend = BackendRegistry.ServerById("kvmem")
            ?? throw new Exception("the independent KVMem backend is not registered");
        Check(backend is StrataBackend, "KVMem is not registered as its own Strata profile");
        Check(backend.Descriptor.BuiltIn && backend.Descriptor.GpuHeavy, "KVMem must be a built-in GPU-heavy backend");
        Equal(1, BackendRegistry.All().Count(b => b.Descriptor.Id == "kvmem"));
        Equal("llamacpp", BackendRegistry.Default.Descriptor.Id);
        Equal("kvmem", StrataBackend.KvmemProfile.Id);
        Equal("qwen35moe", StrataBackend.KvmemProfile.Arch);
        Equal("strata-poly.exe", StrataBackend.KvmemProfile.EngineExeName);
        Equal("workers/polystrata", StrataBackend.KvmemProfile.EngineSubdir.Replace('\\', '/'));

        foreach (var stock in new[] { "llamacpp", "polystrata" })
        {
            var registered = BackendRegistry.ById(stock)!;
            Check(!registered.Parameters.Any(p => p.Key.StartsWith("kvmem", StringComparison.OrdinalIgnoreCase)),
                stock + " still advertises the unsupported KVMem panel");
        }
        foreach (var key in LegacyKeys)
            Check(backend.Parameters.Count(p => p.Key == key) == 1, "missing/duplicate KVMem parameter: " + key);
        Check(!backend.Parameters.Any(p => p.Key is "draftModel" or "conversationCache"),
            "KVMem must not expose the conflicting stock draft/conversation-cache controls");
        Equal("1", backend.Parameters.Single(p => p.Key == "kvmem").Default);
        Equal("8192", backend.Parameters.Single(p => p.Key == "kvmemBudget").Default);
        Equal("512", backend.Parameters.Single(p => p.Key == "kvmemGenReserve").Default);
        Equal("1024", backend.Parameters.Single(p => p.Key == "kvmemHostMib").Default);
        Equal("2", backend.Parameters.Single(p => p.Key == "kvmemSessions").Default);
        Equal("128", backend.Parameters.Single(p => p.Key == "promptBatch").Default);
        Equal("128", backend.Parameters.Single(p => p.Key == "stageMib").Default);
        Equal("65536", backend.Parameters.Single(p => p.Key == "context").Default);
        Equal("q8_0", backend.Parameters.Single(p => p.Key == "kv").Default);
        Equal("0", backend.Parameters.Single(p => p.Key == "tensorCoreMma").Default);
        Check(backend.Parameters.Single(p => p.Key == "kv").Choices!.All(c => c.Value is "q8_0" or "fp16"),
            "KVMem advertises a KV type the native adapter does not support");

        var values = backend.Parameters.ToDictionary(p => p.Key, p => p.Default ?? "");
        var budget = backend.Parameters.Single(p => p.Key == "kvmemBudget");
        var diskPath = backend.Parameters.Single(p => p.Key == "kvmemDiskPath");
        Check(ParameterVisibility.Evaluate(budget.VisibleWhen, new ParameterContext("kvmem", null, values)),
            "the enabled KVMem budget control is hidden");
        Check(!ParameterVisibility.Evaluate(diskPath.VisibleWhen, new ParameterContext("kvmem", null, values)),
            "SSD path must be hidden when the disk quota is zero");
        values["kvmemDiskMib"] = "256";
        Check(ParameterVisibility.Evaluate(diskPath.VisibleWhen, new ParameterContext("kvmem", null, values)),
            "SSD path did not become visible with a positive disk quota");
        values["kvmem"] = "0";
        Check(!ParameterVisibility.Evaluate(budget.VisibleWhen, new ParameterContext("kvmem", null, values)),
            "disabled KVMem must hide its dependent controls");
        Check(!ParameterVisibility.Evaluate(diskPath.VisibleWhen, new ParameterContext("kvmem", null, values)),
            "disabled KVMem must hide its SSD path even with a stale disk quota");
    }

    public static void RunCatalog(string root)
    {
        Equal("kvmem/kvmem", EngineCatalog.KvmemRepo);
        Equal(Rtx30Digest, KvmemInstallation.PinnedRtx30Sha256);
        var families = new (double Capability, int Architecture, string Family)[]
        {
            (7.0, 70, "v100"), (7.5, 75, "20"), (8.6, 86, "30"), (8.9, 89, "40"), (12.0, 120, "50"),
        };
        var assets = families.Select(f => new ReleaseAsset(
            $"kvmem-v0.19.1-windows-x64-nvidia-{f.Family}-unified.zip",
            $"https://github.com/kvmem/kvmem/releases/download/v0.19.1/kvmem-v0.19.1-windows-x64-nvidia-{f.Family}-unified.zip",
            new string('a', 64))).ToList();
        assets.Insert(0, new ReleaseAsset("kvmem-v0.19.1-linux-x64-nvidia-30-unified.zip", "https://invalid/linux.zip", new string('b', 64)));
        assets.Add(new ReleaseAsset("kvmem-v0.19.1-windows-x64-nvidia-30.zip", "https://invalid/lightweight.zip", new string('c', 64)));
        assets.Add(new ReleaseAsset(Rtx30Archive + ".sha256", "https://invalid/checksum", null));
        var release = new ReleaseInfo("v0.19.1", assets);
        foreach (var (capability, architecture, family) in families)
        {
            Equal<int?>(architecture, EngineCatalog.KvmemArchitecture(capability));
            Equal(family, EngineCatalog.KvmemGpuFamily(architecture));
            var plan = EngineCatalog.KvmemPlan(release, root, capability);
            Check(plan != null, "no unified plan for exact sm" + architecture);
            Equal("kvmem", plan!.BackendId);
            Equal(1, plan.EngineArchives.Count);
            Equal(1, plan.AllArchives.Count);
            Equal($"kvmem-v0.19.1-windows-x64-nvidia-{family}-unified.zip", plan.EngineArchives.Single().Name);
            Equal(Path.Combine(root, "engines", "kvmem", $"v0.19.1-nvidia-{family}"), plan.EngineDir);
            Equal<int?>(architecture, plan.KvmemArchitecture);
            Check(plan.SourceArchive == null && plan.SourceDir == null, "unified bundle must not fetch a second source archive");
            if (architecture == 86)
            {
                Equal(Rtx30Digest, plan.EngineArchives.Single().Sha256);
                Equal(Rtx30Archive + ".sha256", plan.ChecksumAsset!.Name);
            }
        }
        foreach (var unsupported in new[] { 0d, -1d, 6.1, 8.0, 8.65, 9.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Check(EngineCatalog.KvmemArchitecture(unsupported) == null, "unknown/non-exact capability was guessed: " + unsupported);
            Check(EngineCatalog.KvmemPlan(release, root, unsupported) == null, "unsupported GPU got an RTX-family bundle");
        }
        foreach (var tag in new[] { "", "../v0.19.1", "v0.19.1/../../escape", "latest" })
            Check(EngineCatalog.KvmemPlan(release with { Tag = tag }, root, 8.6) == null, "unsafe/unversioned tag accepted: " + tag);
        Check(EngineCatalog.KvmemPlan(new ReleaseInfo("v0.19.1", [assets[0]]), root, 8.6) == null,
            "Linux/lightweight asset was treated as the Windows full bundle");

        // v0.19.1/sm86 has a pinned known digest, so missing-hash rejection is tested
        // on another architecture and a future version rather than contradicting that pin.
        var futureName = "kvmem-v0.19.2-windows-x64-nvidia-30-unified.zip";
        var future = new ReleaseInfo("v0.19.2", [new ReleaseAsset(futureName, "https://invalid/future.zip", null)]);
        Check(EngineCatalog.KvmemPlan(future, root, 8.6) == null, "unverified future bundle accepted");
        Check(EngineCatalog.KvmemPlan(future with { Assets = [future.Assets[0] with { Sha256 = "junk" }] }, root, 8.6) == null,
            "invalid checksum metadata was accepted");
        var verified = EngineCatalog.KvmemPlan(future with { Assets = [future.Assets[0] with { Sha256 = new string('D', 64) }] }, root, 8.6);
        Equal(new string('d', 64), verified!.EngineArchives.Single().Sha256);
        var checksumOnly = EngineCatalog.KvmemPlan(future with
        {
            Assets = [future.Assets[0], new ReleaseAsset(futureName + ".sha256", "https://invalid/future.sha256", null)],
        }, root, 8.6);
        Check(checksumOnly != null && checksumOnly.ChecksumAsset != null && checksumOnly.EngineArchives.Single().Sha256 == null,
            "a matching checksum-file plan was not retained for verification before installation");
        var noHash40 = new ReleaseInfo("v0.19.1", [new ReleaseAsset("kvmem-v0.19.1-windows-x64-nvidia-40-unified.zip", "https://invalid/nohash40.zip", null)]);
        Check(EngineCatalog.KvmemPlan(noHash40, root, 8.9) == null, "the sm86 pinned digest leaked to a different GPU bundle");

        var hash = new string('e', 64);
        Equal(hash, EngineCatalog.ParseChecksum(hash + "  " + futureName + "\n", futureName));
        Equal(hash, EngineCatalog.ParseChecksum(hash.ToUpperInvariant() + " *" + futureName + "\r\n", futureName));
        Equal(hash, EngineCatalog.ParseChecksum(hash + "\n", futureName));
        Check(EngineCatalog.ParseChecksum(hash + "  other.zip", futureName) == null, "another asset's checksum was reused");
        Check(EngineCatalog.ParseChecksum(hash + "  " + futureName + "\n" + new string('f', 64) + "  " + futureName, futureName) == null,
            "conflicting checksums for one asset were accepted");
        Check(EngineCatalog.ParseChecksum("sha256:not-a-checksum", futureName) == null, "malformed checksum text was accepted");

        var detect = EngineAcquisitionService.DetectComputeCapability;
        try
        {
            EngineAcquisitionService.DetectComputeCapability = () => 8.6;
            Check(EngineAcquisitionService.CanAcquire("kvmem"), "KVMem was not routed to the acquisition service");
            Equal(EngineCatalog.KvmemRepo, EngineAcquisitionService.RepoFor("kvmem"));
            Equal(Rtx30Archive, EngineAcquisitionService.PlanFor("kvmem", release, root)!.EngineArchives.Single().Name);
            EngineAcquisitionService.DetectComputeCapability = () => 0;
            Check(EngineAcquisitionService.PlanFor("kvmem", release, root) == null, "unknown hardware was silently assigned a package");
        }
        finally { EngineAcquisitionService.DetectComputeCapability = detect; }
    }

    public static void RunLayoutsAndResolvers(string root)
    {
        var fixture = CreateFixture(root, "layout");
        var complete = KvmemInstallation.Validate(fixture.Bundle, expectedArchitecture: 86, expectedVersion: "v0.19.1");
        Check(complete.Ok, "complete fake unified package rejected: " + complete.Detail);
        Equal("0.19.1", complete.Version);
        Equal<int?>(86, complete.Architecture);
        Check(!KvmemInstallation.Validate(fixture.Bundle, expectedArchitecture: 89).Ok, "cross-architecture package accepted");
        Check(!KvmemInstallation.Validate(fixture.Bundle, expectedVersion: "v0.19.2").Ok, "release/version mismatch accepted");
        foreach (var relative in KvmemInstallation.RequiredRelativeFiles)
        {
            var path = Path.Combine(fixture.Bundle, relative);
            var original = File.ReadAllText(path);
            try
            {
                File.WriteAllText(path, "");
                var invalid = KvmemInstallation.Validate(fixture.Bundle);
                Check(!invalid.Ok, "empty required unified asset accepted: " + relative);
            }
            finally { File.WriteAllText(path, original); }
        }
        foreach (var relative in new[] { "runtime/cuda/cublas64_13.dll", "runtime/cuda/cublasLt64_13.dll" })
        {
            var path = Path.Combine(fixture.Bundle, relative);
            var original = File.ReadAllText(path);
            File.Delete(path);
            try { Check(!KvmemInstallation.Validate(fixture.Bundle).Ok, "missing shared CUDA dependency accepted: " + relative); }
            finally { File.WriteAllText(path, original); }
        }
        var native = Path.Combine(fixture.Bundle, KvmemInstallation.WorkerRelativePath);
        var nativeContent = File.ReadAllText(native);
        File.Delete(native);
        try { Check(!KvmemInstallation.Validate(fixture.Bundle).Ok, "missing native worker accepted"); }
        finally { File.WriteAllText(native, nativeContent); }
        WriteBundleFile(fixture.Bundle, KvmemInstallation.ArchitectureRelativePath, "{bad");
        Check(!KvmemInstallation.Validate(fixture.Bundle).Ok, "malformed architecture manifest accepted");
        WriteBundleFile(fixture.Bundle, KvmemInstallation.ArchitectureRelativePath, "{\"architecture\":80}");
        Check(!KvmemInstallation.Validate(fixture.Bundle).Ok, "an unsupported package architecture was accepted");
        WriteBundleFile(fixture.Bundle, KvmemInstallation.ArchitectureRelativePath, "{\"architecture\":86}");
        WriteBundleFile(fixture.Bundle, KvmemInstallation.VersionRelativePath, "{\"version\":\"0.19.1\",\"supported_backends\":[\"polystrata\"]}");
        Check(!KvmemInstallation.Validate(fixture.Bundle).Ok, "single-backend package was advertised as unified");
        WriteVersion(fixture.Bundle, "0.19.1");

        SamePath(native, StrataBackend.ResolveEngine(fixture.Config, StrataBackend.KvmemProfile), "KVMem worker resolver returned the wrong bundle path");
        SamePath(Path.Combine(fixture.Bundle, KvmemInstallation.ServerRelativePath), StrataBackend.ResolveServer(fixture.Config, StrataBackend.KvmemProfile), "KVMem server resolver returned the wrong source tree");
        var plan = Build(fixture);
        SamePath(Path.Combine(fixture.Bundle, KvmemInstallation.PythonRelativePath), plan.FileName, "launch did not use bundled Python");
        SamePath(Path.Combine(fixture.Bundle, KvmemInstallation.ServeRootRelativePath), plan.WorkingDirectory, "Python module working directory is not the serve package's parent");
        Check(!PathIdentity.Equals(plan.FileName, fixture.Config.Strata.PythonPath), "KVMem fell back to the stock Python setting");
        var modelLess = WithoutKvmemModel(fixture.Config);
        Throws<FileNotFoundException>(() => new StrataBackend(StrataBackend.KvmemProfile).BuildLaunch(new EngineStartRequest { Config = modelLess }));

        // A broken explicit KVMem path must not resolve via the complete stock engine/source.
        fixture.Config.Engines["kvmem"].Parameters["packageRoot"] = Path.Combine(fixture.Work, "missing bundle");
        Check(StrataBackend.ResolveEngine(fixture.Config, StrataBackend.KvmemProfile) == null, "missing KVMem worker resolved through the legacy PolyStrata root");
        Check(StrataBackend.ResolveServer(fixture.Config, StrataBackend.KvmemProfile) == null, "missing KVMem serve tree resolved through legacy settings");

        var discoverRoot = Path.Combine(fixture.Work, "discovery");
        var old = CreateBundle(Path.Combine(discoverRoot, "engines", "kvmem", "v0.19.1-nvidia-30"));
        Equal(old, KvmemInstallation.DefaultPackageRoot(discoverRoot));
        CreateBundle(Path.Combine(discoverRoot, "engines", "kvmem", ".staging-unpublished"), "0.99.0");
        Equal(old, KvmemInstallation.DefaultPackageRoot(discoverRoot));
        var newest = CreateBundle(Path.Combine(discoverRoot, "engines", "kvmem", "v0.19.2-nvidia-30"), "0.19.2");
        Equal(newest, KvmemInstallation.DefaultPackageRoot(discoverRoot));
        CreateBundle(Path.Combine(discoverRoot, "engines", "kvmem", "v0.19.2-nvidia-40"), "0.19.2", 89);
        Check(KvmemInstallation.DefaultPackageRoot(discoverRoot) == null, "multiple installed architectures were resolved by guessing");
    }

    public static async Task RunRuntimeProbeAsync(string root)
    {
        var fixture = CreateFixture(root, "runtime probe");
        Check(KvmemInstallation.SupportsCapabilities(NativeCapabilities), "the v0.19.1 native capability contract was rejected");
        var capabilityCases = new (string Key, JsonNode? Value)[]
        {
            ("schema_version", JsonValue.Create(2)), ("backend", JsonValue.Create("ninfer")),
            ("query_scoring_revision", JsonValue.Create(1)), ("independent_graph_lrfu", JsonValue.Create(false)),
            ("signed_lrfu_hits", JsonValue.Create(false)), ("block_tokens", null),
        };
        foreach (var (key, value) in capabilityCases)
        {
            var changed = JsonNode.Parse(NativeCapabilities)!.AsObject();
            changed[key] = value?.DeepClone();
            Check(!KvmemInstallation.SupportsCapabilities(changed.ToJsonString()), "incompatible native capability was accepted: " + key);
        }
        var badBlocks = JsonNode.Parse(NativeCapabilities)!.AsObject();
        badBlocks["block_tokens"]!["supported"] = new JsonArray(JsonValue.Create(32));
        Check(!KvmemInstallation.SupportsCapabilities(badBlocks.ToJsonString()), "a worker lacking 64-token blocks passed the full contract");
        Check(!KvmemInstallation.SupportsCapabilities("{bad"), "malformed native capability JSON was accepted");
        var original = KvmemInstallation.RuntimeProbe;
        var starts = new List<ProcessStartInfo>();
        try
        {
            KvmemInstallation.RuntimeProbe = (start, timeout, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                starts.Add(start);
                Check(timeout > TimeSpan.Zero && timeout <= TimeSpan.FromSeconds(30), "runtime probe is not bounded");
                Check(start.UseShellExecute == false && start.CreateNoWindow, "probe can launch an interactive shell/window");
                Check(!start.ArgumentList.Contains("--model") && !start.ArgumentList.Contains("--serve"), "runtime probe tried to load a model or start a service");
                Check(start.Environment["PATH"]!.Contains(Path.Combine(fixture.Bundle, KvmemInstallation.CudaRelativePath), StringComparison.OrdinalIgnoreCase),
                    "runtime probe did not use the bundle's shared CUDA DLLs");
                Equal("1", start.Environment["PYTHONDONTWRITEBYTECODE"]);
                Equal("1", start.Environment["PYTHONNOUSERSITE"]);
                if (Path.GetFileName(start.FileName).Equals("python.exe", StringComparison.OrdinalIgnoreCase))
                {
                    Equal(Path.Combine(fixture.Bundle, KvmemInstallation.PythonRelativePath), start.FileName);
                    Equal(Path.Combine(fixture.Bundle, KvmemInstallation.ServeRootRelativePath), start.WorkingDirectory);
                    Check(start.ArgumentList.Contains("-B"), "Python imports may write bytecode into the installation");
                    Check(!start.ArgumentList.Any(a => a.Contains("pip install", StringComparison.Ordinal)), "runtime probe performs a network installation");
                    return Task.FromResult("KVMem bundled imports OK");
                }
                Equal(Path.Combine(fixture.Bundle, KvmemInstallation.WorkerRelativePath), start.FileName);
                return Task.FromResult(start.ArgumentList.Contains("--kvmem-capabilities")
                    ? NativeCapabilities : "--kvmem-budget --kvmem-history-reuse");
            };
            var valid = await KvmemInstallation.ValidateRuntimeAsync(fixture.Bundle);
            Check(valid.Ok, "bundled runtime seam was rejected: " + valid.Detail);
            Equal(3, starts.Count);
            Check(starts.Last().ArgumentList.SequenceEqual(new[] { "--kvmem-capabilities" }), "native capability handshake was not probed separately");

            var manifest = Path.Combine(fixture.Bundle, KvmemInstallation.ArchitectureRelativePath);
            var originalManifest = File.ReadAllText(manifest);
            var worker = Path.Combine(fixture.Bundle, KvmemInstallation.WorkerRelativePath);
            var workerBytes = File.ReadAllBytes(worker);
            try
            {
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(workerBytes)).ToLowerInvariant();
                File.WriteAllText(manifest, JsonSerializer.Serialize(new { architecture = 86, polystrata = new { worker_sha256 = digest } }));
                Check((await KvmemInstallation.ValidateRuntimeAsync(fixture.Bundle)).Ok, "a matching native provenance digest was rejected");
                File.AppendAllText(worker, "tampered fixture");
                Check(!(await KvmemInstallation.ValidateRuntimeAsync(fixture.Bundle)).Ok, "a changed native worker passed provenance verification");
            }
            finally
            {
                File.WriteAllBytes(worker, workerBytes);
                File.WriteAllText(manifest, originalManifest);
            }

            KvmemInstallation.RuntimeProbe = (start, _, _) => Task.FromResult(Path.GetFileName(start.FileName) == "python.exe" ? "imports OK" : "stock worker --model");
            Check(!(await KvmemInstallation.ValidateRuntimeAsync(fixture.Bundle)).Ok, "stock worker falsely passed KVMem runtime verification");
            KvmemInstallation.RuntimeProbe = (start, _, _) => Task.FromResult(
                start.ArgumentList.Contains("--kvmem-capabilities") ? "{\"backend\":\"ninfer\"}" :
                Path.GetFileName(start.FileName) == "python.exe" ? "imports OK" : "--kvmem-budget --kvmem-history-reuse");
            Check(!(await KvmemInstallation.ValidateRuntimeAsync(fixture.Bundle)).Ok, "wrong backend capability handshake was accepted");
            KvmemInstallation.RuntimeProbe = (_, _, _) => throw new TimeoutException("fixture deadline");
            Check(!(await KvmemInstallation.ValidateRuntimeAsync(fixture.Bundle)).Ok, "probe timeout advertised a healthy runtime");
            KvmemInstallation.RuntimeProbe = (_, _, _) => throw new IOException("fixture missing runtime DLL");
            Check(!(await KvmemInstallation.ValidateRuntimeAsync(fixture.Bundle)).Ok, "runtime dependency failure was ignored");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await ExpectThrowsAsync<OperationCanceledException>(() => KvmemInstallation.ValidateRuntimeAsync(fixture.Bundle, cancelled.Token));
            using var midProbe = new CancellationTokenSource();
            KvmemInstallation.RuntimeProbe = (_, _, _) =>
            {
                midProbe.Cancel();
                return Task.FromCanceled<string>(midProbe.Token);
            };
            await ExpectThrowsAsync<OperationCanceledException>(() => KvmemInstallation.ValidateRuntimeAsync(fixture.Bundle, midProbe.Token));
        }
        finally { KvmemInstallation.RuntimeProbe = original; }
    }

    public static void RunLaunchDefaults(string root)
    {
        var fixture = CreateFixture(root, "launch defaults");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Model)!, "mtp-paired.gguf"), "external draft fixture");
        var before = JsonSerializer.Serialize(fixture.Config);
        var plan = Build(fixture);
        Equal(before, JsonSerializer.Serialize(fixture.Config));
        Check(plan.Arguments.Contains("serve.server") && plan.Arguments.Contains("strata"), "launch does not invoke the prepared serve frontend");
        Check(plan.Arguments.Contains("-X") && plan.Arguments.Contains("utf8") && plan.Arguments.Contains("-u"), "Python UTF-8/unbuffered startup was lost");
        var cfg = ConfigOf(plan);
        var args = NativeArgs(cfg);
        var expected = new Dictionary<string, string>
        {
            ["--max-context"] = "65536", ["--kv"] = "q8_0", ["--vram-reserve-mib"] = "512",
            ["--prompt-batch"] = "128", ["--stage-mib"] = "128", ["--window"] = "1", ["--lookup"] = "0",
            ["--prompt-cache"] = "0", ["--conversation-cache-mib"] = "0", ["--conversation-cache-slots"] = "0",
            ["--kvmem-budget"] = "8192", ["--kvmem-gen-reserve"] = "512", ["--kvmem-host-mib"] = "1024",
            ["--kvmem-block-tokens"] = "32", ["--kvmem-sessions"] = "2", ["--kvmem-sink-tokens"] = "128",
            ["--kvmem-recent-tokens"] = "1024", ["--kvmem-query-scorer"] = "page-softmax", ["--kvmem-score-agg"] = "mean",
            ["--kvmem-graph-alpha"] = "0.2", ["--kvmem-query-max-tokens"] = "1024", ["--kvmem-query-replay-max-tokens"] = "8192",
        };
        foreach (var (flag, value) in expected) Equal(value, Argument(args, flag));
        Equal(fixture.Model, Argument(args, "--model"));
        foreach (var flag in new[] { "--no-draft", "--kvmem-history-reuse", "--no-kvmem-lrfu" })
            Equal(1, args.Count(a => a == flag));
        foreach (var forbidden in new[] { "--kvmem", "--serve", "--kvmem-mtp", "--draft-model", "--batch", "--kvmem-disk-mib", "--kvmem-disk-path" })
            Check(!args.Contains(forbidden), "unsafe/unrequested flag was emitted: " + forbidden);
        SamePath(Path.Combine(fixture.Bundle, KvmemInstallation.WorkerRelativePath), cfg["exe"]!.GetValue<string>(), "runconfig executable is not the bundled paging worker");
        Equal(fixture.Tokenizer, cfg["tokenizer"]!.GetValue<string>());
        Equal("cuda", cfg["backend"]!.GetValue<string>());
        Equal(0, cfg["gpu"]!.GetValue<int>());
        Equal(Path.Combine(fixture.Bundle, KvmemInstallation.CudaRelativePath), cfg["lib_dirs"]!.AsArray().Single()!.GetValue<string>());
        var env = cfg["env"]!.AsObject();
        foreach (var key in new[] { "KVMEM_POLY_STORAGE_METRICS", "KVMEM_POLY_SESSION_METRICS", "STRATA_FATTN", "STRATA_FDEC" })
            Equal("1", env[key]!.GetValue<string>());
        Equal("0", env["STRATA_GDN_MMA"]!.GetValue<string>());
        Equal("PCI_BUS_ID", env["CUDA_DEVICE_ORDER"]!.GetValue<string>());
        Equal(false, cfg["warm_start"]!.GetValue<bool>());
        Equal(true, cfg["client_reasoning_history"]!.GetValue<bool>());
        Equal(1024, cfg["kvmem_default_max_tokens"]!.GetValue<int>());
        Equal("off", cfg["kvmem_mtp_policy"]!.GetValue<string>());
        Equal(0, cfg["kvmem_mtp_drafts_max"]!.GetValue<int>());
        Check(plan.GeneratedFiles.Keys.All(p => p.Contains(Path.Combine("data", "kvmem"), StringComparison.OrdinalIgnoreCase)),
            "KVMem generated files were mixed into stock PolyStrata data");
        Check(plan.GeneratedFiles.Keys.All(p => !File.Exists(p)), "BuildLaunch materialized its runconfig as a side effect");

        var parameters = fixture.Config.Engines["kvmem"].Parameters;
        parameters["polyFlashAttention"] = "0";
        parameters["tensorCoreMma"] = "1";
        var overridden = ConfigOf(Build(fixture));
        Equal("0", overridden["env"]!["STRATA_FATTN"]!.GetValue<string>());
        Equal("0", overridden["env"]!["STRATA_FDEC"]!.GetValue<string>());
        Equal("1", overridden["env"]!["STRATA_GDN_MMA"]!.GetValue<string>());
        var overriddenPort = Fixtures.FreePort();
        var portPlan = new StrataBackend(StrataBackend.KvmemProfile).BuildLaunch(
            new EngineStartRequest { Config = fixture.Config, Port = overriddenPort }, fixture.Tokenizer);
        Equal(overriddenPort, ConfigOf(portPlan)["port"]!.GetValue<int>());
        Equal(overriddenPort.ToString(CultureInfo.InvariantCulture), Argument(portPlan.Arguments, "--port"));
        parameters.Remove("servePort");
        parameters.Remove("host");
        fixture.Config.Strata.ServePort = 8091;
        fixture.Config.Strata.Host = "0.0.0.0";
        var independent = ConfigOf(Build(fixture));
        Equal(8080, independent["port"]!.GetValue<int>());
        Equal("127.0.0.1", independent["host"]!.GetValue<string>());
    }

    public static void RunDisabledZeroPollution(string root)
    {
        var fixture = CreateFixture(root, "disabled");
        var values = fixture.Config.Engines["kvmem"].Parameters;
        values["kvmem"] = "0";
        var clean = NativeArgs(ConfigOf(Build(fixture)));
        foreach (var (key, value) in new[]
        {
            ("kvmemBudget", "bad"), ("kvmemGenReserve", "-1"), ("kvmemHostMib", "bad"),
            ("kvmemDiskMib", "-1"), ("kvmemDiskPath", "stale SSD folder"), ("kvmemSessions", "999"),
        }) values[key] = value;
        var stale = NativeArgs(ConfigOf(Build(fixture)));
        Check(clean.SequenceEqual(stale), "disabled KVMem settings changed the ordinary native command");
        Check(!stale.Any(a => a == "--kvmem" || a.StartsWith("--kvmem-", StringComparison.Ordinal) || a.StartsWith("--no-kvmem", StringComparison.Ordinal)),
            "disabled KVMem leaked a paging flag");
        Equal("128", Argument(stale, "--prompt-batch"));
        Equal("128", Argument(stale, "--stage-mib"));
        foreach (var flag in new[] { "--window", "--no-draft", "--lookup", "--prompt-cache", "--conversation-cache-slots" })
            Check(!stale.Contains(flag), "disabled KVMem still forced the paging-only decode contract: " + flag);
    }

    public static void RunInvalidParameters(string root)
    {
        var fixture = CreateFixture(root, "validation");
        var values = fixture.Config.Engines["kvmem"].Parameters;
        var baseline = new Dictionary<string, string>(values);
        var cases = new (string Key, string Value)[]
        {
            ("kvmemBudget", "2047"), ("kvmemBudget", "2049"), ("kvmemBudget", "131073"), ("kvmemBudget", "not-an-int"),
            ("kvmemGenReserve", "31"), ("kvmemGenReserve", "33"), ("kvmemGenReserve", "16385"),
            ("context", "8703"), ("context", "-1"),
            ("promptBatch", "0"), ("promptBatch", "513"), ("promptBatch", "4097"),
            ("stageMib", "0"), ("stageMib", "8193"),
            ("kvmemHostMib", "0"), ("kvmemHostMib", "131073"), ("kvmemHostMib", "bad"),
            ("kvmemSessions", "0"), ("kvmemSessions", "17"), ("kvmemSessions", "1.5"),
            ("kvmemDiskMib", "-1"), ("kvmemDiskMib", "524289"), ("kvmemDiskMib", "512"),
            ("kv", "int8"), ("kv", "int8k"), ("kv", "q4_0"),
            ("draftModel", Path.Combine(fixture.Work, "external draft.gguf")),
            ("conversationCache", "auto"), ("conversationCache", "4096"), ("vramReserveMib", "255"),
        };
        foreach (var (key, value) in cases)
        {
            Reset(values, baseline);
            values[key] = value;
            try { Throws<ArgumentException>(() => Build(fixture)); }
            catch (Exception ex) { throw new Exception($"Invalid KVMem setting was accepted: {key}={value}", ex); }
        }
        Reset(values, baseline);
        values["context"] = "8704";
        Equal("8704", Argument(NativeArgs(ConfigOf(Build(fixture))), "--max-context"));
        values["kvmemBudget"] = "2048";
        values["kvmemGenReserve"] = "32";
        values["promptBatch"] = "32";
        values["context"] = "2080";
        values["kvmemSessions"] = "1";
        values["kv"] = "fp16";
        values["conversationCache"] = "0";
        var minimum = NativeArgs(ConfigOf(Build(fixture)));
        Equal("2048", Argument(minimum, "--kvmem-budget"));
        Equal("32", Argument(minimum, "--kvmem-gen-reserve"));
        Equal("fp16", Argument(minimum, "--kv"));
        Equal("0", Argument(minimum, "--conversation-cache-mib"));
        Equal("1", Argument(minimum, "--kvmem-sessions"));

        Reset(values, baseline);
        values["kvmemDiskMib"] = "2048";
        values["kvmemDiskPath"] = Path.Combine(fixture.Work, "NVMe 冷 cache");
        var disk = NativeArgs(ConfigOf(Build(fixture)));
        Equal("2048", Argument(disk, "--kvmem-disk-mib"));
        Equal(Path.GetFullPath(values["kvmemDiskPath"]), Argument(disk, "--kvmem-disk-path"));
        // Zero quota never activates SSD, even if a previous folder remains in the panel.
        values["kvmemDiskMib"] = "0";
        var noDisk = NativeArgs(ConfigOf(Build(fixture)));
        Check(!noDisk.Contains("--kvmem-disk-mib") && !noDisk.Contains("--kvmem-disk-path"), "zero disk quota activated SSD");
    }

    public static void RunCpuVision(string root)
    {
        var fixture = CreateFixture(root, "CPU vision");
        Check(!ConfigOf(Build(fixture)).ContainsKey("vision"), "text-only launch advertised vision");
        var worker = WriteBundleFile(fixture.Bundle, "workers/polystrata/strata-vision.exe");
        var mmproj = Path.Combine(Path.GetDirectoryName(fixture.Model)!, "mmproj-Q8_0.gguf");
        File.WriteAllText(mmproj, "projection fixture");
        fixture.Config.Engines["kvmem"].Parameters["mmproj"] = mmproj;
        fixture.Config.Engines["kvmem"].Parameters["threads"] = "64";
        var vision = ConfigOf(Build(fixture))["vision"]!.AsObject();
        SamePath(worker, vision["exe"]!.GetValue<string>(), "vision did not use the bundled companion executable");
        Equal(mmproj, vision["mmproj"]!.GetValue<string>());
        Equal(fixture.Model, vision["model"]!.GetValue<string>());
        Equal(false, vision["gpu"]!.GetValue<bool>());
        Equal(1024, vision["max_tokens"]!.GetValue<int>());
        Equal(16, vision["threads"]!.GetValue<int>());
        fixture.Config.Engines["kvmem"].Parameters.Remove("mmproj");
        var paired = ConfigOf(Build(fixture))["vision"]!.AsObject();
        Equal(mmproj, paired["mmproj"]!.GetValue<string>());
        Equal(false, paired["gpu"]!.GetValue<bool>());
    }

    public static async Task RunModelLimitsAsync(string root)
    {
        var fixture = CreateFixture(root, "model limits");
        var backend = new StrataBackend(StrataBackend.KvmemProfile);
        var good = await backend.InspectAsync(fixture.Model, CancellationToken.None);
        Check(good.Runnable, "qwen35moe fixture rejected: " + string.Join("; ", good.Blockers));
        Check(good.Notes.Any(n => n.Contains("稀疏", StringComparison.Ordinal)), "KVMem compatibility hides its sparse-attention limitation");
        foreach (var arch in new[] { "qwen3", "qwen35", "gemma4", "glm4moe", "kolibri1", "clover_moe", "" })
        {
            var model = Path.Combine(fixture.Work, "unsupported-" + (arch.Length == 0 ? "unknown" : arch) + ".gguf");
            File.WriteAllBytes(model, Fixtures.StrataGguf(arch: arch));
            var report = await backend.InspectAsync(model, CancellationToken.None);
            Check(!report.Runnable && report.Blockers.Any(b => b.Contains("qwen35moe", StringComparison.Ordinal)),
                "unsupported KVMem architecture passed inspection: " + arch);
            fixture.Config.Engines["kvmem"].ModelPath = model;
            Throws<NotSupportedException>(() => backend.BuildLaunch(new EngineStartRequest { Config = fixture.Config }, fixture.Tokenizer));
        }
        var broken = Path.Combine(fixture.Work, "unsupported expert.gguf");
        File.WriteAllBytes(broken, Fixtures.StrataGguf(gateType: 999));
        Check(!(await backend.InspectAsync(broken, CancellationToken.None)).Runnable, "unsupported native expert type was admitted");
        Check(!(await backend.InspectAsync(Path.Combine(fixture.Work, "missing.gguf"), CancellationToken.None)).Runnable,
            "missing KVMem model was runnable");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectThrowsAsync<OperationCanceledException>(() => backend.InspectAsync(fixture.Model, cancelled.Token));
    }

    public static async Task RunStockRawRejectionAsync(string root)
    {
        var fixture = CreateFixture(root, "stock raw guard");
        var backend = new LlamaCppBackend();
        foreach (var stale in new[]
        {
            "--kvmem", "--kvmem-budget 8192", "--kvmem-budget=8192", "--kvmem-gen-reserve 512",
            "--kvmem-host-mib 1024", "--kvmem-disk-mib 2048", "--kvmem-disk-path \"old tier\"", "--kvmem-sessions 2",
        })
        {
            var config = new ManagerConfig { ModelPath = fixture.Model, Args = "-c 4096 " + stale };
            Throws<NotSupportedException>(() => backend.BuildLaunch(new EngineStartRequest { Config = config }));
            await ExpectThrowsAsync<NotSupportedException>(() => backend.StartAsync(
                new EngineStartRequest { Config = config }, new DelegateLogSink(_ => { }), CancellationToken.None));
        }
        var harmless = new ManagerConfig { ModelPath = fixture.Model, Args = "-c 4096 --prompt \"the --kvmem marker is literal text\"" };
        Check(backend.BuildLaunch(new EngineStartRequest { Config = harmless }).Arguments.Contains("the --kvmem marker is literal text"),
            "the stock raw-argument guard confused a prompt value with a feature flag");
    }

    public static async Task RunReadinessAsync(string root)
    {
        var fixture = CreateFixture(root, "readiness");
        await using var server = new FakeKvmemHttpServer(fixture.Model);
        var backend = new StrataBackend(StrataBackend.KvmemProfile);
        var session = new EngineSession
        {
            BackendId = "kvmem", ModelPath = fixture.Model, Port = server.Port,
            Extra = new Dictionary<string, string>
            {
                ["kvmem"] = "1", ["budget_tokens"] = "8192", ["reserve_tokens"] = "512", ["host_mib"] = "1024",
                ["block_tokens"] = "32", ["retained_sessions"] = "2", ["disk_mib"] = "0",
            },
        };
        server.Health["loaded"] = false;
        Check(!await backend.ReadyAsync(session, CancellationToken.None), "health HTTP 200 with loaded=false was accepted as ready");
        server.Health.Remove("loaded");
        Check(!await backend.ReadyAsync(session, CancellationToken.None), "health without model-load status was accepted");
        server.Health["loaded"] = true;
        server.Props["model_path"] = Path.Combine(fixture.Work, "other model.gguf");
        Check(!await backend.ReadyAsync(session, CancellationToken.None), "another model on the same port was accepted");
        server.Props["model_path"] = fixture.Model;
        Check(await backend.ReadyAsync(session, CancellationToken.None), "correct loaded model and exact KVMem budgets were not ready");
        var kv = server.Props["kvmem"]!.AsObject();
        foreach (var (field, expected) in session.Extra.Where(p => p.Key != "kvmem"))
        {
            kv[field] = int.Parse(expected, CultureInfo.InvariantCulture) + 1;
            Check(!await backend.ReadyAsync(session, CancellationToken.None), "wrong native budget was accepted: " + field);
            kv.Remove(field);
            Check(!await backend.ReadyAsync(session, CancellationToken.None), "missing expected budget was accepted: " + field);
            kv[field] = int.Parse(expected, CultureInfo.InvariantCulture);
        }
        kv["enabled"] = false;
        Check(!await backend.ReadyAsync(session, CancellationToken.None), "non-paging native kernel was marked ready");
        kv["enabled"] = true;
        kv["history_reuse"] = false;
        Check(!await backend.ReadyAsync(session, CancellationToken.None), "missing history-reuse contract was accepted");
        kv["history_reuse"] = true;
        server.Props["backend"] = "kvmem";
        Check(!await backend.ReadyAsync(session, CancellationToken.None), "launcher id was confused with the native PolyStrata backend id");
        server.Props["backend"] = "polystrata";
        server.PropsBody = "{bad";
        Check(!await backend.ReadyAsync(session, CancellationToken.None), "malformed /props was accepted");
        server.PropsBody = null;
        server.PropsStatus = 503;
        Check(!await backend.ReadyAsync(session, CancellationToken.None), "unavailable /props was accepted");
        server.PropsStatus = 200;
        server.Props["model_path"] = fixture.Model.ToUpperInvariant().Replace('\\', '/');
        Check(await backend.ReadyAsync(session, CancellationToken.None), "equivalent normalized model path was rejected");
        server.Props["model_path"] = fixture.Model;
        var restoreConfig = Path.Combine(fixture.Work, "restored runconfig.json");
        File.WriteAllText(restoreConfig, ConfigOf(Build(fixture)).ToJsonString());
        var restored = session with
        {
            Extra = new Dictionary<string, string>(),
            Arguments = "-m serve.server --config " + LauncherArguments.Quote(restoreConfig),
        };
        Check(await backend.ReadyAsync(restored, CancellationToken.None), "restored CLI session could not verify its persisted native settings");
        kv["budget_tokens"] = 4096;
        Check(!await backend.ReadyAsync(restored, CancellationToken.None), "restored session ignored its persisted expected KV budget");
        kv["budget_tokens"] = 8192;
        Check(!await backend.ReadyAsync(restored with { Arguments = "" }, CancellationToken.None), "identity-free HTTP 200 was treated as a restored KVMem session");
        File.Delete(restoreConfig);
        Check(!await backend.ReadyAsync(restored, CancellationToken.None), "missing persisted runconfig still verified a restored session");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectThrowsAsync<OperationCanceledException>(() => backend.ReadyAsync(session, cancelled.Token));
        var ordinary = session with { Extra = new Dictionary<string, string> { ["kvmem"] = "0" } };
        server.Props.Remove("kvmem");
        Check(await backend.ReadyAsync(ordinary, CancellationToken.None), "disabled paging still required a KVMem props block");
        server.Health["loaded"] = false;
        Check(!await backend.ReadyAsync(ordinary, CancellationToken.None), "disabled paging ignored model readiness");
        Check(server.Requests > 0, "readiness never exercised the loopback HTTP fixture");
    }

    private sealed record Fixture(string Work, string Bundle, string Model, string Tokenizer, ManagerConfig Config);

    private static Fixture CreateFixture(string root, string name)
    {
        var work = Path.Combine(root, "kvmem-regression", name + "-" + Guid.NewGuid().ToString("N"));
        var bundle = CreateBundle(Path.Combine(work, "unified bundle with spaces"));
        var model = Path.Combine(work, "models", "KVMem fixture model.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllBytes(model, Fixtures.StrataGguf(mtp: true));
        var tokenizer = Path.Combine(work, "tokenizer");
        Directory.CreateDirectory(tokenizer);
        foreach (var file in new[] { "vocab.json", "merges.txt", "tokenizer.json", "token_type.json", "chat_template.jinja" })
            File.WriteAllText(Path.Combine(tokenizer, file), "fixture");
        var stock = Path.Combine(work, "stock PolyStrata");
        WriteBundleFile(stock, "engine-poly/strata-poly.exe");
        WriteBundleFile(stock, "serve/server.py");
        var stockPython = WriteBundleFile(stock, "stock-python.exe");
        var stockModel = Path.Combine(stock, "legacy model.gguf");
        File.WriteAllBytes(stockModel, Fixtures.StrataGguf());
        var config = new ManagerConfig();
        config.Strata.PackageRoot = stock;
        config.Strata.ServeRoot = stock;
        config.Strata.PythonPath = stockPython;
        config.Strata.ModelPath = stockModel;
        config.Strata.Context = 4096;
        config.Engines["polystrata"] = new EngineSettings
        {
            ModelPath = stockModel,
            Parameters = new() { ["packageRoot"] = stock, ["serveRoot"] = stock, ["pythonPath"] = stockPython },
        };
        config.Engines["kvmem"] = new EngineSettings { ModelPath = model, Parameters = new() { ["packageRoot"] = bundle } };
        return new(work, bundle, model, tokenizer, config);
    }

    private static string CreateBundle(string directory, string version = "0.19.1", int architecture = 86)
    {
        var bundle = Path.GetFullPath(directory);
        foreach (var relative in KvmemInstallation.RequiredRelativeFiles) WriteBundleFile(bundle, relative);
        WriteBundleFile(bundle, "runtime/cuda/cublas64_13.dll");
        WriteBundleFile(bundle, "runtime/cuda/cublasLt64_13.dll");
        WriteVersion(bundle, version);
        WriteBundleFile(bundle, KvmemInstallation.ArchitectureRelativePath, JsonSerializer.Serialize(new { architecture }));
        return bundle;
    }

    private static string WriteBundleFile(string bundle, string relative, string content = "# fixture\n")
    {
        var path = Path.Combine(bundle, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void WriteVersion(string bundle, string version) => WriteBundleFile(bundle,
        KvmemInstallation.VersionRelativePath, JsonSerializer.Serialize(new { version, supported_backends = new[] { "ninfer", "polystrata" } }));

    private static ManagerConfig WithoutKvmemModel(ManagerConfig config)
    {
        var copy = JsonSerializer.Deserialize<ManagerConfig>(JsonSerializer.Serialize(config))!;
        copy.Engines["kvmem"].ModelPath = "";
        return copy;
    }

    private static LaunchPlan Build(Fixture fixture) => new StrataBackend(StrataBackend.KvmemProfile)
        .BuildLaunch(new EngineStartRequest { Config = fixture.Config }, fixture.Tokenizer);

    private static JsonObject ConfigOf(LaunchPlan plan)
    {
        var path = Argument(plan.Arguments, "--config");
        Check(plan.GeneratedFiles.Count == 1 && plan.GeneratedFiles.ContainsKey(path), "launch did not generate exactly one declared runconfig");
        return JsonNode.Parse(plan.GeneratedFiles[path])!.AsObject();
    }

    private static List<string> NativeArgs(JsonObject cfg) => cfg["args"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    private static string Argument(IReadOnlyList<string> args, string flag)
    {
        Equal(1, args.Count(a => a == flag));
        var index = args.ToList().IndexOf(flag);
        Check(index + 1 < args.Count, "flag has no value: " + flag);
        return args[index + 1];
    }

    private static void SamePath(string expected, string? actual, string message) =>
        Check(PathIdentity.Equals(expected, actual), message + ": " + actual);

    private static void Reset(Dictionary<string, string> values, Dictionary<string, string> baseline)
    {
        values.Clear();
        foreach (var (key, value) in baseline) values[key] = value;
    }

    private static async Task ExpectThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    /// <summary>Bounded, in-process /health + /props fixture; never binds a real model-service port.</summary>
    private sealed class FakeKvmemHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;

        public FakeKvmemHttpServer(string model)
        {
            Health = new JsonObject { ["status"] = "ok", ["loaded"] = true, ["model"] = "fixture", ["max_context"] = 65536 };
            Props = new JsonObject
            {
                ["backend"] = "polystrata", ["model_path"] = model, ["is_sleeping"] = false,
                ["kvmem"] = new JsonObject
                {
                    ["enabled"] = true, ["budget_tokens"] = 8192, ["reserve_tokens"] = 512, ["host_mib"] = 1024,
                    ["block_tokens"] = 32, ["retained_sessions"] = 2, ["disk_mib"] = 0, ["history_reuse"] = true,
                },
            };
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = ServeAsync();
        }

        public int Port { get; }
        public JsonObject Health { get; }
        public JsonObject Props { get; }
        public string? PropsBody { get; set; }
        public int PropsStatus { get; set; } = 200;
        public int Requests => Volatile.Read(ref _requests);

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (SocketException) when (_stop.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { return; }
                using (client)
                {
                    try
                    {
                        await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var request = await reader.ReadLineAsync(_stop.Token) ?? "";
                        for (string? line; !string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token));) { }
                        Interlocked.Increment(ref _requests);
                        var path = request.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
                        var code = path == "/health" ? 200 : path == "/props" ? PropsStatus : 404;
                        var body = path == "/health" ? Health.ToJsonString() : path == "/props" ? PropsBody ?? Props.ToJsonString() : "{}";
                        var bytes = Encoding.UTF8.GetBytes(body);
                        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} fixture\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header, _stop.Token);
                        await stream.WriteAsync(bytes, _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                    catch (IOException) { } // A cancelled HTTP caller can close its fixture connection.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { _stop.Dispose(); }
        }
    }
}
