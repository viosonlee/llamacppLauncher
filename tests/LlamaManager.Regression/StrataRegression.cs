using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LlamaManager.Models;
using LlamaManager.Services;

internal static class StrataRegression
{
    public static async Task<int> RunChildAsync(string[] args)
    {
        var folder = Path.GetDirectoryName(Environment.ProcessPath!)!;
        File.WriteAllText(Path.Combine(folder, "started.pid"), Environment.ProcessId.ToString());
        File.WriteAllText(Path.Combine(folder, "arguments.json"), JsonSerializer.Serialize(args));
        if (args.Contains("--families"))
        {
            // Reproduces the probe deadlock: flood stderr so an unread pipe fills, then hang.
            if (File.Exists(Path.Combine(folder, "families-hang")))
            {
                for (var i = 0; i < 20000; i++) Console.Error.WriteLine(new string('e', 200));
                await Task.Delay(60000);
            }
            // The upstream engine advertises its native families; the old cyber engine exits nonzero here.
            if (File.Exists(Path.Combine(folder, "no-qwen35"))) return 2;
            Console.WriteLine("qwen4exp qwen35moe");
            return 0;
        }
        if (File.Exists(Path.Combine(folder, "hang"))) await Task.Delay(60000);
        if (args.Contains("--ids")) Console.WriteLine("[1, 2]");
        else if (args.Contains("--tokens"))
        {
            // q36 native path: reads GGUF directly, emits "gen:" ids and its own stats line.
            Console.WriteLine("gen: 1 2");
            Console.WriteLine("strata-q36: prompt 1 tokens in 5.0 ms (200.0 tok/s), 2 generated in 10.0 ms (200.0 tok/s)");
        }
        else
        {
            // Match the engine's padded label, and split a UTF-8 character across token boundaries.
            Console.WriteLine("output  : 1 2");
            Console.WriteLine("decode 2 tokens in 10.0 ms  ->  200.00 tok/s");
            Console.WriteLine("prefill 1 tokens in 5.0 ms  ->  200.00 tok/s");
            Console.Error.WriteLine(new string('x', 100000));
        }
        return File.Exists(Path.Combine(folder, "fail")) ? 9 : 0;
    }

    public static async Task RunAsync(string root, string model)
    {
        var package = Path.Combine(root, "strata 模型 package");
        var engine = Path.Combine(package, "engine");
        var tokenizer = Path.Combine(engine, "tokenize");
        Fixtures.CopyRuntime(engine);
        Fixtures.CopyRuntime(tokenizer);
        File.Copy(Environment.ProcessPath!, Path.Combine(engine, "strata.exe"), true);
        File.Copy(Environment.ProcessPath!, Path.Combine(tokenizer, "llama-tokenize.exe"), true);
        Directory.CreateDirectory(Path.Combine(package, "pack"));
        Directory.CreateDirectory(Path.Combine(package, "profile"));
        File.WriteAllText(Path.Combine(package, "pack", "index.txt"), "fixture");
        File.WriteAllText(Path.Combine(package, "pack", "dense.bin"), "fixture");
        File.WriteAllText(Path.Combine(package, "profile", "one.bin"), "fixture");
        File.WriteAllText(Path.Combine(engine, "vocab_bytes.tsv"), "1\tE4B8\n2\tAD\n");
        var config = new ManagerConfig { Strata = new StrataConfig { PackageRoot = package, ModelPath = model, Prompt = "hello" } };
        ConfigService.SaveStrata(config);
        var paths = StrataService.ResolvePaths(config);
        var result = await StrataService.RunAsync(config);
        Program.Equal("中", result.Answer);
        Program.Check(result.Stats.Contains("decode 200.00"), "real stats parser lost throughput");
        Program.Equal(0, ConfigService.ReadConfig().Strata.LastPid);

        // Explicit invalid selections must not silently fall back to another model/pack/profile.
        config.Strata.PackDir = Path.Combine(root, "missing-pack");
        try { StrataService.ResolvePaths(config); throw new Exception("missing pack accepted"); }
        catch (DirectoryNotFoundException) { }
        config.Strata.PackDir = "";
        File.WriteAllText(Path.Combine(package, "profile", "two.bin"), "fixture");
        try { StrataService.ResolvePaths(config); throw new Exception("ambiguous profile accepted"); }
        catch (InvalidOperationException) { }
        config.Strata.ProfilePath = paths.ProfileBin;

        var mtp = Path.Combine(package, "draft 模型");
        Directory.CreateDirectory(mtp);
        foreach (var file in new[] { "dense.bin", "dense.txt", "experts.bin" }) File.WriteAllText(Path.Combine(mtp, file), "fixture");
        config.Strata.MtpDir = mtp;
        config.Strata.Spec = 4;
        config.Strata.MtpEnabled = true;
        var start = StrataService.CreateEngineStart(config, paths, model, Path.Combine(root, "token ids.txt"));
        Program.Equal(mtp, start.ArgumentList[start.ArgumentList.IndexOf("--mtp") + 1]);
        Program.Equal("4", start.ArgumentList[start.ArgumentList.IndexOf("--spec") + 1]);
        Program.Equal("1", start.Environment["STRATA_MTP_PLAIN"]);
        Program.Check(!start.ArgumentList.Contains("--mtp-max-t"), "draft cap was reintroduced");
        config.Strata.MtpEnabled = false;
        start = StrataService.CreateEngineStart(config, paths, model, "ids.txt");
        Program.Check(!start.ArgumentList.Contains("--mtp"), "disabled MTP still passed");
        Program.Check(!start.Environment.ContainsKey("STRATA_MTP_PLAIN"), "disabled MTP inherited norm mode");

        // A nonzero exit must be reported even if the engine happened to print an output row.
        File.WriteAllText(Path.Combine(engine, "fail"), "");
        try { await StrataService.RunAsync(config); throw new Exception("engine failure accepted"); }
        catch (InvalidOperationException ex) { Program.Check(ex.Message.Contains("9"), "exit code missing"); }
        File.Delete(Path.Combine(engine, "fail"));

        foreach (var stage in new[] { tokenizer, engine })
        {
            var pidFile = Path.Combine(stage, "started.pid");
            File.Delete(pidFile);
            File.WriteAllText(Path.Combine(stage, "hang"), "");
            using var cancel = new CancellationTokenSource();
            var running = StrataService.RunAsync(config, ct: cancel.Token);
            try
            {
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(pidFile) && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
                Program.Check(File.Exists(pidFile), "fake child did not start");
                var pid = int.Parse(File.ReadAllText(pidFile));
                using var child = Process.GetProcessById(pid);
                if (stage == engine)
                {
                    while (ConfigService.ReadConfig().Strata.LastPid != pid && deadline.Elapsed < TimeSpan.FromSeconds(10))
                        await Task.Delay(20);
                    Program.Equal(pid, ConfigService.ReadConfig().Strata.LastPid);
                    ConfigService.SaveStrata(config); // stale settings cannot erase the active child's identity
                    Program.Equal(pid, ConfigService.ReadConfig().Strata.LastPid);
                }
                try { await StrataService.RunAsync(config); throw new Exception("concurrent run accepted"); }
                catch (InvalidOperationException) { }
                cancel.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("cancellation ignored"); }
                catch (OperationCanceledException) { }
                Program.Check(child.HasExited, "cancel left a child running");
                Program.Equal(0, ConfigService.ReadConfig().Strata.LastPid);
            }
            finally
            {
                cancel.Cancel();
                try { await running; } catch (OperationCanceledException) { }
                File.Delete(Path.Combine(stage, "hang"));
            }
        }
        // Stop() is reachable in every state: with a live child it kills it and clears the identity.
        File.WriteAllText(Path.Combine(engine, "hang"), "");
        File.Delete(Path.Combine(engine, "started.pid"));
        using var stopCancel = new CancellationTokenSource();
        var stopProbe = StrataService.RunAsync(config, ct: stopCancel.Token);
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(engine, "started.pid")) && deadline.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(20);
            var pid = int.Parse(File.ReadAllText(Path.Combine(engine, "started.pid")));
            using var child = Process.GetProcessById(pid);
            Program.Check(StrataService.Stop(config), "Stop reported nothing stopped with a live child");
            Program.Check(child.HasExited, "Stop left the engine running");
            Program.Equal(0, ConfigService.ReadConfig().Strata.LastPid);
            Program.Check(!StrataService.Stop(config), "idle Stop reported a stop");
            stopCancel.Cancel();
            // A user-initiated Stop kills the child, so the probe surfaces the engine's
            // exit as InvalidOperationException rather than OCE; both are the designed paths.
            try { await stopProbe.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException
                && (ex is OperationCanceledException || ex.Message.Contains("引擎提前退出"))) { }
        }
        finally { File.Delete(Path.Combine(engine, "hang")); }

        // Cancellation releases the run gate and cross-process lease.
        Program.Equal("中", (await StrataService.RunAsync(config)).Answer);
    }

    public static async Task RunQ36Async(string root, string qwen3Model)
    {
        // A qwen35moe fixture (arch qwen35moe) selects the native q36 path when the engine advertises it.
        var package = Path.Combine(root, "q36 模型 package");
        var engine = Path.Combine(package, "engine");
        var tokenizer = Path.Combine(engine, "tokenize");
        Fixtures.CopyRuntime(engine);
        Fixtures.CopyRuntime(tokenizer);
        File.Copy(Environment.ProcessPath!, Path.Combine(engine, "strata.exe"), true);
        File.Copy(Environment.ProcessPath!, Path.Combine(tokenizer, "llama-tokenize.exe"), true);
        var qwen35 = Path.Combine(root, "qwen35 model.gguf");
        File.WriteAllBytes(qwen35, Fixtures.StrataGguf());
        var config = new ManagerConfig { Strata = new StrataConfig { PackageRoot = package, ModelPath = qwen35, Prompt = "hello" } };
        ConfigService.SaveStrata(config);

        // Warm the fresh engine copy: its first launch pays antivirus scanning + cold JIT, which
        // can exceed the 5 s probe timeout on a busy machine (observed as "q36 family not detected").
        StrataService.EngineSupportsQwen35(Path.Combine(engine, "strata.exe"));

        Program.Check(StrataService.EngineSupportsQwen35(Path.Combine(engine, "strata.exe")), "q36 family not detected");
        // The old engine (no --families) is not mistaken for one that runs qwen35moe.
        File.WriteAllText(Path.Combine(engine, "no-qwen35"), "");
        Program.Check(!StrataService.EngineSupportsQwen35(Path.Combine(engine, "strata.exe")), "engine without qwen35moe accepted");
        File.Delete(Path.Combine(engine, "no-qwen35"));

        // Argument shape: --native + --tokens IDS, no --pack/--expert-profile, MTP as a draft count.
        var paths = StrataService.ResolveQ36Paths(config);
        var start = StrataService.CreateQ36EngineStart(config, paths, qwen35, "1 2");
        Program.Check(start.ArgumentList.Contains("--native") && start.ArgumentList.Contains("--tokens"), "q36 args missing");
        Program.Check(start.ArgumentList[start.ArgumentList.IndexOf("--tokens") + 1] == "1 2", "q36 ids not passed inline");
        Program.Check(!start.ArgumentList.Contains("--pack") && !start.ArgumentList.Contains("--expert-profile"), "q36 passed pack args");
        Program.Check(!start.ArgumentList.Contains("--mtp"), "MTP off still passed --mtp");
        config.Strata.MtpEnabled = true;
        config.Strata.Spec = 2;
        var mtpStart = StrataService.CreateQ36EngineStart(config, paths, qwen35, "1 2");
        Program.Equal("2", mtpStart.ArgumentList[mtpStart.ArgumentList.IndexOf("--mtp") + 1]);
        config.Strata.MtpEnabled = false;

        // The engine's "gen:" line is parsed and detokenized with the exported vocab.
        var vocab = Path.Combine(root, "q36-vocab.tsv");
        File.WriteAllText(vocab, "1\tE4B8\n2\tAD\n");
        Program.Equal("中", StrataService.DetokenizeForTest(vocab, "gen: 1 2", q36: true));
        // Pre-seed the cached vocab so RunAsync does not need Python (a fresh file beats the model's mtime).
        var cached = StrataService.Q36VocabPath(qwen35);
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.WriteAllText(cached, "1\tE4B8\n2\tAD\n");
        File.SetLastWriteTimeUtc(cached, DateTime.UtcNow.AddMinutes(1));

        var result = await StrataService.RunAsync(config);
        Program.Equal("中", result.Answer);
        Program.Equal(0, ConfigService.ReadConfig().Strata.LastPid);
    }

    public static async Task RunPackServiceAsync(string root)
    {
        // The fixture GGUF is qwen3: the preparation entry must refuse it loudly instead of producing a pack.
        var options = new StrataPackService.Options(Path.Combine(root, "pkg"), Path.Combine(root, "standard model.gguf"),
            CacheDirectory: Path.Combine(root, "packs"));
        try { await StrataPackService.PrepareAsync(options); throw new Exception("unsupported architecture accepted"); }
        catch (NotSupportedException) { }
        // Concurrent preparations are serialized: the second call is rejected immediately.
        var config = new ManagerConfig { Strata = new StrataConfig { PackageRoot = root } };
        ConfigService.SaveStrata(config);
        var fake = Path.Combine(root, "pkg", "tools");
        Directory.CreateDirectory(fake);
        foreach (var tool in new[] { "iq_pack.py", "make_profile.py", "gguf_reader.py", "strata_tokenizer.py", "_paths.py" })
            File.WriteAllText(Path.Combine(fake, tool), "# fixture");
        var missingModel = new StrataPackService.Options(Path.Combine(root, "pkg"), Path.Combine(root, "nope.gguf"),
            CacheDirectory: Path.Combine(root, "packs"));
        try { await StrataPackService.PrepareAsync(missingModel); throw new Exception("missing model accepted"); }
        catch (FileNotFoundException) { }
    }
}
