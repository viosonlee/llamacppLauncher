using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class StrataBackendRegression
{
    /// <summary>
    /// The PolyStrata backend starts the engine's own HTTP service
    /// (<c>python -m serve.server --engine strata --config &lt;json&gt; --port N</c>) and maps the
    /// launched process into a canonical session. A fake serve/server.py stands in for Python.
    /// </summary>
    public static async Task RunLaunchAsync(string root, string model)
    {
        // A qwen35moe fixture so the PolyStrata analyzer sees a runnable model.
        model = Path.Combine(root, "apex model.gguf");
        File.WriteAllBytes(model, Fixtures.StrataGguf());
        // A fake Strata source tree: serve/server.py + tools/strata_tokenizer.py + a fake engine.
        var source = Path.Combine(root, "strata-src");
        Directory.CreateDirectory(Path.Combine(source, "serve"));
        Directory.CreateDirectory(Path.Combine(source, "tools"));
        var server = Path.Combine(source, "serve", "server.py");
        File.WriteAllText(server, "# fixture\n");

        // Fake python: answer --version probes and act as the HTTP server for the run.
        var python = Path.Combine(root, "fake-python.cmd");
        File.WriteAllText(python, "@echo off\r\necho fake python 3.12\r\n");

        // The engine ships strata-poly.exe in engine-poly/; the package root holds serve/.
        var package = Path.Combine(root, "poly-pkg");
        var engineDir = Path.Combine(package, "engine-poly");
        Directory.CreateDirectory(engineDir);
        File.WriteAllText(Path.Combine(engineDir, "strata-poly.exe"), "# fake exe");

        // The tokenizer prep tool must exist; pre-seed the tokenizer dir so no Python is needed.
        File.WriteAllText(Path.Combine(source, "tools", "strata_tokenizer.py"), "# fixture\n");

        var port = Fixtures.FreePort();
        var config = new ManagerConfig();
        config.Engines["polystrata"] = new EngineSettings
        {
            ModelPath = model,
            Port = port,
            Parameters = new Dictionary<string, string>
            {
                ["packageRoot"] = package,
                ["serveRoot"] = source,
                ["pythonPath"] = python,
                ["servePort"] = port.ToString(),
                ["context"] = "4096",
            },
        };
        ConfigService.SaveConfig(config);
        ConfigService.SaveEngineSettings(config, "polystrata", config.Engines["polystrata"]);

        var backend = BackendRegistry.ServerById("polystrata");
        Check(backend != null, "polystrata backend not registered");
        Check(backend!.Descriptor.GpuHeavy, "strata backend must be GPU-heavy");

        // Probe: the engine exe, server.py and python all exist.
        var probe = await backend.ProbeAsync(CancellationToken.None);
        Check(probe.Available, "strata backend not available: " + probe.Problem);

        // Compatibility: the APEX fixture (Q3_K/Q6_K/IQ2_S) must pass PolyStrata's expert check.
        var report = await backend.InspectAsync(model, CancellationToken.None);
        Check(report.Runnable, "APEX fixture rejected by polystrata backend: " + string.Join("; ", report.Blockers));

        // BuildLaunch: the command line must invoke serve.server with the runconfig and port.
        var plan = backend.BuildLaunch(new EngineStartRequest { Config = config });
        var args = plan.Arguments.ToList();
        Check(args.Contains("serve.server"), "launch does not run serve.server");
        Check(args.Contains("--engine") && args.Contains("strata"), "engine flag missing");
        var configIndex = args.IndexOf("--config");
        Check(configIndex >= 0 && configIndex + 1 < args.Count, "--config missing");
        var runconfigPath = args[configIndex + 1];
        Check(plan.GeneratedFiles.ContainsKey(runconfigPath), "runconfig not generated");
        var runconfig = JsonNode.Parse(plan.GeneratedFiles[runconfigPath])!.AsObject();
        Equal("qwen35moe", runconfig["arch"]!.GetValue<string>());
        Check(runconfig["exe"]!.GetValue<string>().EndsWith("strata-poly.exe"),
            "polystrata runconfig must point at strata-poly.exe");
        var runArgs = runconfig["args"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        // strata-poly reads the GGUF in place: --model, never the upstream loader's --native.
        Check(runArgs.Contains("--model"), "polystrata must load the GGUF with --model");
        Check(!runArgs.Contains("--native"), "polystrata has no --native loader");
        Check(runArgs.Contains("--max-context") && runArgs.Contains("4096"), "--max-context must follow the panel");
        Check(runArgs.Contains("--vram-reserve-mib"), "polystrata must pass --vram-reserve-mib");
        // Options the engine does not have must never be emitted: strata-poly rejects unknown
        // options outright (it refuses --expert-slots with "unknown option"), so a stray flag
        // would fail the launch instead of being ignored.
        foreach (var unsupported in new[] { "--expert-slots", "--prefill", "--max-new", "--mtp", "--pool-workers" })
            Check(!runArgs.Contains(unsupported), $"strata-poly has no {unsupported}; never emit it");

        // The panel's own fields must reach the command line.
        config.Engines["polystrata"].Parameters["threads"] = "8";
        var panelPlan = backend.BuildLaunch(new EngineStartRequest { Config = config });
        var panelArgs = panelPlan.Arguments.ToList();
        var panelRunconfig = JsonNode.Parse(panelPlan.GeneratedFiles[panelArgs[panelArgs.IndexOf("--config") + 1]])!.AsObject();
        var panelRunArgs = panelRunconfig["args"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Check(panelRunArgs.Contains("--threads") && panelRunArgs.Contains("8"), "--threads must follow the panel value");
        config.Engines["polystrata"].Parameters.Remove("threads");

        // tail-skip and kv are polystrata options; both must reach the engine, and the default
        // (empty) kv must stay off the command line so the engine keeps its own default.
        Check(!runArgs.Contains("--kv"), "an unset kv must not be passed at all");
        Check(!runArgs.Contains("--tail-skip"), "tail-skip 0 must not be passed at all");
        config.Engines["polystrata"].Parameters["kv"] = "q8_0";
        config.Engines["polystrata"].Parameters["tailSkip"] = "6";
        var kvPlan = backend.BuildLaunch(new EngineStartRequest { Config = config });
        var kvArgs = kvPlan.Arguments.ToList();
        var kvRunconfig = JsonNode.Parse(kvPlan.GeneratedFiles[kvArgs[kvArgs.IndexOf("--config") + 1]])!.AsObject();
        var kvRunArgs = kvRunconfig["args"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Check(kvRunArgs.Contains("--kv") && kvRunArgs.Contains("q8_0"), "--kv q8_0 must reach the runconfig");
        Check(kvRunArgs.Contains("--tail-skip") && kvRunArgs.Contains("6"), "--tail-skip 6 must reach the runconfig");
        config.Engines["polystrata"].Parameters.Remove("kv");
        config.Engines["polystrata"].Parameters.Remove("tailSkip");

        // cache-bias and the conversation park are opt-in (both default off in strata-poly):
        // unset must stay off the command line, "auto" must be passed verbatim, and a decimal
        // bias must use an invariant dot so a zh-CN locale never emits "0,5".
        Check(!runArgs.Contains("--cache-bias"), "an unset cache-bias must not be passed at all");
        Check(!runArgs.Contains("--conversation-cache-mib"), "an unset conversation cache must not be passed at all");
        config.Engines["polystrata"].Parameters["cacheBias"] = "0.5";
        config.Engines["polystrata"].Parameters["conversationCache"] = "auto";
        var biasPlan = backend.BuildLaunch(new EngineStartRequest { Config = config });
        var biasArgs = biasPlan.Arguments.ToList();
        var biasRunconfig = JsonNode.Parse(biasPlan.GeneratedFiles[biasArgs[biasArgs.IndexOf("--config") + 1]])!.AsObject();
        var biasRunArgs = biasRunconfig["args"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var biasIndex = biasRunArgs.IndexOf("--cache-bias");
        Check(biasIndex >= 0 && biasIndex + 1 < biasRunArgs.Count && biasRunArgs[biasIndex + 1] == "0.5",
            "--cache-bias 0.5 must reach the runconfig with an invariant decimal point");
        var parkIndex = biasRunArgs.IndexOf("--conversation-cache-mib");
        Check(parkIndex >= 0 && parkIndex + 1 < biasRunArgs.Count && biasRunArgs[parkIndex + 1] == "auto",
            "--conversation-cache-mib auto must reach the runconfig verbatim");
        config.Engines["polystrata"].Parameters["cacheBias"] = "0";
        config.Engines["polystrata"].Parameters["conversationCache"] = "4096";
        var parkPlan = backend.BuildLaunch(new EngineStartRequest { Config = config });
        var parkArgs = parkPlan.Arguments.ToList();
        var parkRunconfig = JsonNode.Parse(parkPlan.GeneratedFiles[parkArgs[parkArgs.IndexOf("--config") + 1]])!.AsObject();
        var parkRunArgs = parkRunconfig["args"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Check(!parkRunArgs.Contains("--cache-bias"), "cache-bias 0 must stay off the command line");
        var parkIndex2 = parkRunArgs.IndexOf("--conversation-cache-mib");
        Check(parkIndex2 >= 0 && parkIndex2 + 1 < parkRunArgs.Count && parkRunArgs[parkIndex2 + 1] == "4096",
            "--conversation-cache-mib 4096 must reach the runconfig");
        config.Engines["polystrata"].Parameters.Remove("cacheBias");
        config.Engines["polystrata"].Parameters.Remove("conversationCache");

        // KVMem virtualized KV cache (REQ-124):
        // Unset must not emit --kvmem; when enabled, budget/reserve/host/sessions reach runconfig.
        Check(!runArgs.Contains("--kvmem"), "unset kvmem must not reach runconfig");
        config.Engines["polystrata"].Parameters["kvmem"] = "1";
        config.Engines["polystrata"].Parameters["kvmemBudget"] = "32768";
        config.Engines["polystrata"].Parameters["kvmemGenReserve"] = "2048";
        config.Engines["polystrata"].Parameters["kvmemHostMib"] = "16384";
        config.Engines["polystrata"].Parameters["kvmemDiskMib"] = "4096";
        config.Engines["polystrata"].Parameters["kvmemDiskPath"] = @"C:\ssd-tier";
        config.Engines["polystrata"].Parameters["kvmemSessions"] = "4";
        var kvmemPlan = backend.BuildLaunch(new EngineStartRequest { Config = config });
        var kvmemArgs = kvmemPlan.Arguments.ToList();
        var kvmemRunconfig = JsonNode.Parse(kvmemPlan.GeneratedFiles[kvmemArgs[kvmemArgs.IndexOf("--config") + 1]])!.AsObject();
        var kvmemRunArgs = kvmemRunconfig["args"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Check(kvmemRunArgs.Contains("--kvmem"), "--kvmem must reach runconfig");
        Check(kvmemRunArgs.Contains("--kvmem-budget") && kvmemRunArgs.Contains("32768"), "--kvmem-budget 32768 must reach runconfig");
        Check(kvmemRunArgs.Contains("--kvmem-gen-reserve") && kvmemRunArgs.Contains("2048"), "--kvmem-gen-reserve 2048 must reach runconfig");
        Check(kvmemRunArgs.Contains("--kvmem-host-mib") && kvmemRunArgs.Contains("16384"), "--kvmem-host-mib 16384 must reach runconfig");
        Check(kvmemRunArgs.Contains("--kvmem-disk-mib") && kvmemRunArgs.Contains("4096"), "--kvmem-disk-mib 4096 must reach runconfig");
        Check(kvmemRunArgs.Contains("--kvmem-disk-path") && kvmemRunArgs.Contains(@"C:\ssd-tier"), "--kvmem-disk-path must reach runconfig");
        Check(kvmemRunArgs.Contains("--kvmem-sessions") && kvmemRunArgs.Contains("4"), "--kvmem-sessions 4 must reach runconfig");
        config.Engines["polystrata"].Parameters.Remove("kvmem");
        config.Engines["polystrata"].Parameters.Remove("kvmemBudget");
        config.Engines["polystrata"].Parameters.Remove("kvmemGenReserve");
        config.Engines["polystrata"].Parameters.Remove("kvmemHostMib");
        config.Engines["polystrata"].Parameters.Remove("kvmemDiskMib");
        config.Engines["polystrata"].Parameters.Remove("kvmemDiskPath");
        config.Engines["polystrata"].Parameters.Remove("kvmemSessions");

        // mmproj and strata-vision integration
        File.WriteAllText(Path.Combine(engineDir, "strata-vision.exe"), "# fake vision");
        var visionMmproj = Path.Combine(root, "mmproj-test.gguf");
        File.WriteAllText(visionMmproj, "# fake mmproj");
        config.Engines["polystrata"].Parameters["mmproj"] = visionMmproj;
        var visionPlan = backend.BuildLaunch(new EngineStartRequest { Config = config });
        var visionArgs = visionPlan.Arguments.ToList();
        var visionRunconfig = JsonNode.Parse(visionPlan.GeneratedFiles[visionArgs[visionArgs.IndexOf("--config") + 1]])!.AsObject();
        Check(visionRunconfig.ContainsKey("vision"), "runconfig must contain vision block when mmproj is set");
        Equal(visionMmproj, visionRunconfig["vision"]!["mmproj"]!.GetValue<string>());
        config.Engines["polystrata"].Parameters.Remove("mmproj");

        // The engine page's settings must actually drive the runconfig (not the legacy Strata block).
        Equal(port, runconfig["port"]!.GetValue<int>());
        Check(args.Contains(port.ToString()), "port not passed to the server");

        // A one-shot CLI --port override must reach the Strata runconfig and serve.server.
        var overridePort = port + 3;
        var overridePlan = backend.BuildLaunch(new EngineStartRequest { Config = config, Port = overridePort });
        var overrideArgs = overridePlan.Arguments.ToList();
        Check(overrideArgs.Contains(overridePort.ToString()), "one-shot port override not passed to serve.server");
        var overrideConfigPath = overrideArgs[overrideArgs.IndexOf("--config") + 1];
        var overrideRunconfig = JsonNode.Parse(overridePlan.GeneratedFiles[overrideConfigPath])!.AsObject();
        Equal(overridePort, overrideRunconfig["port"]!.GetValue<int>());
    }

    /// <summary>Resolvers must report honest problems instead of throwing at random points.</summary>
    public static void RunResolvers(string root)
    {
        var empty = new ManagerConfig();
        Check(StrataBackend.ResolveEngine(empty) == null, "engine resolved from an empty config");
        Check(StrataBackend.ResolveServer(empty) == null, "server resolved from an empty config");
        Check(!StrataService.EngineSupportsQwen35(Path.Combine(root, "missing.exe")), "missing engine advertised qwen35moe");
    }

    public static void RunModelPresets(string root)
    {
        var config = new ManagerConfig();
        var modelA = Path.Combine(root, "modelA.gguf");
        var modelB = Path.Combine(root, "modelB.gguf");
        File.WriteAllText(modelA, "a");
        File.WriteAllText(modelB, "b");

        var settings = new EngineSettings
        {
            ModelPath = modelA,
            Parameters = new()
            {
                ["packageRoot"] = Path.Combine(root, "pkg"),
                ["context"] = "8192",
                ["kv"] = "q8_0",
                ["tailSkip"] = "6"
            }
        };
        config.Engines["polystrata"] = settings;
        ConfigService.SaveConfig(config);

        config.Normalize();
        var normA = PathIdentity.Normalize(modelA);
        Check(settings.ModelPresets.ContainsKey(normA), "ModelPresets did not seed modelA");
        Equal("8192", settings.ModelPresets[normA]["context"]);

        var normB = PathIdentity.Normalize(modelB);
        settings.ModelPresets[normB] = new()
        {
            ["packageRoot"] = Path.Combine(root, "pkg"),
            ["context"] = "16384",
            ["kv"] = "int8",
            ["tailSkip"] = "1"
        };
        settings.ModelPath = modelB;
        settings.Parameters = new(settings.ModelPresets[normB]);
        ConfigService.SaveEngineSettings(config, "polystrata", settings);

        Equal("16384", config.Engines["polystrata"].Parameters["context"]);
        Equal("int8", config.Engines["polystrata"].Parameters["kv"]);

        Check(config.Engines["polystrata"].ModelPresets.ContainsKey(normA), "modelA preset missing");
        Equal("8192", config.Engines["polystrata"].ModelPresets[normA]["context"]);
        Equal("q8_0", config.Engines["polystrata"].ModelPresets[normA]["kv"]);
    }
}
