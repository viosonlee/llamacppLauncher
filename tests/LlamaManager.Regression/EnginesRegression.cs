using System.IO;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class EnginesRegression
{
    /// <summary>
    /// Phase 1: the llama.cpp backend must be a faithful adapter over ServerService.
    /// It starts/stops the same fake server and maps identity without changing behaviour.
    /// </summary>
    public static async Task RunLlamaCppAsync(string root, string model)
    {
        var backend = BackendRegistry.ServerById("llamacpp");
        Check(backend != null, "llamacpp backend not registered");
        Check(BackendRegistry.Default.Descriptor.Id == "llamacpp", "default backend is not llamacpp");

        var probe = await backend!.ProbeAsync(CancellationToken.None);
        Check(probe.Available && probe.ExecutablePath != null, "llama-server not probed: " + probe.Problem);

        var report = await backend.InspectAsync(model, CancellationToken.None);
        Check(report.Runnable, "fake model must be runnable on llama.cpp");

        var port = Fixtures.FreePort();
        var config = new ManagerConfig { ModelPath = model, Port = port, Args = $"--host 127.0.0.1 --port {port} -ngl 0" };
        ConfigService.SaveConfig(config);

        var logs = new List<string>();
        var sink = new DelegateLogSink(logs.Add);
        EngineSession session = null!;
        try
        {
            session = await backend.StartAsync(new EngineStartRequest { Config = config }, sink, CancellationToken.None);
            Check(session.BackendId == "llamacpp", "session backend id not stamped");
            Check(session.Port == port, "session port not mapped");
            Check(session.ProcessId > 0 && session.StartTimeUtcTicks > 0, "session identity incomplete");
            Equal(model, session.ModelPath);
            Check(await backend.ReadyAsync(session, CancellationToken.None), "backend reported not ready after start");

            // The mapped session must round-trip back to the persisted ServerSession identity.
            var persisted = ConfigService.ReadConfig().RunningServer;
            Check(persisted != null && persisted.ProcessId == session.ProcessId, "persisted session not found");
            Equal(persisted!.EnginePath, session.ExecutablePath);
            Equal(persisted.StartTimeUtcTicks, session.StartTimeUtcTicks);
        }
        finally
        {
            if (session != null) await backend.StopAsync(session, sink);
        }
        Check(!ServerService.TestPortInUse(port), "backend stop left the port busy");
        Check(!logs.Any(l => l.Contains("错误")), "unexpected error logged: " + string.Join(" | ", logs));

        // Inspect must be honest about an unreadable model rather than silently pass.
        var missing = await backend.InspectAsync(Path.Combine(root, "does-not-exist.gguf"), CancellationToken.None);
        Check(!missing.Runnable, "missing model must not be reported runnable");
    }

    /// <summary>Embedding start/stop must go through the same backend facade.</summary>
    public static async Task RunEmbeddingAsync(string root, string model)
    {
        var backend = BackendRegistry.ServerById("llamacpp")!;
        var port = Fixtures.FreePort();
        var config = new ManagerConfig { EmbeddingModelPath = model, EmbeddingPort = port };
        ConfigService.SaveConfig(config);
        var sink = new DelegateLogSink(_ => { });
        var session = await backend.StartAsync(new EngineStartRequest { Config = config, Embedding = true }, sink, CancellationToken.None);
        try
        {
            Equal("1", session.Extra["embedding"]);
            Check(await backend.ReadyAsync(session, CancellationToken.None), "embedding backend not ready");
        }
        finally { await backend.StopAsync(session, sink); }
        Check(!ServerService.TestPortInUse(port), "embedding backend stop left the port busy");
    }

    /// <summary>Registry resolution and GPU arbitration over the canonical session store.</summary>
    public static void RunRegistryAndArbiter()
    {
        Check(BackendRegistry.All().Any(b => b.Descriptor.Id == "llamacpp"), "llamacpp not in registry");
        Check(BackendRegistry.ById("llamacpp") != null, "ById failed");
        Check(BackendRegistry.ById("nope") == null, "unknown backend resolved");
        Check(BackendRegistry.Default.Descriptor.Id == "llamacpp", "wrong default backend");

        var config = new ManagerConfig();
        Check(EngineArbiter.ActiveGpuBackend(config) == null, "empty config claims the GPU");
        Check(EngineArbiter.ConflictFor(config, "llamacpp") == null, "empty config conflicts");

        config.SetRunning(ManagerConfig.SlotMain, new ServerSession { ProcessId = 1, EnginePath = "llama-server.exe", ModelPath = "m.gguf", Port = 8080 });
        Equal("llamacpp", EngineArbiter.ActiveGpuBackend(config));
        Check(EngineArbiter.ConflictFor(config, "llamacpp") == null, "llamacpp conflicts with itself");

        var embedOnly = new ManagerConfig();
        embedOnly.SetRunning(ManagerConfig.SlotEmbedding, new ServerSession { ProcessId = 2, Embedding = true, Port = 8081 });
        Check(EngineArbiter.ActiveGpuBackend(embedOnly) == null, "embedding-only claimed the main GPU slot");
    }
}
