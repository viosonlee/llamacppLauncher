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
}
