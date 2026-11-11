using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LlamaManager.Models;
using LlamaManager.Services;

internal static class Program
{
    private static int _passed, _failed;
    private static string _root = "", _model = "", _prismModel = "";
    private static ManagerConfig Config(int port) => new() { ModelPath = _model, Port = port, Args = $"--host 127.0.0.1 --port {port} -ngl 0" };
    internal static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected <{expected}>, got <{actual}>");
    }
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task Test(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
    }
    private static Task Run(Action action) { action(); return Task.CompletedTask; }

    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--real-log-cache")
        {
            if (args.Length != 2) throw new ArgumentException("--real-log-cache requires a log path.");
            LogCacheRegression.RunNative(args[1]);
            return 0;
        }
        if (args.FirstOrDefault() == "--real-file-signature")
        {
            if (args.Length != 2) throw new ArgumentException("--real-file-signature requires the model path.");
            await FileSignatureRegression.RunNativeAsync(Path.Combine(ConfigService.GetManagerRoot(), "logs"), args[1]);
            return 0;
        }
        if (args.FirstOrDefault() == "--real-download-publication")
        {
            var work = Path.Combine(ConfigService.GetManagerRoot(), "logs", "download-native-" + Guid.NewGuid().ToString("N"));
            await DownloadConcurrencyRegression.RunWindowsLockAsync(work);
            return 0;
        }
        if (args.FirstOrDefault() == "--real-cuda-cache")
        {
            if (args.Length != 2) throw new ArgumentException("--real-cuda-cache requires the real llama-server executable path.");
            await CudaCacheRegression.RunNativeAsync(args[1]);
            return 0;
        }
        if (args.FirstOrDefault() == "--real-kvmem-ui")
            return await RealKvmemUiSmoke.RunAsync(args[1]);
        if (args.FirstOrDefault() == "--check-published")
            return await PublishedSmoke.RunAsync(args[1]);
        if (args.FirstOrDefault() == "--read-live-speed")
        {
            // Optional second argument: the backend id, so a PolyStrata service can be probed too.
            var config = ConfigService.ReadConfig();
            var backendId = args.Skip(1).FirstOrDefault() ?? "llamacpp";
            var slot = backendId == "llamacpp" ? ManagerConfig.SlotMain : backendId;
            var session = config.GetRunning(slot) ?? throw new InvalidOperationException($"没有记录的 {backendId} 运行实例。");
            var status = await new SpeedMonitorService().QuerySpeedAsync(session, backendId);
            Console.WriteLine(status.Text.Length == 0 ? "(无)" : status.Text);
            if (status.Detail.Length > 0) Console.WriteLine("遥测: " + status.Detail);
            return 0;
        }
        if (args.FirstOrDefault() == "--probe-logs")
        {
            // Prints exactly what the log page would render, with per-file line counts.
            var backendId = args.Skip(1).FirstOrDefault() ?? ConfigService.ReadConfig().ActiveBackendId;
            foreach (var line in LogService.ReadRecent(backendId))
                Console.WriteLine(line.StartsWith("────") ? $"\n{line}" : line);
            return 0;
        }
        if (args.FirstOrDefault() == "--download-model")
        {
            // Real end-to-end check of the curated download: name or index from the catalogue.
            var pick = args.Skip(1).FirstOrDefault() ?? "0";
            var model = int.TryParse(pick, out var index)
                ? ModelCatalog.Recommendations[index]
                : ModelCatalog.Recommendations.First(m => m.FileName.Contains(pick, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"下载 {model.DisplayName}（{EngineDownloadService.FormatBytes(model.Bytes)}）");
            Console.WriteLine($"目标：{ModelDownloadService.TargetDirectory}");
            var result = await ModelDownloadService.DownloadAsync(model, Console.WriteLine,
                CancellationToken.None, progress: (received, total) =>
                    Console.Write($"\r{EngineDownloadService.FormatBytes(received)}" +
                        (total is > 0 ? $" / {EngineDownloadService.FormatBytes(total.Value)}   " : "   ")));
            Console.WriteLine($"\n完成：{result.Path}（{EngineDownloadService.FormatBytes(result.Bytes)}）");
            return 0;
        }
        if (args.FirstOrDefault() == "--benchmark")
        {
            // --benchmark <model.gguf> <runs> <plan-file>: each non-empty line is "label|args".
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LLAMA_MANAGER_ROOT")))
                throw new ArgumentException("Set an isolated LLAMA_MANAGER_ROOT for a benchmark run.");
            var modelPath = args[1];
            var runs = int.Parse(args[2]);
            var plan = ParameterBenchmarkService.ParsePlan(File.ReadAllText(args[3]));
            var report = await ParameterBenchmarkService.RunAsync(modelPath, "", plan, runs, Console.WriteLine, CancellationToken.None);
            Console.WriteLine(ParameterBenchmarkService.Describe(report));
            return report.Errors.Count == 0 ? 0 : 1;
        }
        if (args.FirstOrDefault() == "--check-strata")
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LLAMA_MANAGER_ROOT")))
                throw new ArgumentException("Set an isolated LLAMA_MANAGER_ROOT for the explicit GPU smoke run.");
            var config = JsonSerializer.Deserialize<ManagerConfig>(File.ReadAllText(args[1]))!;
            var result = await StrataService.RunAsync(config, Console.WriteLine);
            Console.WriteLine(result.Answer);
            Console.WriteLine("Log: " + result.LogPath);
            return 0;
        }
        if (args.FirstOrDefault() == "--prepare-pack")
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LLAMA_MANAGER_ROOT")))
                throw new ArgumentException("Set an isolated LLAMA_MANAGER_ROOT for the pack smoke run.");
            var o = JsonSerializer.Deserialize<StrataPackService.Options>(File.ReadAllText(args[1]))!;
            var prepared = await StrataPackService.PrepareAsync(o, Console.WriteLine);
            Console.WriteLine("Pack: " + prepared.PackDir);
            Console.WriteLine("Profile: " + prepared.ProfilePath);
            Console.WriteLine("Reused: " + prepared.Reused);
            return 0;
        }
        if (args.Contains("--ids") || args.Contains("--tokens-file") || args.Contains("--tokens") || args.Contains("--families"))
            return await StrataRegression.RunChildAsync(args);
        if (args.Contains("-m")) { await Fixtures.RunFakeServerAsync(args); return 0; }
        if (args.FirstOrDefault() == "--invoke-cli")
        {
            try { return await CommandLineService.RunAsync(args.Skip(1).ToArray(), Console.Out); }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.FirstOrDefault() == "--list-devices")
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "probe.count"), "device\n");
            Console.WriteLine("CUDA0: Fake GPU (6144 MiB, 5000 MiB free)"); return 0;
        }
        if (args.FirstOrDefault() == "--help")
        {
            Console.WriteLine("--fit [on|off] --flash-attn [on|off|auto]"); return 0;
        }
        if (args.FirstOrDefault() == "--version")
        {
            Console.WriteLine("version: 0.0.0-fixture (fake llama-server)"); return 0;
        }
        if (args.FirstOrDefault() == "--probe-hang") { await Task.Delay(60000); return 0; }
        if (args.FirstOrDefault() == "--probe-output")
        {
            await Task.WhenAll(Console.Out.WriteAsync(new string('a', 100000)), Console.Error.WriteAsync(new string('b', 100000))); return 0;
        }

        _root = Path.Combine(Directory.GetCurrentDirectory(), "logs", "regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("LLAMA_MANAGER_ROOT", _root);
        Fixtures.CopyRuntime(_root);
        Fixtures.CopyRuntime(Path.Combine(_root, "engines", "prism"));
        _model = Path.Combine(_root, "standard model.gguf");
        _prismModel = Path.Combine(_root, "renamed-special.gguf");
        File.WriteAllBytes(_model, Fixtures.Gguf(moe: false));
        File.WriteAllBytes(_prismModel, Fixtures.Gguf(prism: true, type: 143));
        Console.WriteLine("Isolated fixtures: " + _root);
        try
        {
            await Test("arguments: preserve prompt whitespace, aliases and independent threads", () => Run(() =>
            {
                var original = "--prompt \"a  b --port 99\" --threads-batch 3 --threads 6 --custom \"x  y\" -fa off --reasoning-budget 0";
                var changed = new LauncherArguments(original).Set("-t", "8");
                Equal(original.Replace("--threads 6", "-t 8"), changed);
                Equal("3", new LauncherArguments(changed).Get("-tb"));
                Equal("off", new LauncherArguments(changed).Get("-fa"));
                Equal("0", new LauncherArguments(changed).Get("--reasoning-budget"));
                Equal("--port", new LauncherArguments(new LauncherArguments("").Set("-p", "--port")).Get("-p"));
                Equal("a\"b", new LauncherArguments("--prompt \"a\"\"b\"").Get("-p"));
            }));
            await Test("arguments: default, -1, zero, duplicates and equals", () => Run(() =>
            {
                var args1 = new LauncherArguments("--reasoning-budget=-1 --port=9010 --gpu-layers 54 --flash-attn auto");
                Equal("-1", args1.Get("--reasoning-budget")); Equal(9010, args1.GetPort()); Equal("54", args1.Get("-ngl"));
                var args2 = new LauncherArguments("-t 4 --threads=6 --unknown \"x y\"");
                Equal("6", args2.Get("-t"));
                var updated = new LauncherArguments(args2.Set("--threads", "8"));
                Equal(1, updated.Values.Count(x => x == "-t")); Equal("8", updated.Get("-t"));
                Check(!new LauncherArguments(args1.Set("--reasoning-budget", null)).Has("--reasoning-budget"), "default did not remove option");
                Throws<ArgumentException>(() => new LauncherArguments("--port 0").GetPort());
                Throws<ArgumentException>(() => new LauncherArguments("--port 65536").GetPort());
                Throws<ArgumentException>(() => new LauncherArguments("--prompt \"unclosed"));
            }));
            await Test("arguments: Windows quote round trip", () => Run(() =>
            {
                foreach (var value in new[] { "", "abc", "a  b", "C:\\模型 路径\\", "x\\\"y", "x\\\\\"y", "\nnew\tline", "one\"two", "--literal" })
                    Equal(value, new LauncherArguments(LauncherArguments.Quote(value)).Values.Single());
                var random = new Random(12);
                const string chars = "ab \\\"\t中";
                for (var i = 0; i < 1000; i++)
                {
                    var value = new string(Enumerable.Range(0, random.Next(40)).Select(_ => chars[random.Next(chars.Length)]).ToArray());
                    Equal(value, new LauncherArguments(LauncherArguments.Quote(value)).Values.Single());
                }
            }));
            await Test("config: atomic saves and backup recovery preserve corrupt original", () => Run(() =>
            {
                var path = Path.Combine(_root, "store.json");
                var store = new ConfigStore(path);
                store.Save(new ManagerConfig { ModelPath = "first" });
                store.Update(c => c.ModelPath = "second");
                Equal("second", store.Read().ModelPath);
                File.WriteAllText(path, "{bad");
                Equal("first", store.Read().ModelPath);
                Check(store.RecoveryWarning != null, "no recovery warning");
                store.Update(c => c.Args = "-t 8");
                Equal("{bad", File.ReadAllText(Directory.GetFiles(_root, "store.json.*.corrupt").Single()));
                Equal("-t 8", store.Read().Args);
                File.WriteAllText(path, "null"); File.WriteAllText(path + ".bak", "null");
                Throws<InvalidDataException>(() => store.Read());
            }));
            await Test("config: narrow transactions preserve unknown user metadata", () => Run(() =>
            {
                var path = Path.Combine(_root, "extension-properties.json");
                File.WriteAllText(path, """{"Window":{"Width":900,"Height":720},"FutureMetadata":{"nested":[1,"keep",true]}}""");
                var store = new ConfigStore(path);
                store.Update(c => c.Port = 8088);
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                Equal(900, json.RootElement.GetProperty("Window").GetProperty("Width").GetInt32());
                Equal("keep", json.RootElement.GetProperty("FutureMetadata").GetProperty("nested")[1].GetString());
                Equal(8088, store.Read().Port);
            }));
            await Test("config: serialize concurrent updates and propagate write failures", async () =>
            {
                var path = Path.Combine(_root, "concurrent.json");
                var store = new ConfigStore(path);
                await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.Update(c => c.LastPid++))));
                Equal(20, store.Read().LastPid);
                using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Throws<IOException>(() => store.Update(c => c.LastPid++));
                Equal(0, Directory.GetFiles(_root, "concurrent.json.*.tmp").Length);
            });
            await Test("config: settings/runtime transactions do not clobber each other", () => Run(() =>
            {
                var config = Config(Fixtures.FreePort());
                ConfigService.SaveConfig(config);
                var stale = ConfigService.ReadConfig();
                var session = new ServerSession { ProcessId = 99, StartTimeUtcTicks = DateTime.UtcNow.Ticks, EnginePath = "x", ModelPath = _model, Port = 9000 };
                ConfigService.SaveSession(config, session);
                stale.Args = "-t 7"; ConfigService.SaveConfig(stale);
                Equal(session, ConfigService.ReadConfig().RunningServer);
                ConfigService.SaveSession(config, null);
                Equal("-t 7", ConfigService.ReadConfig().Args);
            }));
            await Test("strata: config persistence does not clobber sessions or main settings", () => Run(() =>
            {
                var config = Config(Fixtures.FreePort());
                ConfigService.SaveConfig(config);
                var session = new ServerSession { ProcessId = 77, StartTimeUtcTicks = DateTime.UtcNow.Ticks, EnginePath = "x", ModelPath = _model, Port = 9000 };
                ConfigService.SaveSession(config, session);
                config.Strata.PackageRoot = Path.Combine(_root, "strata-pkg");
                config.Strata.Prompt = "write a quicksort";
                config.Strata.ReserveMib = 800;
                config.Strata.Kv = "int8";
                ConfigService.SaveStrata(config);
                var reloaded = ConfigService.ReadConfig();
                Equal("write a quicksort", reloaded.Strata.Prompt);
                Equal(800, reloaded.Strata.ReserveMib);
                Equal(session, reloaded.RunningServer);
                // a stats-only transaction must not touch the running session identity
                ConfigService.SaveStrataStats("decode 15.6 tok/s");
                reloaded = ConfigService.ReadConfig();
                Equal("decode 15.6 tok/s", reloaded.Strata.LastStats);
                Equal(session, reloaded.RunningServer);
                ConfigService.SaveStrataSession(config, 4242, 1234567890, "strata.exe", running: true);
                var strataSession = ConfigService.ReadConfig().Strata;
                Equal(4242, strataSession.LastPid);
                Equal(1234567890L, strataSession.LastStartedUtcTicks);
                Equal("strata.exe", strataSession.LastEnginePath);
                ConfigService.SaveStrataSession(config, 0, 0, null, running: false);
                var cleared = ConfigService.ReadConfig().Strata;
                Equal(0, cleared.LastPid);
                Equal(0L, cleared.LastStartedUtcTicks);
                Equal("", cleared.LastEnginePath);
            }));
            await Test("strata: stats parsing extracts decode/prefill/cache/hits/pool", () => Run(() =>
            {
                var lines = new[]
                {
                    "strata: session is up (engine 0.1.39)",
                    "  decode                   256 tokens in 16312.5 ms  ->  15.69 tok/s",
                    "  prefill                 1038 tokens in 2818.0 ms  ->  368.35 tok/s  (time to first token 5615.0 ms)",
                    "  expert cache auto: 2.53 GiB free, 800 MiB reserved (+0 MiB for the draft head) -> 817 slots",
                    "  R4 expert-cache hits   1229 of 4090 = 0.3005      (0 admitted, 2861 refused, cache 100.0000% full)",
                    "  the CPU expert pool    43.683 ms/token over 40 layers (14 positions, 560 dispatches)",
                };
                var parsed = new[]
                {
                    Regex.Match(lines[1], @"decode\s+\d+ tokens in [\d.]+ ms\s+->\s+([\d.]+) tok/s"),
                    Regex.Match(lines[2], @"prefill\s+\d+ tokens in [\d.]+ ms\s+->\s+([\d.]+) tok/s"),
                    Regex.Match(lines[3], @"expert cache auto:\s*(.*)$"),
                    Regex.Match(lines[4], @"(R4 expert-cache hits .*)$"),
                    Regex.Match(lines[5], @"(the CPU expert pool .*)$"),
                };
                Check(parsed.All(m => m.Success), "a stats line did not match");
                Equal("15.69", parsed[0].Groups[1].Value);
                Equal("368.35", parsed[1].Groups[1].Value);
                Check(parsed[2].Groups[1].Value.Contains("817 slots"), "cache detail missing");
                Check(parsed[3].Groups[1].Value.StartsWith("R4"), "hits detail missing");
                Check(parsed[4].Groups[1].Value.StartsWith("the CPU"), "pool detail missing");
            }));
            await Test("strata: path resolution rejects missing package and falls back safely", () => Run(() =>
            {
                var config = new ManagerConfig();
                Throws<DirectoryNotFoundException>(() => StrataService.ResolvePaths(config));
                Check(StrataService.ResolvePathsSafe(config) == null, "safe resolution should return null");
                Check(!StrataService.IsRunning(config), "no process should be running");
            }));
            await Test("strata: subprocess output, MTP settings, failure and cancellation in both stages", () =>
                StrataRegression.RunAsync(_root, _model));
            await Test("strata: pack preparation refuses unsupported models and missing files", () =>
                StrataRegression.RunPackServiceAsync(_root));
            await Test("strata: q36 native path selects the engine path, args and output line", () =>
                StrataRegression.RunQ36Async(_root, _model));
            await Test("config: legacy sessions migrate into Running and legacy keys are deleted", () =>
                Run(MigrationRegression.RunMigrateAndDelete));
            await Test("config: an empty config invents no session", () =>
                Run(MigrationRegression.RunEmptyConfig));
            await Test("engines: llama.cpp backend adapts ServerService without changing identity", () =>
                EnginesRegression.RunLlamaCppAsync(_root, _model));
            await Test("engines: embedding lifecycle goes through the backend facade", () =>
                EnginesRegression.RunEmbeddingAsync(_root, _model));
            await Test("engines: registry resolution and GPU arbitration", () =>
                Run(EnginesRegression.RunRegistryAndArbiter));
            await Test("engines: port probe distinguishes busy, free and service ports", () =>
                Run(EnginesRegression.RunPortProbe));
            await Test("strata backend: launches serve.server with a generated runconfig", () =>
                StrataBackendRegression.RunLaunchAsync(_root, _model));
            await Test("strata backend: resolvers report honest problems", () =>
                Run(() => StrataBackendRegression.RunResolvers(_root)));
            await Test("strata backend: per-model presets persist across models", () =>
                Run(() => StrataBackendRegression.RunModelPresets(_root)));
            await Test("KVMem: independent registry and honest stock schemas", () =>
                Run(KvmemRegression.RunRegistryAndSchema));
            await Test("KVMem: exact GPU release assets and mandatory checksums", () =>
                Run(() => KvmemRegression.RunCatalog(_root)));
            await Test("KVMem: complete bundle layouts and isolated path resolution", () =>
                Run(() => KvmemRegression.RunLayoutsAndResolvers(_root)));
            await Test("KVMem: bounded bundled runtime probes and cancellation", () =>
                KvmemRegression.RunRuntimeProbeAsync(_root));
            await Test("KVMem: real native launch flags and effective defaults", () =>
                Run(() => KvmemRegression.RunLaunchDefaults(_root)));
            await Test("KVMem: configurable default output budget and model isolation", () =>
                Run(() => KvmemRegression.RunOutputLimit(_root)));
            await Test("GPU arbitration: stale records never block and live ones refuse a second engine", () =>
                Run(GpuArbitrationRegression.Run));
            await Test("GPU arbitration: backend start refuses a live GPU engine without launching", () =>
                GpuArbitrationRegression.RunStartRefusalAsync(_root));
            await Test("KVMem: browser opens the chat UI instead of the props JSON", () =>
                Run(() => KvmemRegression.RunWebUiUrl(_root)));
            await Test("KVMem: disabled paging leaves no stale native flags", () =>
                Run(() => KvmemRegression.RunDisabledZeroPollution(_root)));
            await Test("KVMem: reject invalid capacity, alignment, SSD and decode combinations", () =>
                Run(() => KvmemRegression.RunInvalidParameters(_root)));
            await Test("KVMem: matching projection uses bundled CPU vision", () =>
                Run(() => KvmemRegression.RunCpuVision(_root)));
            await Test("KVMem: unsupported model architectures and experts are blocked", () =>
                KvmemRegression.RunModelLimitsAsync(_root));
            await Test("KVMem: stock llama.cpp refuses legacy raw paging flags", () =>
                KvmemRegression.RunStockRawRejectionAsync(_root));
            await Test("KVMem: health 200 requires loaded model and matching native budgets", () =>
                KvmemRegression.RunReadinessAsync(_root));
            await Test("KVMem: verified installation preserves concurrent settings and rolls back failures", () =>
                KvmemAcquisitionRegression.RunInstallTransactionsAsync(_root));
            await Test("KVMem: unified extraction rejects unsafe archives and occupied destinations", () =>
                KvmemAcquisitionRegression.RunSafeExtractionAsync(_root));
            await Test("updates: real native build and driver version parsing", () =>
                Run(BuiltInUpdateRegression.RunVersionParsing));
            await Test("updates: pair native and CUDA runtime by release and driver", () =>
                Run(() => BuiltInUpdateRegression.RunManagedPlans(_root)));
            await Test("updates: parse exact release CUDA pins without executing source", () =>
                Run(BuiltInUpdateRegression.RunCudaWheelPins));
            await Test("updates: vision uses real no-argument runtime probe", BuiltInUpdateRegression.RunVisionProbeAsync);
            await Test("updates: latest or manual builds never trigger blind downloads", () =>
                BuiltInUpdateRegression.RunLatestAndManualAsync(_root));
            await Test("updates: verified llama installation preserves identity and rolls back failures", () =>
                BuiltInUpdateRegression.RunLlamaTransactionsAsync(_root));
            await Test("updates: all builtins share failure reporting and cancellation", () =>
                BuiltInUpdateRegression.RunUpdateAllAsync(_root));
            await Test("retention: current and newest previous owned version", () =>
                Run(() => EngineRetentionRegression.RunKeepCurrentAndNewestPrevious(_root)));
            await Test("retention: semantic ordering, repair revisions and GPU isolation", () =>
                Run(() => EngineRetentionRegression.RunVersionOrderingAndMultiGpu(_root)));
            await Test("retention: protect explicit paths and verified live sessions", () =>
                Run(() => EngineRetentionRegression.RunProtectedRootsAndSessions(_root)));
            await Test("retention: require matching ownership and safe paths", () =>
                Run(() => EngineRetentionRegression.RunOwnershipAndPathSafety(_root)));
            await Test("retention: never follow links or remove model/config/log data", () =>
                Run(() => EngineRetentionRegression.RunReparseAndUserDataSafety(_root)));
            await Test("retention: clean only owned unchanged download assets", () =>
                Run(() => EngineRetentionRegression.RunOwnedDownloadRetention(_root)));
            await Test("retention: failures and cancellation preserve committed versions", () =>
                Run(() => EngineRetentionRegression.RunFailuresAndCancellation(_root)));
            await Test("plugins: manifest parse, validation and argument templating", () =>
                Run(() => PluginRegression.RunManifest(_root)));
            await Test("plugins: discovery rejects built-in id collisions", () =>
                Run(() => PluginRegression.RunDiscovery(_root)));
            await Test("download: release-to-plan mapping and repo routing", () =>
                Run(DownloadRegression.RunCatalog));
            await Test("download: extract, zip-slip guard and checksum enforcement", () =>
                DownloadRegression.RunDownloadMechanicsAsync(_root));
            await Test("download: same-target failure, cancellation and two valid transfers preserve ownership", () => DownloadConcurrencyRegression.RunOwnershipAsync(_root));
            await Test("download: publication retry policy is bounded and cancellation-aware", () => DownloadConcurrencyRegression.RunPublicationPolicyAsync(_root));
            await Test("download: real Windows target lock resumes verified HTTP publication", () => DownloadConcurrencyRegression.RunWindowsLockAsync(_root));
            await Test("download: real GitHub release dumps map to install plans", () =>
                Run(() => DownloadRegression.RunAgainstRealReleases(_root)));
            await Test("doctor: version comparison across release schemes", () =>
                Run(DoctorRegression.RunVersionComparison));
            await Test("doctor: local checks, update detection and network degradation", () =>
                DoctorRegression.RunLocalChecksAsync(_root));
            await Test("python: install-name parsing and newest-install selection", () => Run(() =>
            {
                PythonRegression.RunVersionParsing();
                PythonRegression.RunCandidateSelection();
            }));
            await Test("python: silent user-scope install arguments and pinned fallback", () =>
                Run(PythonRegression.RunArgumentShapes));
            await Test("python: auto-install routes, offline and failure degradation", () =>
                PythonRegression.RunEnsureAsync());
            await Test("python: doctor points at the automatic install", () =>
                Run(PythonRegression.RunDoctorActionMentionsInstall));
            await Test("compatibility: every backend reports a verdict and estimate", () =>
                CompatibilityRegression.RunAsync(_root));
            await Test("CLI: backend routing, help and bootstrap requirements", () =>
                CliRegression.RunAsync(_root));
            await Test("strata: compatibility roles, native heads, required tensors and MTP structure", () =>
                Run(StrataCompatibilityRegression.RunFormats));
            await Test("models: switching restores per-engine per-model configuration", () => Run(() => EngineModelIsolationRegression.RunModelSwitchIsolation(_root)));
            await Test("models: new model defaults and single-field edits never leak", () => Run(() => EngineModelIsolationRegression.RunNewModelDefaultsAndParameterIsolation(_root)));
            await Test("models: recommendation apply is scoped and rejects concurrent edits", () => Run(() => EngineModelIsolationRegression.RunRecommendationCasIsolation(_root)));
            await Test("hardware configuration: exact prepared parameters and current-model CAS", () => Run(() => KvmemHardwareConfigurationRegression.Run(_root)));
            await Test("removed feature: no KVMem benchmarking CLI or recommendation cache", () => RemovedKvmemBenchmarkRegression.RunAsync(_root));
            await Test("parameters: nearest legal multiples are safe and shared", () => Run(ParameterNormalizationRegression.Run));
            await Test("hardware: model-scoped conservative recommendation policy", () => Run(HardwareRecommendationRegression.Run));
            await Test("hardware: cached bounded probes and cancellation", HardwareRecommendationRegression.RunProbeAsync);
            await Test("parameters: VisibleWhen grammar hides, groups and never throws", () =>
                Run(ParameterVisibilityRegression.Run));
            await Test("schema: help, tooltip and range hints fall back in order", () =>
                Run(CoreUtilitiesRegression.RunParameterSpec));
            await Test("paths: normalization and equality collapse case and separators", () =>
                Run(CoreUtilitiesRegression.RunPathIdentity));
            await Test("models: display name, size units, category and MoE detection", () =>
                Run(CoreUtilitiesRegression.RunModelInfo));
            await Test("llama.cpp: argument projection round trip keeps hand-written flags", () =>
                Run(CoreUtilitiesRegression.RunLlamaCppRoundTrip));
            await Test("model pairing: automatically detects paired mmproj and draft/mtp models", () =>
                Run(() => CoreUtilitiesRegression.RunModelPairing(_root)));
            await Test("strata: GPU summary parsing and PolyStrata compatibility verdicts", () =>
                Run(CoreUtilitiesRegression.RunStrataDiagnostics));
            await Test("strata: tokenizer path shape and port precedence", () =>
                Run(CoreUtilitiesRegression.RunStrataPaths));
            await Test("backends: status factories, resource estimate floor and crash log", () =>
                Run(CoreUtilitiesRegression.RunBackendTypesAndDiagnostics));
            await Test("strata: complete GGUF shards, duplicate detection and cancellation", () =>
                Run(() => StrataCompatibilityRegression.RunShards(_root)));
            await Test("signatures: normalized paths, LRU bound, version replacement and failed-read recovery", () => Run(() => FileSignatureRegression.RunPolicies(_root)));
            await Test("signatures: preserve head/tail format and renamed-model identity", () => Run(() => FileSignatureRegression.RunFormat(_root)));
            await Test("signatures: share concurrent work and reject changed-file observations", () => FileSignatureRegression.RunConcurrencyAsync(_root));
            await Test("presets: reject a known signature mismatch on identical names", () => Run(() =>
            {
                var config = new ManagerConfig();
                var old = Path.Combine(_root, "gone", Path.GetFileName(_model));
                config.ModelPresets[old] = new ModelPreset { Args = "wrong", Signature = "different-signature" };
                Equal(0, ConfigService.RebindOrphanPresets(config, [_model], false));
                config.ModelPresets[old].Signature = FileSignature.Compute(_model);
                Equal(1, ConfigService.RebindOrphanPresets(config, [_model], false));
                Equal("wrong", config.ModelPresets[PathIdentity.Normalize(_model)].Args);
            }));
            await Test("GGUF: unordered metadata, real layers and renamed Prism routing", () => Run(() =>
            {
                using var stream = new MemoryStream(Fixtures.Gguf(prism: true, type: 34));
                var meta = GgufService.Read(stream);
                Check(meta.IsMoE && meta.HasHadamard && meta.HasTq1 && meta.RequiresPrism, "metadata lost");
                Equal(64, meta.LayerCount); Equal(8192, meta.ContextLength); Equal(stream.Length, stream.Position);
                Check(ConfigService.IsPrismModel(_prismModel), "renamed Prism model not detected");
                Equal("qwen3", GgufService.GetArchitecture(_model));
                var misleading = Path.Combine(_root, "bonsai-dense.gguf");
                File.WriteAllBytes(misleading, Fixtures.Gguf(moe: false));
                Check(!ConfigService.IsPrismModel(misleading), "filename overrode valid metadata");
                using var mtpStream = new MemoryStream(Fixtures.Gguf(moe: false, mtp: true));
                Check(GgufService.Read(mtpStream).HasMtp, "bundled MTP head not detected");
                using var plainStream = new MemoryStream(Fixtures.Gguf(moe: false));
                Check(!GgufService.Read(plainStream).HasMtp, "plain model flagged as MTP");
            }));
            await Test("GGUF: large string arrays are skipped without losing alignment", () => Run(() =>
            {
                using var stream = new MemoryStream(Fixtures.Gguf(moe: false, vocabSize: 5000));
                var meta = GgufService.Read(stream);
                Equal(64, meta.LayerCount); Equal(8192, meta.ContextLength);
                Equal(stream.Length, stream.Position); // consumed exactly the metadata, nothing more
            }));
            await Test("GGUF: large string arrays are read in bulk, not per string", () => Run(() =>
            {
                using var counting = new Fixtures.CountingStream(new MemoryStream(Fixtures.Gguf(moe: false, vocabSize: 5000)));
                Equal(64, GgufService.Read(counting).LayerCount);
                Check(counting.ReadCalls < 50, $"metadata parsing issued {counting.ReadCalls} stream reads");
            }));
            await Test("GGUF: truncated and invalid data are bounded; changed files invalidate cache", () => Run(() =>
            {
                var bytes = Fixtures.Gguf();
                for (var length = 0; length < bytes.Length; length += 13)
                {
                    using var stream = new MemoryStream(bytes[..length]);
                    try { GgufService.Read(stream); throw new Exception("truncated GGUF accepted"); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException) { }
                }
                var path = Path.Combine(_root, "cache.gguf");
                File.WriteAllBytes(path, bytes); Equal(64, GgufService.TryRead(path)!.LayerCount);
                File.WriteAllBytes(path, Fixtures.Gguf(layers: 32)); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
                Equal(32, GgufService.TryRead(path)!.LayerCount);
                File.WriteAllBytes(path, new byte[24]); Check(GgufService.TryRead(path) == null, "invalid header accepted");
            }));
            await Test("preset: actual layers, KV reserve and engine capabilities", () => Run(() =>
            {
                var meta = new GgufMetadata { LayerCount = 64, ContextLength = 2048, AttentionHeads = 32, KvHeads = 8, EmbeddingLength = 4096 };
                var device = new CudaInfo { Available = true, FreeVramMb = 5000 };
                var args1 = new LauncherArguments(PresetService.Plan(meta, 6L << 30, device));
                Check(int.Parse(args1.Get("-ngl")!) is > 24 and < 64, "actual 64 layers not used");
                Equal("2048", args1.Get("-c")); Check(!args1.Has("--fit"), "unsupported fit passed");
                var args2 = new LauncherArguments(PresetService.Plan(meta, 6L << 30, device with { SupportsFit = true }));
                Equal("on", args2.Get("--fit")); Equal("999", args2.Get("-ngl"));
                Equal("0", new LauncherArguments(PresetService.Plan(meta, 6L << 30, new CudaInfo())).Get("-ngl"));
            }));
            await Test("preset: MoE too big for the card offloads experts instead of truncating layers", () => Run(() =>
            {
                // 13 GiB MoE on a 6 GiB card: the old plan divided layers by whole-file size and
                // produced a near-zero -ngl; the expert-aware plan offloads everything instead.
                // Geometry mirrors the real 35B-A3B family the calibration came from (GQA, 4 KV heads).
                var moe = new GgufMetadata { LayerCount = 48, ContextLength = 131072, AttentionHeads = 32, KvHeads = 4, EmbeddingLength = 4096, IsMoE = true, ExpertCount = 128, ExpertUsedCount = 8 };
                var device = new CudaInfo { Available = true, FreeVramMb = 5200 };
                var args = new LauncherArguments(PresetService.Plan(moe, 13L << 30, device));
                Equal("999", args.Get("-ngl"));
                Check(int.TryParse(args.Get("--n-cpu-moe"), out var off) && off is >= 1 and <= 48,
                    $"expert offload layers out of range: {args.Get("--n-cpu-moe")}");
                // Calibrated against the measured 6 GiB boundary (~26 runs, ~30 is fastest):
                // the formula must land between them, near the fast setting.
                Check(off is >= 26 and <= 32, $"offload estimate drifted: {off}");
                // The ratio is calibrated: 13 GiB × 0.91 experts − (5.2 − 0.77 KV) GiB room,
                // over 48 layers → ~28, inside the measured 26 (runs) … 30 (fastest) band.
                Check(off is >= 26 and <= 32, $"offload estimate drifted: {off}");
                // A MoE that fits the card must not carry --n-cpu-moe at all.
                var fits = new LauncherArguments(PresetService.Plan(moe, 13L << 30, new CudaInfo { Available = true, FreeVramMb = 16000 }));
                Check(!fits.Has("--n-cpu-moe"), "offload flag set although the model fits");
                Check(int.Parse(fits.Get("-ngl")!) > 48, "a fitting MoE should offload everything");
                // Dense models keep the KV-aware plan (no expert flag either way).
                var dense = new GgufMetadata { LayerCount = 48, ContextLength = 4096, AttentionHeads = 32, KvHeads = 8, EmbeddingLength = 4096 };
                var denseArgs = new LauncherArguments(PresetService.Plan(dense, 13L << 30, device));
                Check(!denseArgs.Has("--n-cpu-moe"), "dense model got the expert flag");
                Check(int.Parse(denseArgs.Get("-ngl")!) < 48, "dense oversized model should truncate layers");
            }));
            await Test("probe: stdout/stderr drained concurrently and real timeout enforced", async () =>
            {
                var output = await ProcessProbe.RunAsync(Environment.ProcessPath!, "--probe-output", TimeSpan.FromSeconds(5));
                Check(output.Contains(new string('a', 1000)) && output.Contains(new string('b', 1000)), "pipe output missing");
                var clock = Stopwatch.StartNew();
                try { await ProcessProbe.RunAsync(Environment.ProcessPath!, "--probe-hang", TimeSpan.FromMilliseconds(300)); throw new Exception("hang was not cancelled"); }
                catch (OperationCanceledException) { Check(clock.Elapsed < TimeSpan.FromSeconds(3), "timeout ineffective"); }
            });
            await Test("CUDA: concurrent callers share one probe per engine; explicit invalidation", async () =>
            {
                CudaService.Invalidate();
                var file = Path.Combine(_root, "probe.count");
                var values = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => CudaService.DetectAsync(_model)));
                Check(values.All(v => v.Available && v.SupportsFit && v.SupportsFlash && v.FreeVramMb == 5000), "bad CUDA parsing");
                Equal(1, File.ReadAllLines(file).Length);
                await CudaService.DetectAsync(_prismModel);
                Equal(1, File.ReadAllLines(Path.Combine(_root, "engines", "prism", "probe.count")).Length);
                CudaService.Invalidate(); await CudaService.DetectAsync(_model);
                Equal(2, File.ReadAllLines(file).Length);
            });
            await Test("CUDA: deterministic TTL refresh shares in-flight probes and expires failures", CudaCacheRegression.RunAsync);
            await Test("CUDA: device-list parsing survives hostile or malformed numbers", () => Run(() =>
            {
                var normal = CudaService.Parse("CUDA0: NVIDIA GeForce RTX 3060 Laptop GPU (6144 MiB, 5000 MiB free)");
                Check(normal.Available && normal.TotalVramMb == 6144 && normal.FreeVramMb == 5000, "normal device line misparsed");
                // External output is untrusted: overflow and absurd values must degrade to "unknown".
                var overflow = CudaService.Parse("CUDA0: GPU Pad (99999999999999999999 MiB, 5000 MiB free)");
                Equal(0, overflow.TotalVramMb);
                Equal(5000, overflow.FreeVramMb);
                var absurd = CudaService.Parse("CUDA0: GPU Pad (0 MiB, 0 MiB free)");
                Equal(0, absurd.TotalVramMb);
                Check(absurd.Available, "a zero-capacity line lost the availability flag");
                Equal(0, CudaService.Parse("").TotalVramMb);
                Check(!CudaService.Parse("no devices").Available, "a text without a CUDA line claimed availability");
            }));
            await Test("log cache: recover requested history and protect shared small consumers", () => Run(() => LogCacheRegression.RunCounts(_root)));
            await Test("log cache: normalize aliases, evict LRU paths and retry failures", () => Run(() => LogCacheRegression.RunCachePolicy(_root)));
            await Test("log cache: preserve partial UTF8 and deterministic rotation recovery", () => Run(() => LogCacheRegression.RunIncremental(_root)));
            await Test("log cache: preserve cold byte budget and bounded partial lines", () => Run(() => LogCacheRegression.RunByteBudget(_root)));
            await Test("logs: incremental reads, partial UTF8, truncation and actual GPU buffers", () => Run(() =>
            {
                var file = Path.Combine(_root, "incremental.log");
                var reader = new IncrementalLogReader();
                File.WriteAllText(file, "first\n"); Equal("first", reader.Read(file).Single());
                var before = reader.BytesRead; reader.Read(file); Equal(before, reader.BytesRead);
                var utf8 = Encoding.UTF8.GetBytes("中文\n");
                using (var stream = new FileStream(file, FileMode.Append)) stream.Write(utf8[..2]);
                Equal(1, reader.Read(file).Count);
                using (var stream = new FileStream(file, FileMode.Append)) stream.Write(utf8[2..]);
                Equal("中文", reader.Read(file).Last());
                File.WriteAllText(file, "new\n"); Equal("new", reader.Read(file).Single());
                var diagnostic = LogService.ParseGpuSummary(["offloaded 64/65 layers to GPU", "CUDA0 model buffer size = 12.34 MiB", "CPU_Mapped model buffer size = 5600.00 MiB"]);
                Check(diagnostic.Contains("GPU 权重 12.34") && diagnostic.Contains("CPU 权重 5600"), "diagnostic hides CPU fallback");
                var strataDiag = LogService.ParseStrataGpuSummary(["strata-poly: loaded in 7.7 s; 2162 of 10496 routed experts in VRAM (2.5 GiB), 16 CPU threads"]);
                Check(strataDiag.Contains("2162/10496") && strataDiag.Contains("2.5 GiB"), "strata diagnostic parses expert counts");
                var shrunk = LogService.ParseStrataGpuSummary(["strata-poly: the expert cache 2162 -> 2069 experts (2.25 GiB) for the VRAM free now, 288 MiB left"]);
                Check(shrunk.Contains("2069") && shrunk.Contains("2.25 GiB"), "strata diagnostic parses a shrunk expert cache");
                Check(LogService.GetPrimaryLogPath("polystrata").EndsWith("engine-polystrata.log"), "polystrata log path matches");
            }));
            await Test("speed monitor: supports object/array counters, ignores idle stale timings", () => Run(() =>
            {
                Check(SpeedMonitorService.Parse("[{\"is_processing\":true,\"next_token\":{\"n_decoded\":42}}]").Contains("42"), "object schema");
                Check(SpeedMonitorService.Parse("[{\"is_processing\":true,\"next_token\":[{\"n_decoded\":24}]}]").Contains("24"), "array schema");
                Equal("", SpeedMonitorService.Parse("[{\"is_processing\":false,\"timings\":{\"predicted_per_second\":999}}]"));
            }));
            await Test("speed monitor: counter sampling and request/session reset", () => Run(() =>
            {
                var monitor = new SpeedMonitorService();
                var session = new ServerSession { ProcessId = 1, StartTimeUtcTicks = 100 };
                string Snapshot(int task, int count) => $"[{{\"id\":0,\"id_task\":{task},\"is_processing\":true,\"next_token\":[{{\"n_decoded\":{count}}}]}}]";
                Check(monitor.Observe(session, Snapshot(6, 10), [], TimeSpan.Zero).Text.Contains("采样"), "first sample fabricated speed");
                Check(monitor.Observe(session, Snapshot(6, 50), [], TimeSpan.FromSeconds(2)).Text.Contains("20.00 tok/s"), "counter delta speed missing");
                Check(monitor.Observe(session, Snapshot(7, 100), [], TimeSpan.FromSeconds(4)).Text.Contains("采样"), "new request reused previous counter");
                Equal(SpeedStatus.Empty, monitor.Observe(session with { ProcessId = 2 }, "[]", [], TimeSpan.FromSeconds(6)));
            }));
            await Test("speed monitor: decode log retained when idle or API unavailable, prompt speed excluded", () => Run(() =>
            {
                var monitor = new SpeedMonitorService();
                var session = new ServerSession { ProcessId = 1 };
                const string decode = "0.21.829.709 I slot print_timing: id 0 | task 6 | eval time = 10950.98 ms / 256 tokens (42.95 ms per token, 23.29 tokens per second)";
                const string prompt = "0.21.829.706 I slot print_timing: id 0 | task 6 | prompt eval time = 319.06 ms / 39 tokens (8.18 ms per token, 122.23 tokens per second)";
                Equal(null, SpeedMonitorService.ParseDecodeTiming(prompt));
                const string strataDone = "[strata] done: 500 tokens in 9.8 s (50.9 tok/s) (stop, cancel=False)";
                const string strataReq = "[strata] request prompt 24 cached 0 output 500 prompt_read 35 ms total 9855 ms prefill 685.7 tok/s decode 50.9 tok/s";
                Equal(50.9, SpeedMonitorService.ParseDecodeTiming(strataDone)?.Rate);
                Equal(500, SpeedMonitorService.ParseDecodeTiming(strataDone)?.Tokens);
                Equal(50.9, SpeedMonitorService.ParseDecodeTiming(strataReq)?.Rate);
                Equal(500, SpeedMonitorService.ParseDecodeTiming(strataReq)?.Tokens);
                Check(monitor.Observe(session, "[{\"is_processing\":false}]", [prompt, decode], TimeSpan.Zero).Text.Contains("上次速度：23.29 tok/s"), "idle completion disappeared");
                Check(monitor.Observe(session, null, [], TimeSpan.FromSeconds(2)).Text.Contains("23.29"), "API outage hid completed speed");
                Check(monitor.Observe(session, "bad json", [], TimeSpan.FromSeconds(4)).Text.Contains("23.29"), "malformed snapshot hid completed speed");
            }));
            await Test("speed monitor: API rates and prompt progress", () => Run(() =>
            {
                var monitor = new SpeedMonitorService();
                var session = new ServerSession();
                var active = "[{\"is_processing\":true,\"n_decoded\":42,\"timings\":{\"predicted_per_second\":12.5}}]";
                Check(monitor.Observe(session, active, [], TimeSpan.Zero).Text.Contains("12.50 tok/s"), "server rate missing");
                var prompt = "[{\"is_processing\":true,\"n_prompt_tokens\":100,\"n_prompt_tokens_processed\":20,\"n_prompt_tokens_cache\":30}]";
                Check(monitor.Observe(session, prompt, [], TimeSpan.FromSeconds(2)).Text.Contains("50/100"), "prompt progress missing");
            }));
            await Test("speed logs: polling noise cannot evict the last completion", () => Run(() =>
            {
                var path = Path.Combine(_root, "speed-log.log");
                File.WriteAllText(path, "eval time = 1000 ms / 20 tokens (50 ms per token, 20.00 tokens per second)\n" + string.Concat(Enumerable.Repeat("GET /slots noise\n", 1000)));
                var reader = new IncrementalLogReader();
                Equal(1, reader.Read(path, 20, line => SpeedMonitorService.ParseDecodeTiming(line) != null).Count);
                File.AppendAllText(path, string.Concat(Enumerable.Repeat("GET /slots noise\n", 2000)));
                Equal(1, reader.Read(path, 20, line => SpeedMonitorService.ParseDecodeTiming(line) != null).Count);
            }));
            await Test("speed monitor: samples decode rate, rebaselines after gaps, reports kinds", () => Run(() =>
            {
                var monitor = new SpeedMonitorService();
                var session = new ServerSession { Port = 8080 };
                var t0 = TimeSpan.FromSeconds(1000);
                string Slot(int decoded, int task = 7) =>
                    $"{{\"id\":0,\"is_processing\":true,\"id_task\":{task},\"n_decoded\":{decoded}}}";
                var first = monitor.Observe(session, "[" + Slot(100) + "]", [], t0);
                Check(first.Kind == SpeedKind.Generating && first.Text.Contains("100") && first.Text.Contains("采样中"), "first poll shows count without rate, got: " + first.Text);
                var second = monitor.Observe(session, "[" + Slot(300) + "]", [], t0 + TimeSpan.FromSeconds(2));
                Check(second.Text.Contains("约 100.00 tok/s"), "sampled rate over 2s, got: " + second.Text);
                Equal("", monitor.Observe(session, null, [], t0 + TimeSpan.FromSeconds(4)).Text);
                var rebaselined = monitor.Observe(session, "[" + Slot(500) + "]", [], t0 + TimeSpan.FromSeconds(6));
                Check(rebaselined.Text.Contains("采样中"), "must rebaseline after a gap instead of diluting, got: " + rebaselined.Text);
                var recovered = monitor.Observe(session, "[" + Slot(700) + "]", [], t0 + TimeSpan.FromSeconds(8));
                Check(recovered.Text.Contains("约 100.00 tok/s"), "rate resumes after rebaseline, got: " + recovered.Text);
                var prompt = monitor.Observe(session, "[{\"id\":0,\"is_processing\":true,\"id_task\":8,\"n_decoded\":0,\"n_prompt_tokens\":1000,\"n_prompt_tokens_processed\":400}]", [], t0 + TimeSpan.FromSeconds(10));
                Check(prompt.Kind == SpeedKind.Prompt && prompt.Text.Contains("400"), "prompt progress, got: " + prompt.Text);
                var done = monitor.Observe(session, "[]", ["I slot print_timing: id 0 | task 8 | eval time = 1000.00 ms / 50 tokens ( 20.00 ms per token, 50.00 tokens per second)"], t0 + TimeSpan.FromSeconds(12));
                Check(done.Kind == SpeedKind.Completed && done.Text.Contains("上次速度") && done.Text.Contains("50.00 tok/s"), "completed timing, got: " + done.Text);
            }));
            await Test("poly metrics: parses live state, finished requests and hardware", () => Run(() =>
            {
                const string generating =
                    """
                    {"live":{"state":"generating","queued":0,"phase":"thinking","prompt_tokens":23,"prompt_read":null,
                      "prompt_total":null,"generated":115,"max_tokens":300,"elapsed_s":4.8,"tok_s":45.9,"tok_s_mean":36.4,
                      "prefill_tok_s_mean":14.6,"tok_s_window_s":2.0},
                     "requests":[{"time":1792321007.33,"duration_s":8.1,"finish":"length","prompt_tokens":23,"reused":0,
                      "output_tokens":300,"prompt_total":23,"prompt_read":23,"engine_generated":300,"prompt_ms":1573.7,
                      "decode_ms":6532.5,"decode_tok_s":45.9,"hit_rate":0.721,"drafts_offered":180,"drafts_accepted":157},
                      {"time":1792320900.0,"duration_s":1.0,"finish":"stop","prompt_tokens":5,"output_tokens":9,
                       "decode_tok_s":10.0,"prompt_ms":1.0,"drafts_offered":0,"drafts_accepted":0}],
                     "engine":{"model":"ornith","max_context":131072,"expert_slots":2162,"expert_cache_mib":2521,
                      "pool_workers":8,"vram_free_mib":254},
                     "hardware":{"gpu_util":80,"gpu_mem_used":5368709120,"gpu_mem_total":6442450944,"gpu_temp":52,
                      "gpu_power":85.6,"cpu":40.0,"ram_used":22548578304,"ram_total":25130119168}}
                    """;
                var metrics = PolyMetricsService.Parse(generating);
                Check(metrics != null, "payload rejected");
                Check(metrics!.Live.IsGenerating, "generating state missed");
                Equal("thinking", metrics.Live.Phase);
                Equal(115, metrics.Live.Generated);
                Equal(45.9, metrics.Live.TokensPerSecond);
                var latest = metrics.LastRequest;
                Check(latest != null && latest.OutputTokens == 300 && Math.Abs(latest.DecodeTokensPerSecond - 45.9) < 1e-9,
                    "the newest request must win, not the first array entry");
                Equal(157, latest!.DraftsAccepted);
                Check(Math.Abs(latest.DraftAcceptance!.Value - 157.0 / 180) < 1e-9, "draft acceptance wrong");
                Check(metrics.Engine is { ExpertSlots: 2162, ExpertCacheMib: 2521, VramFreeMib: 254, Workers: 8 }, "engine block lost");
                Check(metrics.Hardware is { GpuMemoryTotal: 6442450944, GpuUtilization: 80, GpuTemperature: 52 }, "hardware block lost");
                var detail = PolyMetricsService.Describe(metrics);
                Check(detail.Contains("2,162 专家") && detail.Contains("2,521 MiB") && detail.Contains("5.0/6.0 GiB"),
                    "telemetry line incomplete: " + detail);

                const string reading =
                    """{"live":{"state":"reading","phase":"reading the prompt","prompt_tokens":3020,"generated":0,"elapsed_s":0.1,"tok_s":null}}""";
                var readingState = PolyMetricsService.Parse(reading)!.Live;
                Check(readingState.IsReading && !readingState.IsGenerating, "reading state missed");
                Equal(3020, readingState.PromptTokens);

                const string idle =
                    """{"live":{"state":"idle","queued":0,"phase":null,"prompt_tokens":null,"generated":null,"tok_s":null}}""";
                var idleState = PolyMetricsService.Parse(idle)!.Live;
                Check(idleState.IsIdle, "idle state missed");
                Equal("作答中", PolyMetricsService.DescribePhase("writing"));
                Equal("思考中", PolyMetricsService.DescribePhase("thinking"));
                Equal(null, PolyMetricsService.Parse("not json"));
                Equal(null, PolyMetricsService.Parse("{}"));
                Equal(null, PolyMetricsService.Parse(""));
            }));
            await Test("speed monitor: metrics drive generation, queue and completed speed", () => Run(() =>
            {
                var monitor = new SpeedMonitorService();
                const string generating =
                    """{"live":{"state":"generating","queued":0,"phase":"writing","prompt_tokens":23,"generated":115,"max_tokens":300,"elapsed_s":4.8,"tok_s":45.9}}""";
                var active = monitor.ObserveMetrics(PolyMetricsService.Parse(generating)!);
                Check(active.Kind == SpeedKind.Generating, "kind wrong: " + active.Kind);
                Check(active.Text.Contains("45.9 tok/s") && active.Text.Contains("115/300") && active.Text.Contains("作答中"),
                    "live line incomplete: " + active.Text);
                Check(active.Text.Contains("4.8 s"), "elapsed missing: " + active.Text);
                Check(active.IsActive, "active flag lost");

                const string queued = """{"live":{"state":"idle","queued":1,"generated":0}}""";
                var waiting = monitor.ObserveMetrics(PolyMetricsService.Parse(queued)!);
                Check(waiting.Kind == SpeedKind.Prompt && waiting.Text.Contains("排队"), "queue state missed: " + waiting.Text);

                const string done =
                    """
                    {"live":{"state":"idle","generated":null,"tok_s":null},
                     "requests":[{"time":1792321007.33,"duration_s":8.1,"finish":"stop","prompt_tokens":23,
                      "output_tokens":300,"decode_tok_s":45.9,"prompt_ms":1573.7,"drafts_offered":180,"drafts_accepted":157}],
                     "engine":{"expert_slots":2162,"vram_free_mib":254}}
                    """;
                var completed = monitor.ObserveMetrics(PolyMetricsService.Parse(done)!);
                Check(completed.Kind == SpeedKind.Completed, "completed kind wrong: " + completed.Kind);
                Check(completed.Text.Contains("上次速度：45.90 tok/s") && completed.Text.Contains("300 tokens"),
                    "completed line incomplete: " + completed.Text);
                Check(completed.Sample is { } sample && sample.Stamp == 1792321007.33 && sample.Tokens == 300 &&
                    Math.Abs(sample.Rate - 45.9) < 1e-9, "sample payload lost");
                Check(completed.Sample!.DraftAcceptance is { } acceptance && Math.Abs(acceptance - 0.87) < 0.005,
                    "draft acceptance lost");
                Check(completed.Detail.Contains("2,162 专家"), "detail lost on completed status: " + completed.Detail);

                // A service without a finished request stays quiet instead of reporting stale numbers.
                var quiet = monitor.ObserveMetrics(PolyMetricsService.Parse("""{"live":{"state":"idle"}}""")!);
                Check(quiet.Kind == SpeedKind.Idle && quiet.Text.Length == 0, "idle with no history must be empty: " + quiet.Text);

                // The moment between "reading" and the first token still counts as generating.
                var starting = monitor.ObserveMetrics(PolyMetricsService.Parse(
                    """{"live":{"state":"generating","queued":0,"phase":"thinking","prompt_tokens":23,"generated":0,"max_tokens":-1,"elapsed_s":0.0,"tok_s":null}}""")!);
                Check(starting.Kind == SpeedKind.Generating, "generated=0 must still be generating, got: " + starting.Kind);
                Check(starting.Text.Contains("0 tokens") && !starting.Text.Contains("/0"), "unbounded max_tokens must not render /0: " + starting.Text);
            }));
            await Test("session validation: a stale record is not a running service", () => Run(() =>
            {
                var current = Process.GetCurrentProcess();
                var alive = new ServerSession
                {
                    ProcessId = current.Id,
                    StartTimeUtcTicks = current.StartTime.ToUniversalTime().Ticks,
                    EnginePath = current.MainModule?.FileName ?? "",
                };
                Check(ServerService.IsSessionAlive(alive), "the test host must be recognised");
                Check(!ServerService.IsSessionAlive(alive with { ProcessId = current.Id + 4000 }), "an unknown PID passed");
                Check(!ServerService.IsSessionAlive(alive with { StartTimeUtcTicks = 1 }), "a mismatched start time passed");
                Check(!ServerService.IsSessionAlive(null), "null passed");
                Check(!ServerService.IsSessionAlive(new ServerSession()), "a record without PID passed");
            }));
            await Test("model catalog: URLs, ordering, fit and the download guard", () => Run(() =>
            {
                var list = ModelCatalog.Recommendations;
                Check(list.Count >= 4, "catalogue too small for a first-run picker");
                Check(list.SequenceEqual(list.OrderBy(m => m.VramMb)), "catalogue must be ordered smallest-first");
                Check(list.Select(m => m.FileName).Distinct().Count() == list.Count, "duplicate file names");
                // Sizes are the exact byte counts read from the CDN; a hand-edited round number
                // would break the "same size = already downloaded" check.
                Check(list.All(m => m.Bytes % (1L << 20) != 0), "byte counts look hand-rounded");
                foreach (var m in list)
                {
                    Check(m.Url.StartsWith("https://huggingface.co/") && m.Url.Contains("/resolve/main/") &&
                        m.Url.EndsWith(m.FileName), $"malformed URL: {m.Url}");
                    Check(m.Bytes > 1_000_000, $"implausible size for {m.FileName}");
                    Check(!string.IsNullOrWhiteSpace(m.Note) && !string.IsNullOrWhiteSpace(m.Repo), "row missing note or repo");
                }
                var smallest = list[0];
                Check(smallest.Fits(0), "unknown VRAM must not hide the smallest model");
                Check(!smallest.Fits(64), "a 64 MiB card cannot fit anything");
                Check(list[^1].Fits(list[^1].VramMb * 2), "double the stated VRAM must fit");

                // The GGUF guard rejects an HTML error page and accepts the real magic.
                var junk = Path.Combine(_root, "junk.gguf");
                File.WriteAllText(junk, "<html><body>Not Found</body></html>");
                Throws<IOException>(() => ModelDownloadService.VerifyGguf(junk));
                ModelDownloadService.RemoveInvalid(junk);
                Check(!File.Exists(junk), "invalid download was not removed");
                var good = Path.Combine(_root, "good.gguf");
                File.WriteAllBytes(good, Fixtures.Gguf());
                ModelDownloadService.VerifyGguf(good);
            }));
            await Test("model download: target honours the configured models directory", () => Run(() =>
            {
                var config = ConfigService.ReadConfig();
                var saved = config.ModelsDirectory;
                try
                {
                    var custom = Path.Combine(_root, "custom-models");
                    config.ModelsDirectory = custom;
                    ConfigService.SaveConfig(config);
                    Equal(custom, ModelDownloadService.TargetDirectory);
                    config.ModelsDirectory = "";
                    ConfigService.SaveConfig(config);
                    Equal(Path.Combine(ConfigService.GetRoot(), "models"), ModelDownloadService.TargetDirectory);
                }
                finally { config.ModelsDirectory = saved; ConfigService.SaveConfig(config); }
            }));
            await Test("benchmark: plan parsing, variant stats and report rendering", () => Run(() =>
            {
                var plan = ParameterBenchmarkService.ParsePlan(
                    "# 注释行被忽略\nmoe 28|--n-cpu-moe 28 -c 4096\nmoe 30|--n-cpu-moe 30 -c 4096\n\n无标签变体 -c 2048");
                Equal(3, plan.Count);
                Equal("moe 28", plan[0].Label); Equal("--n-cpu-moe 28 -c 4096", plan[0].Args);
                Equal("变体 3", plan[2].Label);
                Check(new LauncherArguments(plan[2].Args).Has("-c"), "label-less variant lost its args");
                Throws<ArgumentException>(() => ParameterBenchmarkService.ParsePlan("带端口 --port 9999"));
                Check(ParameterBenchmarkService.ParsePlan("").Count == 0, "empty plan must parse to zero");

                var good = new BenchmarkVariant("好", "-ngl 999", [30.0, 34.0, null], [0.5, 0.6, null]);
                Equal(32.0, good.MeanDecodeTps); Equal(30.0, good.WorstDecodeTps); Equal(2, good.SuccessfulRuns);
                var dead = new BenchmarkVariant("坏", "-ngl 999", [null, null], [null, null]);
                Check(dead.MeanDecodeTps == null && dead.WorstDecodeTps == null, "a failed variant must report null rates");

                var report = new BenchmarkReport(
                    [good, new BenchmarkVariant("快", "-ngl 999 --n-cpu-moe 26", [40.0], [0.4])],
                    ["坏: 未找到模型"], "开始时显存空闲 5000 / 6144 MiB");
                Check(report.FastestLabel == "快", "fastest variant missed");
                var text = ParameterBenchmarkService.Describe(report);
                Check(text.Contains("32.00 tok/s") && text.Contains("最差 30.00") && text.Contains("★"),
                    "rendering incomplete: " + text);
                Check(text.Contains("失败：坏: 未找到模型"), "error line missing");
                Check(text.Contains("空闲 5000"), "VRAM note missing from the report");

                // Process telemetry: engine timings (prefill + draft acceptance) and sampled VRAM.
                var sample = SpeedTestService.ParseTimings(
                    "{\"timings\":{\"predicted_per_second\":32.5,\"prompt_per_second\":210,\"draft_n\":100,\"draft_n_accepted\":78}}");
                Check(sample != null && sample.DecodeTps == 32.5 && sample.PrefillTps == 210,
                    "timings decode/prefill not parsed");
                Check(sample!.DraftAcceptance is > 0.77 and < 0.79, "draft acceptance not computed");
                Check(SpeedTestService.ParseTimings("{}") == null, "payload without timings must yield null");
                Check(SpeedTestService.ParseTimings("not json") == null, "bad json must be ignored");
                Equal(5900, GpuMemoryService.ParseUsedMib("5900\n"));
                Equal(0, GpuMemoryService.ParseUsedMib("N/A\n"));
                Equal(0, GpuMemoryService.ParseUsedMib(""));

                var measured = new BenchmarkVariant("遥测", "-ngl 999", [32.0], [0.5])
                {
                    PrefillTps = [210.0], DraftOffered = 100, DraftAccepted = 78, PeakVramMb = 5900
                };
                Equal(210.0, measured.MeanPrefillTps);
                Equal(0.78, measured.DraftAcceptance);
                var telemetryText = ParameterBenchmarkService.Describe(
                    new BenchmarkReport([measured], [], "开始时显存空闲 5000 / 6144 MiB"));
                Check(telemetryText.Contains("预填 210 tok/s") && telemetryText.Contains("草稿 78%") &&
                    telemetryText.Contains("显存峰 5,900 MiB"), "telemetry missing: " + telemetryText);
            }));
            await Test("benchmark: per-variant telemetry is collected end-to-end and rendered", async () =>
            {
                var config = Config(Fixtures.FreePort());
                config.ActiveBackendId = "llamacpp"; ConfigService.SaveConfig(config);
                var report = await ParameterBenchmarkService.RunAsync(_model, "",
                    [("快", "--fake-timings -ngl 999"), ("慢", "--fake-timings -ngl 999 -c 2048")], 2,
                    _ => { }, CancellationToken.None, _ => Task.FromResult(5900));
                Equal(2, report.Variants.Count);
                var fast = report.Variants[0];
                Equal(2, fast.SuccessfulRuns);
                Equal(210.0, fast.MeanPrefillTps);
                Equal(0.78, fast.DraftAcceptance);
                Equal(5900, fast.PeakVramMb);
                Check(report.Variants[1].PeakVramMb == 5900, "peak VRAM not collected for the second variant");
                var rendered = ParameterBenchmarkService.Describe(report);
                Check(rendered.Contains("预填 210 tok/s") && rendered.Contains("草稿 78%") &&
                    rendered.Contains("显存峰 5,900 MiB"), "telemetry not rendered: " + rendered);
            });
            await Test("benchmark: failure and cancellation preserve the existing service", () =>
                BenchmarkRegression.RunFailureAndCancellationAsync(_model));
            await Test("lifecycle: server start truncates the shared stderr log", async () =>
            {
                var log = ConfigService.GetMainStderrPath();
                File.WriteAllText(log, "STALE-MARKER\n");
                var config = Config(Fixtures.FreePort()); ConfigService.SaveConfig(config);
                Check(await ServerService.StartAsync(config), "fake server failed");
                try
                {
                    using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    Check(!(await reader.ReadToEndAsync()).Contains("STALE-MARKER"), "stale log survived restart");
                }
                finally { Check(await ServerService.StopAsync(config), "could not stop fake server"); }
            });
            await Test("WPF parameter controls preserve original semantics", UiRegression.RunAsync);
            await Test("process: kernel image identity is available at fresh startup", async () =>
            {
                var image = Path.Combine(_root, "llama-server.exe");
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    using var process = Process.Start(new ProcessStartInfo(image, "--probe-hang")
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root })!;
                    var identity = new ServerSession { ProcessId = process.Id,
                        StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks, EnginePath = image };
                    try
                    {
                        Check(PathIdentity.Equals(image, ProcessIdentityService.ExecutablePath(process)), "kernel image query failed at startup");
                        using var valid = ServerService.ValidateProcess(identity);
                        Check(valid != null, "fresh created process identity was rejected");
                        Check(!process.HasExited, "live owned process was treated as exited");
                    }
                    finally
                    {
                        using var valid = ServerService.ValidateProcess(identity);
                        if (valid != null) { valid.Kill(true); await valid.WaitForExitAsync(); }
                    }
                    Check(process.HasExited, "exited process cleanup was not confirmed");
                }
            });
            await Test("session: PID reuse guard", () => Run(() =>
            {
                using var process = Process.GetCurrentProcess();
                var session = new ServerSession { ProcessId = process.Id, StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks + 1, EnginePath = Environment.ProcessPath! };
                Check(ServerService.ValidateProcess(session) == null, "reused PID accepted");
            }));
            await Test("session: adopts externally restarted server with identical args", async () =>
            {
                var config = Config(Fixtures.FreePort()); ConfigService.SaveConfig(config);
                Check(await ServerService.StartAsync(config), "fake server failed");
                var stale = config.RunningServer!;
                using (var first = ServerService.GetManagedProcess(config)) Check(first != null && first.Id == stale.ProcessId, "not detected");
                // Simulate an external restart (autostart/manual): kill without recording, relaunch identical command line.
                try { using var victim = Process.GetProcessById(stale.ProcessId); victim.Kill(true); victim.WaitForExit(15000); } catch { }
                await Fixtures.AssertListenerReleasedAsync(stale.Port, TimeSpan.FromSeconds(15), "external restart fixture");
                var deadline = DateTime.UtcNow.AddSeconds(15);
                using var relaunch = Process.Start(new ProcessStartInfo(Path.Combine(_root, "llama-server.exe"), stale.Args)
                {
                    WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true
                })!;
                try
                {
                    deadline = DateTime.UtcNow.AddSeconds(15);
                    while (!await ServerService.TestApiReadyAsync(stale.Port) && DateTime.UtcNow < deadline) await Task.Delay(200);
                    Check(await ServerService.TestApiReadyAsync(stale.Port), "relaunched server not ready");
                    using var adopted = ServerService.GetManagedProcess(config);
                    Check(adopted != null && adopted.Id == relaunch.Id,
                        $"externally restarted server not adopted (adopted={(adopted == null ? "null" : adopted.Id.ToString())}, wanted={relaunch.Id})");
                    var latest = ConfigService.ReadConfig();
                    Check(latest.RunningServer != null && latest.RunningServer.ProcessId == relaunch.Id,
                        $"session not migrated (pid={(latest.RunningServer == null ? "null" : latest.RunningServer.ProcessId.ToString())})");
                }
                finally { Check(await ServerService.StopAsync(config), "could not stop adopted server"); }
            });
            await Test("session: adopts a detached server after its config record is lost", async () =>
            {
                var config = Config(Fixtures.FreePort()); ConfigService.SaveConfig(config);
                Check(await ServerService.StartAsync(config), "fake server failed");
                var session = config.RunningServer!;
                try
                {
                    // Simulate the record being lost (crash, cleared or corrupt config)
                    // while the detached server keeps listening on the port.
                    ConfigService.SaveSession(config, null);
                    Check(ConfigService.ReadConfig().RunningServer == null, "record not cleared");
                    Process? adopted = null;
                    var deadline = DateTime.UtcNow.AddSeconds(25);
                    while (adopted == null && DateTime.UtcNow < deadline)
                    {
                        adopted = ServerService.GetManagedProcess(config);
                        if (adopted == null) await Task.Delay(500);
                    }
                    Check(adopted != null && adopted.Id == session.ProcessId,
                        $"detached server not re-adopted (adopted={(adopted == null ? "null" : adopted.Id.ToString())}, wanted={session.ProcessId})");
                    adopted!.Dispose();
                    var latest = ConfigService.ReadConfig();
                    Check(latest.RunningServer != null && latest.RunningServer.ProcessId == session.ProcessId, "session not restored");
                }
                finally
                {
                    if (ConfigService.ReadConfig().RunningServer != null)
                        Check(await ServerService.StopAsync(config), "could not stop adopted server");
                    else
                        try { using var p = Process.GetProcessById(session.ProcessId); p.Kill(true); } catch { }
                }
            });
            await Test("lifecycle: selection and port edits do not change the running identity", async () =>
            {
                var config = Config(Fixtures.FreePort()); ConfigService.SaveConfig(config);
                Check(await ServerService.StartAsync(config), "fake server failed");
                var session = config.RunningServer!;
                config.ModelPath = _prismModel; config.Port = Fixtures.FreePort(); config.Args = "--port " + config.Port;
                ConfigService.SaveConfig(config);
                using var process = ServerService.GetManagedProcess(config);
                Equal(session.ProcessId, process!.Id); Equal(session.Port, ServerService.GetRunningPort(config));
                Check(await ServerService.StopAsync(config), "could not stop original engine");
                Check(!await ServerService.TestApiReadyAsync(session.Port), "old endpoint remained active");
            });
            await Test("lifecycle: stop interrupts pending readiness; a second start cannot race", async () =>
            {
                var config = Config(Fixtures.FreePort()); config.Args += " --fake-delay 3000";
                ConfigService.SaveConfig(config);
                var starting = ServerService.StartAsync(config);
                await Task.Delay(150);
                Check(await ServerService.StopAsync(config), "stop missed pending launch");
                try { await starting; throw new Exception("cancelled start reported success"); }
                catch (OperationCanceledException) { }
                var next = Config(Fixtures.FreePort()); ConfigService.SaveConfig(next);
                var results = await Task.WhenAll(ServerService.StartAsync(next), ServerService.StartAsync(next));
                Check(results.All(x => x), "concurrent start failed");
                Check(await ServerService.StopAsync(next), "stop failed");
            });
            await Test("embedding: custom model and port use the same lifecycle", async () =>
            {
                var config = Config(Fixtures.FreePort());
                config.EmbeddingModelPath = _model; config.EmbeddingPort = Fixtures.FreePort(); ConfigService.SaveConfig(config);
                Check(await ServerService.StartEmbeddingAsync(config), "embedding failed");
                Equal(config.EmbeddingPort, config.RunningEmbedding!.Port);
                Check(await ServerService.StopEmbeddingAsync(config), "custom embedding stop failed");
            });
            await Test("speed test: model lookup excluded from TTFT; events are not token counts", async () =>
            {
                var config = Config(Fixtures.FreePort()); config.Args += " --fake-timings"; ConfigService.SaveConfig(config);
                await ServerService.StartAsync(config);
                var result = await SpeedTestService.RunAsync(config.RunningServer!.Port);
                Check(result.Success && result.HasUsage && result.CompletionTokens == 6 && result.StreamEvents == 2, "stream usage");
                Check(result.TtftSeconds < .4, "model lookup incorrectly included");
                Check(result.ServerDecodeTps == 42.5 && result.DecodeTps == 42.5, "engine timing was not preferred over SSE events");
                Check(result.PrefillTps == 210, "engine prefill rate not captured");
                Check(result.DraftOffered == 100 && result.DraftAccepted == 78 &&
                    Math.Abs(result.DraftAcceptance!.Value - 0.78) < 1e-9, "engine draft acceptance not captured");
                await ServerService.StopAsync(config);
                config = Config(Fixtures.FreePort()); config.Args += " --fake-no-usage"; ConfigService.SaveConfig(config);
                await ServerService.StartAsync(config);
                result = await SpeedTestService.RunAsync(config.RunningServer!.Port);
                Check(result.Success && !result.HasUsage && result.DecodeTps == null, "fabricated tokens from SSE events");
                await ServerService.StopAsync(config);
            });
            await Test("lifecycle: default alias uses model file stem and respects explicit -a / --alias", async () =>
            {
                var config = Config(Fixtures.FreePort()); ConfigService.SaveConfig(config);
                Check(await ServerService.StartAsync(config), "fake server failed");
                Equal("standard model", new LauncherArguments(config.RunningServer!.Args).Get("--alias"));
                Equal("standard model", (await SpeedTestService.RunAsync(config.RunningServer!.Port)).Model);
                await ServerService.StopAsync(config);

                config = Config(Fixtures.FreePort()); config.Args += " -a custom-api-model"; ConfigService.SaveConfig(config);
                Check(await ServerService.StartAsync(config), "fake server with explicit -a failed");
                Equal("custom-api-model", new LauncherArguments(config.RunningServer!.Args).Get("--alias"));
                Equal("custom-api-model", (await SpeedTestService.RunAsync(config.RunningServer!.Port)).Model);
                await ServerService.StopAsync(config);
            });
            await Test("CLI: detached server survives parent exit and GUI settings edits", async () =>
            {
                var config = Config(Fixtures.FreePort()); config.ActiveBackendId = "llamacpp"; ConfigService.SaveConfig(config);
                ConfigService.SaveActiveBackend(config, "llamacpp");
                await ProcessProbe.RunAsync(Environment.ProcessPath!, "--invoke-cli --start", TimeSpan.FromSeconds(10));
                var latest = ConfigService.ReadConfig();
                using var server = ServerService.GetManagedProcess(latest);
                Check(server != null && !server.HasExited, "server died when parent exited");
                config.ModelPath = _prismModel; config.Args = "--port " + Fixtures.FreePort(); ConfigService.SaveConfig(config);
                var status = await ProcessProbe.RunAsync(Environment.ProcessPath!, "--invoke-cli --status", TimeSpan.FromSeconds(10));
                Check(status.Contains("\"running\":true") && status.Contains("\"ready\":true"), "status lost session");
                await ProcessProbe.RunAsync(Environment.ProcessPath!, "--invoke-cli --stop", TimeSpan.FromSeconds(10));
                Check(!await ServerService.TestApiReadyAsync(latest.RunningServer!.Port), "CLI stop left service alive");
            });
            await Test("CLI: separate processes serialize concurrent starts", async () =>
            {
                var config = Config(Fixtures.FreePort()); config.ActiveBackendId = "llamacpp"; ConfigService.SaveConfig(config);
                ConfigService.SaveActiveBackend(config, "llamacpp");
                await Task.WhenAll(
                    ProcessProbe.RunAsync(Environment.ProcessPath!, "--invoke-cli --start", TimeSpan.FromSeconds(10)),
                    ProcessProbe.RunAsync(Environment.ProcessPath!, "--invoke-cli --start", TimeSpan.FromSeconds(10)));
                using var process = ServerService.GetManagedProcess(config);
                Check(process != null, "concurrent CLI starts did not produce a managed session");
                await ServerService.StopAsync(config);
            });
        }
        finally
        {
            await ServerService.StopAsync(ConfigService.ReadConfig());
            await ServerService.StopEmbeddingAsync(ConfigService.ReadConfig());
        }
        Console.WriteLine($"Results: {_passed} passed, {_failed} failed");
        if (_failed == 0) Fixtures.Cleanup(_root);
        return _failed == 0 ? 0 : 1;
    }
}
