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
    /// Phase 3: the Strata backend starts the engine's own HTTP service
    /// (<c>python -m serve.server --engine strata --config &lt;json&gt; --port N</c>) and maps the
    /// launched process into a canonical session. A fake serve/server.py stands in for Python.
    /// </summary>
    public static async Task RunLaunchAsync(string root, string model)
    {
        // A qwen35moe fixture so the Strata analyzer sees a runnable model.
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

        // A fake engine directory with strata.exe (the regression exe advertises qwen35moe via --families).
        var package = Path.Combine(root, "strata-pkg");
        var engineDir = Path.Combine(package, "engine");
        Fixtures.CopyRuntime(engineDir);
        File.Copy(Environment.ProcessPath!, Path.Combine(engineDir, "strata.exe"), true);
        // Warm the fresh copy once: a first launch pays for antivirus scanning and cold JIT,
        // which can exceed the probe's 5 s timeout on a busy machine and flake the run.
        // The warm-up result is ignored; the probe below re-checks with a hot process.
        StrataBackend.EngineSupportsQwen35(Path.Combine(engineDir, "strata.exe"));

        // The tokenizer prep tool must exist; pre-seed the tokenizer dir so no Python is needed.
        File.WriteAllText(Path.Combine(source, "tools", "strata_tokenizer.py"), "# fixture\n");

        var port = Fixtures.FreePort();
        var config = new ManagerConfig();
        config.Engines["strata-upstream"] = new EngineSettings
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
        ConfigService.SaveEngineSettings(config, "strata-upstream", config.Engines["strata-upstream"]);

        var backend = BackendRegistry.ServerById("strata-upstream");
        Check(backend != null, "strata-upstream backend not registered");
        Check(backend!.Descriptor.GpuHeavy, "strata backend must be GPU-heavy");

        // Probe: the fake engine advertises qwen35moe, server.py exists, python exists.
        var probe = await backend.ProbeAsync(CancellationToken.None);
        Check(probe.Available, "strata backend not available: " + probe.Problem);

        // Compatibility: the APEX fixture (Q3_K/Q6_K/IQ2_S) must pass on the patched engine.
        var report = await backend.InspectAsync(model, CancellationToken.None);
        Check(report.Runnable, "APEX fixture rejected by strata backend: " + string.Join("; ", report.Blockers));

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
        Check(runconfig["exe"]!.GetValue<string>().EndsWith("strata.exe"), "runconfig exe wrong");
        // The engine page's settings must actually drive the runconfig (not the legacy Strata block).
        Equal(port, runconfig["port"]!.GetValue<int>());
        Check(args.Contains(port.ToString()), "port not passed to the server");
    }

    /// <summary>Resolvers must report honest problems instead of throwing at random points.</summary>
    public static void RunResolvers(string root)
    {
        var empty = new ManagerConfig();
        Check(StrataBackend.ResolveEngine(empty) == null, "engine resolved from an empty config");
        Check(StrataBackend.ResolveServer(empty) == null, "server resolved from an empty config");
        Check(!StrataBackend.EngineSupportsQwen35(Path.Combine(root, "missing.exe")), "missing engine advertised qwen35moe");
    }

    /// <summary>
    /// Regression for the reported hang: selecting the Strata backend runs `strata.exe --families`.
    /// An engine that floods stderr and never exits must not deadlock the caller; the probe is
    /// bounded and kills the child. This is the bug the user hit as "选择 strata 卡死".
    /// </summary>
    public static async Task RunProbeHangAsync(string root)
    {
        var package = Path.Combine(root, "strata-hang-pkg");
        var engineDir = Path.Combine(package, "engine");
        Fixtures.CopyRuntime(engineDir);
        var engine = Path.Combine(engineDir, "strata.exe");
        File.Copy(Environment.ProcessPath!, engine, true);
        File.WriteAllText(Path.Combine(engineDir, "families-hang"), "");

        var config = new ManagerConfig();
        config.Engines["strata-upstream"] = new EngineSettings
        {
            Parameters = new Dictionary<string, string> { ["packageRoot"] = package },
        };
        ConfigService.SaveConfig(config);
        ConfigService.SaveEngineSettings(config, "strata-upstream", config.Engines["strata-upstream"]);

        var backend = BackendRegistry.ServerById("strata-upstream")!;
        var started = DateTime.UtcNow;
        var probe = await backend.ProbeAsync(CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;
        Check(elapsed < TimeSpan.FromSeconds(20), $"probe did not stay bounded: {elapsed.TotalSeconds:F1}s");
        // A hung engine is not a usable backend, and the reason is reported (not an exception).
        Check(!probe.Available, "hung engine reported as available");
    }
}
