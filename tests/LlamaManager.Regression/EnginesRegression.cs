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
        await AssertPortReleasedAsync(session, "backend stop");
        Check(!logs.Any(l => l.Contains("错误")), "unexpected error logged: " + string.Join(" | ", logs));

        // Inspect must be honest about an unreadable model rather than silently pass.
        var missing = await backend.InspectAsync(Path.Combine(root, "does-not-exist.gguf"), CancellationToken.None);
        Check(!missing.Runnable, "missing model must not be reported runnable");
    }

    /// <summary>
    /// The launch pre-check must probe the actual bind, not the listener table: a busy port gets
    /// a reason, a free port gets null. The benchmark's port allocator must skip service ports.
    /// </summary>
    public static void RunPortProbe()
    {
        var port = Fixtures.FreePort();
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();
        var busy = ServerService.PortUnavailableReason(port);
        Check(busy != null && busy.Contains("占用"), $"a listening port was not reported busy: {busy ?? "null"}");
        listener.Stop();
        Check(ServerService.PortUnavailableReason(port) == null, "a just-released port was reported unavailable");
        Check(ServerService.PortUnavailableReason(port, "0.0.0.0") == null, "wildcard bind probe failed");
        var reserved = ServerService.PortBindFailureReason(port, System.Net.Sockets.SocketError.AccessDenied);
        Check(reserved.Contains("保留") && reserved.Contains("不会自动切换"), "reserved-port diagnosis is missing");
        Check(ServerService.PortBindFailureReason(port, System.Net.Sockets.SocketError.AddressAlreadyInUse).Contains("占用"),
            "occupied-port diagnosis is missing");
        Throws<ArgumentOutOfRangeException>(() => ServerService.PortUnavailableReason(0));
        Throws<ArgumentOutOfRangeException>(() => ServerService.PortUnavailableReason(65536));

        var defaults = new ManagerConfig();
        var poly = BackendRegistry.ServerById("polystrata")!;
        Equal(8080, defaults.Port);
        Equal(8080, poly.ResolvePort(new EngineStartRequest { Config = defaults }));
        Equal("8080", poly.Parameters.Single(p => p.Key == "servePort").Default);

        var config = new ManagerConfig { Port = 8080, EmbeddingPort = 8081 };
        config.Strata.ServePort = 8091;
        config.Engines["polystrata"] = new EngineSettings
        {
            Port = 0,
            Parameters = new Dictionary<string, string> { ["servePort"] = "8080" }
        };
        var servicePorts = ParameterBenchmarkService.ServicePorts(config);
        Check(servicePorts.SetEquals(new HashSet<int> { 8080, 8081, 8091 }),
            "service ports incomplete: " + string.Join(",", servicePorts));

        var allocated = ParameterBenchmarkService.AllocatePorts(8, servicePorts);
        Check(allocated.Distinct().Count() == allocated.Count, "allocated ports are not distinct");
        Check(allocated.All(p => !servicePorts.Contains(p)), "an allocated port collided with a service port");
        Check(allocated.All(p => ServerService.PortUnavailableReason(p) == null), "allocator leaked a listening socket");
        Throws<ArgumentOutOfRangeException>(() => ParameterBenchmarkService.AllocatePorts(-1, servicePorts));
        Throws<IOException>(() => ParameterBenchmarkService.AllocatePorts(1, Enumerable.Range(1, 65535).ToHashSet()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Throws<OperationCanceledException>(() => ParameterBenchmarkService.AllocatePorts(1, servicePorts, cancelled.Token));

        config.Args = "--port 18080";
        config.Engines["plugin"] = new EngineSettings { Parameters = new() { ["port"] = "18081" } };
        config.SetRunning(ManagerConfig.SlotMain, new ServerSession { Port = 18082 });
        var overridden = ParameterBenchmarkService.ServicePorts(config);
        Check(new[] { 8080, 18080, 18081, 18082 }.All(overridden.Contains), "overridden/live service ports were not excluded");
        config.Args = "--prompt \"unfinished";
        Check(ParameterBenchmarkService.ServicePorts(config).Contains(8080), "incomplete editable args lost 8080 protection");
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
        await AssertPortReleasedAsync(session, "embedding backend stop");
    }

    private static async Task AssertPortReleasedAsync(EngineSession session, string operation)
    {
        // The Windows listener snapshot was observed to both miss a live API and retain exited
        // listeners. Assert the actual stop contract: full-identity process exit + exclusive bind.
        using var owned = ServerService.ValidateProcess(new ServerSession
        {
            ProcessId = session.ProcessId, StartTimeUtcTicks = session.StartTimeUtcTicks, EnginePath = session.ExecutablePath
        });
        Check(owned == null, operation + " left the recorded process alive");
        await Fixtures.AssertListenerReleasedAsync(session.Port, TimeSpan.FromSeconds(5), operation);
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

        // A record alone must not reserve the GPU: crash/reboot leftovers have no live process.
        config.SetRunning(ManagerConfig.SlotMain, new ServerSession { ProcessId = 1, EnginePath = "llama-server.exe", ModelPath = "m.gguf", Port = 8080 });
        Check(EngineArbiter.ActiveGpuBackend(config) == null, "a dead record claimed the GPU");
        Check(EngineArbiter.ConflictFor(config, "polystrata") == null, "a dead record caused a conflict");

        // Only a process matching the recorded identity holds the GPU; this process stands in.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        config.SetRunning(ManagerConfig.SlotMain, new ServerSession
        {
            ProcessId = self.Id, StartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks,
            EnginePath = LlamaManager.Services.ProcessIdentityService.ExecutablePath(self) ?? Environment.ProcessPath!,
            ModelPath = "m.gguf", Port = 8080
        });
        Equal("llamacpp", EngineArbiter.ActiveGpuBackend(config));
        Check(EngineArbiter.ConflictFor(config, "llamacpp") == null, "llamacpp conflicts with itself");
        Equal("llamacpp", EngineArbiter.ConflictFor(config, "polystrata"));

        var embedOnly = new ManagerConfig();
        embedOnly.SetRunning(ManagerConfig.SlotEmbedding, new ServerSession { ProcessId = 2, Embedding = true, Port = 8081 });
        Check(EngineArbiter.ActiveGpuBackend(embedOnly) == null, "embedding-only claimed the main GPU slot");
    }
}
