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
    private static void Throws<T>(Action action) where T : Exception
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
        if (args.FirstOrDefault() == "--check-published")
            return await PublishedSmoke.RunAsync(args[1]);
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
        if (args.Contains("--ids") || args.Contains("--tokens-file")) return await StrataRegression.RunChildAsync(args);
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
            }));
            await Test("speed monitor: supports object/array counters, ignores idle stale timings", () => Run(() =>
            {
                Check(SpeedMonitorService.Parse("[{\"is_processing\":true,\"next_token\":{\"n_decoded\":42}}]").Contains("42"), "object schema");
                Check(SpeedMonitorService.Parse("[{\"is_processing\":true,\"next_token\":[{\"n_decoded\":24}]}]").Contains("24"), "array schema");
                Equal("", SpeedMonitorService.Parse("[{\"is_processing\":false,\"timings\":{\"predicted_per_second\":999}}]"));
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
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (ServerService.TestPortInUse(stale.Port) && DateTime.UtcNow < deadline) await Task.Delay(200);
                Check(!ServerService.TestPortInUse(stale.Port), "port not released");
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
                var config = Config(Fixtures.FreePort()); ConfigService.SaveConfig(config);
                await ServerService.StartAsync(config);
                var result = await SpeedTestService.RunAsync(config.RunningServer!.Port);
                Check(result.Success && result.HasUsage && result.CompletionTokens == 6 && result.StreamEvents == 2, "stream usage");
                Check(result.TtftSeconds < .4, "model lookup incorrectly included");
                Check(result.DecodeTps > 0, "no throughput");
                await ServerService.StopAsync(config);
                config.Args += " --fake-no-usage"; await ServerService.StartAsync(config);
                result = await SpeedTestService.RunAsync(config.RunningServer!.Port);
                Check(result.Success && !result.HasUsage && result.DecodeTps == null, "fabricated tokens from SSE events");
                await ServerService.StopAsync(config);
            });
            await Test("CLI: detached server survives parent exit and GUI settings edits", async () =>
            {
                var config = Config(Fixtures.FreePort()); ConfigService.SaveConfig(config);
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
                var config = Config(Fixtures.FreePort()); ConfigService.SaveConfig(config);
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
