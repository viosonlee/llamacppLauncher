using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

/// <summary>
/// Offline v1.0 compatibility contracts. Register public Run* methods sequentially in the parent runner.
/// No engine execution, downloads, model files, services, configuration writes or build-number
/// capability guesses. Release/snippet provenance is pinned beside the inline JSON below.
/// </summary>
internal static class LlamaV1CompatibilityRegression
{
    public static void RunVersionAndDriverParsing()
    {
        // Parser inputs, not claimed native captures: v1 semver must normalize to its nightly build.
        foreach (var output in new[]
        {
            "version: 1.0.0 (build 12359, commit b1b770d90)",
            "1.0.0 (build 12359, commit b1b770d90)",
            "version:1.0.0(build12359)",
            "loader diagnostic\r\nversion: 1.0.0 (build 0012359, commit b1b770d90)\r\n",
        }) Equal("b12359", BuiltInInstallation.ParseLlamaVersion(output));
        Equal("b12375", BuiltInInstallation.ParseLlamaVersion("version: 1.0.0 (build 12375, commit 9d25e0014)"));
        Equal("b12182", BuiltInInstallation.ParseLlamaVersion("version: b12182"));
        foreach (var output in new[] { "1.0.0", "version: 1.0.0", "nightly-tag.txt: b12359", "version: 1.0.0 (build unknown)" })
            Check(BuiltInInstallation.ParseLlamaVersion(output) == null, "release semver/metadata was mistaken for a native build: " + output);
        Check(!BuiltInInstallation.IsComparableVersion("llamacpp", "v1.0.0"), "stable metadata tag became a comparable nightly");
        Check(DoctorService.IsOutdated("b12182", "b12359") && DoctorService.IsOutdated("b12359", "b12375"),
            "installed build, v1-linked nightly and newer nightly lost their numeric ordering");
        Check(!DoctorService.IsOutdated("b12375", "b12359"), "v1-linked nightly would downgrade the newer nightly");

        // User-reported driver/UMD values; no nvidia-smi process is invoked.
        Equal(13.5, EngineAcquisitionService.ParseDriverCudaVersion("Driver Version: 621.24 | CUDA UMD Version: 13.5 |"));
        Equal(13.5, EngineAcquisitionService.ParseDriverCudaVersion("Driver Version: 621.24 | CUDA Version: 13.5 |"));
        Equal(0d, EngineAcquisitionService.ParseDriverCudaVersion("Driver Version: 621.24"));
    }

    public static void RunPinnedManagedPlans(string root)
    {
        var stableNode = JsonNode.Parse(StableReleaseJson)!;
        Equal("2026-11-13T10:20:19Z", stableNode["published_at"]!.GetValue<string>());
        var stable = EngineCatalog.ParseRelease(stableNode);
        Equal("v1.0.0", stable.Tag);
        var marker = stable.Assets.Single();
        Equal("nightly-tag.txt", marker.Name);
        Equal("https://github.com/ggml-org/llama.cpp/releases/download/v1.0.0/nightly-tag.txt", marker.Url);
        // The seven-byte pointer agrees with the digest in the official release response.
        Equal(marker.Sha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("b12359\n"))).ToLowerInvariant());
        Check(EngineCatalog.ManagedLlamaCppPlan(stable, root, 8.6, 13.5) == null,
            "metadata-only v1.0.0 must not become an install plan");

        var release = Release(V1NightlyJson);
        var binary = Asset(release, "llama-b12359-bin-win-cuda-13.5-x64.zip");
        var runtime = Asset(release, "cudart-llama-bin-win-cuda-13.5-x64.zip");
        Equal("1de9ca1ff408d6e348ea0007c6880ad2393311a02e7799bcde26dc16daef03dd", binary.Sha256);
        Equal("c2bf4cc202bfbaaa7a0b75f5082457c3811326947bb216059b72839d844e9cba", runtime.Sha256);
        var plan = EngineCatalog.ManagedLlamaCppPlan(release, root, 8.6, 13.5);
        Check(plan != null, "official b12359 CUDA 13.5 x64 binary/runtime pair was rejected");
        Equal("llamacpp", plan!.BackendId);
        Equal("13.5", plan.CudaVersion);
        Check(plan.EngineArchives.SequenceEqual(new[] { binary, runtime }), "wrong CUDA minor, architecture, digest or runtime selected");
        Check(plan.AllArchives.SequenceEqual(plan.EngineArchives) && plan.SourceArchive == null && plan.SourceDir == null,
            "native install invented a source archive");
        Check(PathIdentity.Equals(Path.Combine(root, "engines", "llamacpp", "b12359-cuda-13.5"), plan.PackageRoot),
            "v1 nightly escaped its immutable managed version root");
        Check(PathIdentity.Equals(plan.PackageRoot, plan.EngineDir), "binary and runtime no longer extract together");

        foreach (var (capability, driver) in new[] { (8.6, 13.4), (8.6, 0d), (7.5, 13.5) })
        {
            var fallback = EngineCatalog.ManagedLlamaCppPlan(release, root, capability, driver);
            Check(fallback != null, "official b12359 conservative CUDA pair was lost");
            Equal("12.4", fallback!.CudaVersion);
            Equal(Asset(release, "llama-b12359-bin-win-cuda-12.4-x64.zip"), fallback.EngineArchives[0]);
            Equal(Asset(release, "cudart-llama-bin-win-cuda-12.4-x64.zip"), fallback.EngineArchives[1]);
        }
        var only13 = release with { Assets = [binary, runtime] };
        Check(EngineCatalog.ManagedLlamaCppPlan(only13, root, 8.6, 13.4) == null, "driver 13.4 accepted a CUDA 13.5-only release");
        Check(EngineCatalog.ManagedLlamaCppPlan(only13, root, 7.5, 13.5) == null, "pre-Ampere selected CUDA 13.5");
    }

    public static void RunNoCrossReleasePairing(string root)
    {
        var v1 = Release(V1NightlyJson);
        var latest = Release(LatestNightlyJson);
        var binary = Asset(v1, "llama-b12359-bin-win-cuda-13.5-x64.zip");
        var runtime = Asset(v1, "cudart-llama-bin-win-cuda-13.5-x64.zip");
        var laterRuntime = Asset(latest, runtime.Name);
        Equal(runtime.Name, laterRuntime.Name);
        Equal(runtime.Sha256, laterRuntime.Sha256); // Even identical bytes do not change release provenance.
        Check(runtime.Url != laterRuntime.Url, "pinned runtime URLs no longer distinguish releases");

        // Deliberately incomplete/mixed mutations of real assets, NOT purported GitHub responses.
        var invalid = new (string Reason, ReleaseAsset[] Assets)[]
        {
            ("missing runtime", [binary]),
            ("missing binary", [runtime]),
            ("cross CUDA minor", [binary, Asset(v1, "cudart-llama-bin-win-cuda-12.4-x64.zip")]),
            ("ARM64 runtime with x64 binary", [binary, Asset(v1, "cudart-llama-bin-win-cuda-13.5-arm64.zip")]),
            ("binary from a different nightly", [Asset(latest, "llama-b12375-bin-win-cuda-13.5-x64.zip"), runtime]),
            ("untagged runtime URL from a different nightly", [binary, laterRuntime]),
            ("same-named runtime from an unrelated repository", [binary, runtime with { Url = runtime.Url.Replace("ggml-org/llama.cpp", "foreign/llama.cpp") }]),
            ("runtime from an unverified mirror", [binary, runtime with { Url = "https://mirror.invalid/" + runtime.Name }]),
        };
        foreach (var (reason, assets) in invalid)
            Check(EngineCatalog.ManagedLlamaCppPlan(v1 with { Assets = assets }, root, 8.6, 13.5) == null,
                "b12359 accepted " + reason);

        // A wrong-release same-name runtime must not shadow the correct runtime later in the list.
        // Rejecting the contaminated input entirely is also safe.
        var recovered = EngineCatalog.ManagedLlamaCppPlan(v1 with { Assets = [binary, laterRuntime, runtime] }, root, 8.6, 13.5);
        Check(recovered == null || recovered.EngineArchives[1] == runtime,
            "runtime selection trusted a same-name asset from b12375 instead of the b12359 asset");
    }

    public static async Task RunLatestSelectionAsync()
    {
        var fetch = EngineAcquisitionService.ReleasesFetcher;
        var capability = EngineAcquisitionService.DetectComputeCapability;
        var driver = EngineAcquisitionService.DetectCudaVersion;
        var archive = EngineAcquisitionService.ArchiveFetcher;
        var native = BuiltInInstallation.NativeProbe;
        var releases = ReleaseList(StableReleaseJson, V1NightlyJson);
        var fetches = 0;
        try
        {
            EngineAcquisitionService.DetectComputeCapability = () => 8.6;
            EngineAcquisitionService.DetectCudaVersion = () => 13.5;
            EngineAcquisitionService.ReleasesFetcher = (repo, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Equal("ggml-org/llama.cpp", repo);
                fetches++;
                return Task.FromResult<JsonArray?>((JsonArray)releases.DeepClone());
            };
            EngineAcquisitionService.ArchiveFetcher = (_, _, _, _) => throw new Exception("release selection must not download archives");
            BuiltInInstallation.NativeProbe = (_, _, _) => throw new Exception("release selection must not probe any real engine");

            Equal("b12359", (await EngineAcquisitionService.LatestCompatibleReleaseAsync("llamacpp"))?.Tag);
            // Input order must not confuse the metadata-only stable tag with the latest nightly.
            releases = ReleaseList(StableReleaseJson, V1NightlyJson, LatestNightlyJson);
            Equal("b12375", (await EngineAcquisitionService.LatestCompatibleReleaseAsync("llamacpp"))?.Tag);
            releases = ReleaseList(LatestNightlyJson, StableReleaseJson, V1NightlyJson);
            Equal("b12375", (await EngineAcquisitionService.LatestCompatibleReleaseAsync("llamacpp"))?.Tag);

            // Each release has only half a pair; selection must never pool assets across releases.
            releases = ReleaseList(V1NightlyJson, LatestNightlyJson);
            KeepOnly(releases[0]!, "llama-b12359-bin-win-cuda-13.5-x64.zip");
            KeepOnly(releases[1]!, "cudart-llama-bin-win-cuda-13.5-x64.zip");
            await ThrowsAsync<InvalidOperationException>(() => EngineAcquisitionService.LatestCompatibleReleaseAsync("llamacpp"));
            releases = ReleaseList(StableReleaseJson);
            await ThrowsAsync<InvalidOperationException>(() => EngineAcquisitionService.LatestCompatibleReleaseAsync("llamacpp"));
            Equal(5, fetches);
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => EngineAcquisitionService.LatestCompatibleReleaseAsync("llamacpp", stop.Token));
            Equal(5, fetches);
        }
        finally
        {
            EngineAcquisitionService.ReleasesFetcher = fetch;
            EngineAcquisitionService.DetectComputeCapability = capability;
            EngineAcquisitionService.DetectCudaVersion = driver;
            EngineAcquisitionService.ArchiveFetcher = archive;
            BuiltInInstallation.NativeProbe = native;
        }
    }

    public static void RunStockPrismIsolation()
    {
        const string official = "version: 1.0.0 (build 12359, commit b1b770d90)";
        Equal("b12359", BuiltInInstallation.ParseLlamaVersion(official));
        // Negative parser mutations: a modern-looking line must not launder a fork into stock.
        foreach (var output in new[] { "Prism b12359\n" + official, official + "\nprism-b12359-custom", "version: prism-b12182-custom" })
            Check(BuiltInInstallation.ParseLlamaVersion(output) == null, "Prism became an official comparable v1 build");
        Check(!BuiltInInstallation.IsComparableVersion("llamacpp", "prism-b12359"), "Prism tag became an official nightly");
        Check(EngineAcquisitionService.CanAcquire("llamacpp") && !EngineAcquisitionService.CanAcquire("prism"),
            "stock acquisition registration now auto-updates Prism");
        Check(EngineAcquisitionService.RepoFor("prism") == null, "Prism inherited the stock release repository");
        Check(EngineAcquisitionService.ManagedPlanFor("prism", Release(V1NightlyJson), Path.GetTempPath(), 8.6, 13.5) == null,
            "official v1 assets became a Prism install plan");
    }

    public static void RunExistingArgumentsAndSchema()
    {
        // These are normal user flags, still documented at b12359 (README lines 71-89).
        // In particular --moe-cache-mib already exists in the user's b12182: do not infer
        // support for it, CPU-share or FP8 from a blanket v1 build cutoff.
        const string args = "-ngl all --n-cpu-moe 22 --fit on -ctk q8_0 -ctv q8_0 -c 8192";
        var values = LlamaCppParameters.ReadFromArgs(args);
        Equal("all", values["offloadLayers"]);
        Equal("22", values["moeOffload"]);
        Equal("on", new LauncherArguments(args).Get("--fit"));
        foreach (var key in new[] { "contextQuantK", "contextQuantV" })
        {
            Equal("q8_0", values[key]);
            var spec = LlamaCppParameters.Specs.Single(s => s.Key == key);
            Equal(ParamType.Choice, spec.Type);
            Equal("", spec.Default);
            Check(spec.Choices!.Any(c => c.Value == "q8_0"), key + " lost its existing q8_0 choice");
            Check(!spec.Choices!.Any(c => c.Value == "f8_e4m3" && c.Recommended), "FP8 became an automatic q8_0 replacement");
        }
        var edited = LlamaCppParameters.Apply(args, "context", "4096");
        Equal(args.Replace("-c 8192", "-c 4096"), edited);
        var parsed = new LauncherArguments(edited);
        Equal("all", parsed.Get("-ngl"));
        Equal("22", parsed.Get("--n-cpu-moe"));
        Equal("on", parsed.Get("--fit"));
        Equal("q8_0", parsed.Get("-ctk"));
        Equal("q8_0", parsed.Get("-ctv"));
        foreach (var flag in new[] { "--moe-cache-mib", "--moe-cache-cpu-share", "--kvmem" })
            Check(!parsed.Has(flag), "ordinary parameter edit injected an opt-in/foreign feature: " + flag);
        Equal("q8_0", LlamaCppParameters.ReadFromArgs(LlamaCppParameters.Apply(edited, "contextQuantK", "f16"))["contextQuantV"]);
    }

    public static void RunPinnedServerTelemetry()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Check(SpeedMonitorService.Parse(SlotsReadmeJson).Contains("136"), "pinned upstream object next_token counter was lost");
            var slots = JsonNode.Parse(SlotsReadmeJson)!.AsArray();
            // README still shows an object; the same tag's actual to_json emits json::array.
            // Apply that source-backed shape to the documented values, not an invented API change.
            slots[0]!["next_token"] = new JsonArray(slots[0]!["next_token"]!.DeepClone());
            var monitor = new SpeedMonitorService();
            var session = new ServerSession { ProcessId = 12359, StartTimeUtcTicks = 1 };
            var first = monitor.Observe(session, slots.ToJsonString(), [], TimeSpan.Zero);
            Equal(SpeedKind.Generating, first.Kind);
            Check(first.Text.Contains("136") && !first.Text.Contains("tok/s"), "array counter missing or initial rate fabricated");
            // Explicitly synthetic subsequent observation, retaining the real schema.
            slots[0]!["next_token"]![0]!["n_decoded"] = 160;
            var second = monitor.Observe(session, slots.ToJsonString(), [], TimeSpan.FromSeconds(2));
            Check(second.Text.Contains("约 12.00 tok/s") && second.Text.Contains("160"), "array counter delta did not produce an estimated rate");
            slots[0]!["id_task"] = 1;
            var reset = monitor.Observe(session, slots.ToJsonString(), [], TimeSpan.FromSeconds(4));
            Check(!reset.Text.Contains("tok/s"), "new request reused the old request's counter");
            slots[0]!["is_processing"] = false;
            Equal("", SpeedMonitorService.Parse(slots.ToJsonString()));

            var timing = SpeedTestService.ParseTimings(TimingsReadmeJson);
            Check(timing != null, "pinned upstream timings block was ignored");
            Equal<double?>(52.94494935437416, timing!.DecodeTps);
            Equal<double?>(32.301828283480845, timing.PrefillTps);
            Equal(0, timing.DraftOffered);
            Equal(0, timing.DraftAccepted);
            Check(timing.DraftAcceptance == null, "non-speculative response fabricated draft acceptance");
            Check(SpeedTestService.ParseTimings("{}") == null && SpeedTestService.ParseTimings("not json") == null,
                "missing/malformed telemetry fabricated timings");

            // Source-format log fixtures using the README's timings values rounded to two decimals.
            // server-context.cpp at b12359, print_timings(), lines 753-772.
            const string prompt = "slot print_timings: id 1 | task 0 | prompt eval time = 30.96 ms / 1 tokens (30.96 ms per token, 32.30 tokens per second)";
            const string decode = "slot print_timings: id 1 | task 0 |        eval time = 661.06 ms / 35 tokens (18.89 ms per token, 52.94 tokens per second)";
            Check(SpeedMonitorService.ParseDecodeTiming(prompt) == null, "prompt throughput was mistaken for generation speed");
            var decoded = SpeedMonitorService.ParseDecodeTiming(decode);
            Check(decoded.HasValue, "v1 decode log format was lost");
            Equal(52.94, decoded!.Value.Rate);
            Equal(35, decoded.Value.Tokens);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static ReleaseInfo Release(string json) => EngineCatalog.ParseRelease(JsonNode.Parse(json)!);
    private static ReleaseAsset Asset(ReleaseInfo release, string name) => release.Assets.Single(a => a.Name == name);
    private static JsonArray ReleaseList(params string[] json) => new(json.Select(j => JsonNode.Parse(j)).ToArray());
    private static void KeepOnly(JsonNode release, string name) => release["assets"] =
        new JsonArray(release["assets"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == name)!.DeepClone());
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    // Exact field projections of official release responses, captured 2026-11-14.
    // Only unused fields/assets are omitted; names, URLs, flags, dates and digests are unchanged.
    // Stable/latest: supplied /tmp/ggml-org-llama.cpp-releases.json; exact b12359: bounded tag query.
    // https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/v1.0.0
    private const string StableReleaseJson = """
        {
          "url": "https://api.github.com/repos/ggml-org/llama.cpp/releases/444087024",
          "tag_name": "v1.0.0", "draft": false, "prerelease": false,
          "published_at": "2026-11-13T10:20:19Z",
          "assets": [
            {
              "name": "nightly-tag.txt",
              "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/v1.0.0/nightly-tag.txt",
              "digest": "sha256:e8352faf0647cae078e6be57cd129ba534ec0664b162231a68955379e9c05e4b"
            }
          ]
        }
        """;

    // https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/b12359
    // Windows CUDA assets only, including ARM64 decoys; these are real assets, not downloads.
    private const string V1NightlyJson = """
        {
          "url": "https://api.github.com/repos/ggml-org/llama.cpp/releases/444087610",
          "tag_name": "b12359", "draft": false, "prerelease": true,
          "published_at": "2026-11-13T10:21:06Z",
          "assets": [
            {
              "name": "cudart-llama-bin-win-cuda-12.4-x64.zip",
              "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b12359/cudart-llama-bin-win-cuda-12.4-x64.zip",
              "digest": "sha256:8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6"
            },
            {
              "name": "cudart-llama-bin-win-cuda-13.5-arm64.zip",
              "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b12359/cudart-llama-bin-win-cuda-13.5-arm64.zip",
              "digest": "sha256:f89e7b2457f627d052dd99f42205ddfd01964454809754cdc649cffc74320dca"
            },
            {
              "name": "cudart-llama-bin-win-cuda-13.5-x64.zip",
              "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b12359/cudart-llama-bin-win-cuda-13.5-x64.zip",
              "digest": "sha256:c2bf4cc202bfbaaa7a0b75f5082457c3811326947bb216059b72839d844e9cba"
            },
            {
              "name": "llama-b12359-bin-win-cuda-12.4-x64.zip",
              "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b12359/llama-b12359-bin-win-cuda-12.4-x64.zip",
              "digest": "sha256:e9c0abf442e8339c06728366c1ad2c711332f9939c0c00603fb75d85fd44c914"
            },
            {
              "name": "llama-b12359-bin-win-cuda-13.5-arm64.zip",
              "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b12359/llama-b12359-bin-win-cuda-13.5-arm64.zip",
              "digest": "sha256:f40398808e74b88c3ccee904824667ef9825692843533e7fc22881ab2e4de9da"
            },
            {
              "name": "llama-b12359-bin-win-cuda-13.5-x64.zip",
              "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b12359/llama-b12359-bin-win-cuda-13.5-x64.zip",
              "digest": "sha256:1de9ca1ff408d6e348ea0007c6880ad2393311a02e7799bcde26dc16daef03dd"
            }
          ]
        }
        """;

    // https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/b12375
    // The latest nightly at capture time, NOT the nightly linked by stable v1.0.0.
    private const string LatestNightlyJson = """
        {
          "url": "https://api.github.com/repos/ggml-org/llama.cpp/releases/445005853",
          "tag_name": "b12375", "draft": false, "prerelease": true,
          "published_at": "2026-11-14T04:14:54Z",
          "assets": [
            {
              "name": "cudart-llama-bin-win-cuda-13.5-x64.zip",
              "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b12375/cudart-llama-bin-win-cuda-13.5-x64.zip",
              "digest": "sha256:c2bf4cc202bfbaaa7a0b75f5082457c3811326947bb216059b72839d844e9cba"
            },
            {
              "name": "llama-b12375-bin-win-cuda-13.5-x64.zip",
              "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b12375/llama-b12375-bin-win-cuda-13.5-x64.zip",
              "digest": "sha256:63b72430c8b6b5fc2b5e61041b86d792c4afefada23999c1dff73a2e12ca2ef1"
            }
          ]
        }
        """;

    // https://github.com/ggml-org/llama.cpp/blob/b12359/tools/server/README.md#L1082-L1226
    // README lines 1159-1224: second slot, dropping only unused params.
    // Actual array serialization: https://github.com/ggml-org/llama.cpp/blob/b12359/tools/server/server-context.cpp#L812-L842
    private const string SlotsReadmeJson = """
        [
          {
            "id": 1, "id_task": 0, "n_ctx": 65536, "speculative": false, "is_processing": true,
            "next_token": { "has_next_token": true, "has_new_line": true, "n_remain": -1, "n_decoded": 136 }
          }
        ]
        """;

    // https://github.com/ggml-org/llama.cpp/blob/b12359/tools/server/README.md#L1531-L1556
    // Exact example fields/values; only JavaScript comments removed to make valid JSON.
    private const string TimingsReadmeJson = """
        {
          "choices": [],
          "created": 1757141666,
          "id": "chatcmpl-ecQULm0WqPrftUqjPZO1CFYeDjGZNbDu",
          "timings": {
            "cache_n": 236,
            "prompt_n": 1,
            "prompt_ms": 30.958,
            "prompt_per_token_ms": 30.958,
            "prompt_per_second": 32.301828283480845,
            "predicted_n": 35,
            "predicted_ms": 661.064,
            "predicted_per_token_ms": 18.887542857142858,
            "predicted_per_second": 52.94494935437416
          }
        }
        """;
}
