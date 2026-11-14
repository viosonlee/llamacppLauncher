using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

/// <summary>
/// Real Windows processes and root-scoped file leases, with generated models and a fixture
/// HTTP server. This proves launch arbitration, not CUDA execution or GPU kernel correctness.
/// </summary>
internal static class GpuNativeLaunchRegression
{
    private const string BackendId = "gpu-native-contender";
    private const string ContenderName = "Native GPU contender fixture";
    private const string EventBase = "Local\\LlamaManager.GpuNative.";
    private const string OwnerIdentityFile = "owner-created-session.json";
    private const string ContenderSpawnFile = "contender-native-spawn.json";
    private const string ModelFile = "native fixture model.gguf";
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Creates its own isolated manager root; never changes the caller's ConfigService root.</summary>
    public static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native GPU lease evidence requires Windows.");
        var id = Guid.NewGuid().ToString("N");
        var isolated = Path.GetFullPath(Path.Combine(root, "gpu-native-" + id));
        var prefix = EventBase + id;
        Directory.CreateDirectory(isolated);
        Fixtures.CopyRuntime(isolated);
        File.WriteAllText(Path.Combine(isolated, "fixture-id.txt"), id);
        var model = Path.Combine(isolated, ModelFile);
        File.WriteAllBytes(model, Fixtures.Gguf(moe: false));
        var mainPort = Fixtures.FreePort();
        var contenderPort = Fixtures.FreePort();
        while (contenderPort == mainPort) contenderPort = Fixtures.FreePort();
        var initial = new ManagerConfig
        {
            ModelPath = model, Port = mainPort, Args = $"--host 127.0.0.1 --port {mainPort} -ngl 0"
        };
        initial.Engines["llamacpp"] = new EngineSettings { ExecutablePath = Path.Combine(isolated, "llama-server.exe") };
        initial.Engines[BackendId] = new EngineSettings { ModelPath = model, Port = contenderPort };
        var store = new ConfigStore(Path.Combine(isolated, "llama-manager.config.json"));
        store.Save(initial);
        using var signals = new Signals(prefix, create: true);
        NativeHelper? owner = null, contender = null;
        ServerSession? captured = null;
        try
        {
            owner = await StartHelperAsync(isolated, "owner", prefix);
            await WaitSignalAsync(signals.Created, "owner native creation", SignalTimeout);
            captured = ReadFixtureIdentity(isolated, OwnerIdentityFile);
            Check(ServerService.IsSessionAlive(captured), "owner did not create a live, fully identified fixture child");
            Check(store.Read().RunningServer == null, "owner was published before the pre-save barrier");

            contender = await StartHelperAsync(isolated, "contender", prefix);
            await WaitSignalAsync(signals.Waiting, "contender actual GPU file-lease contention", SignalTimeout);
            Check(!owner.Process.HasExited && !contender.Process.HasExited, "a helper exited before the contention barrier");
            Check(ServerService.IsSessionAlive(captured), "owner child exited while its transaction held the GPU lease");
            Check(store.Read().RunningServer == null, "the pending owner unexpectedly published while blocked");
            Check(!File.Exists(Path.Combine(isolated, ContenderSpawnFile)), "contender spawned before owner publication");

            signals.Release.Set();
            await WaitExitAsync(contender.Process, TimeSpan.FromSeconds(20));
            await Task.WhenAll(contender.Output, contender.Error).WaitAsync(TimeSpan.FromSeconds(10));
            Equal(0, contender.Process.ExitCode);
            var refusal = File.ReadAllText(Path.Combine(isolated, "contender-refusal.txt"));
            Check(IsExpectedRefusal(refusal), "contender did not exit with the named GPU-owner refusal: " + refusal);
            Check(!File.Exists(Path.Combine(isolated, ContenderSpawnFile)), "refused contender created a native process");
            var published = store.Read();
            Equal(captured, published.RunningServer);
            Check(published.GetRunning(BackendId) == null, "refused contender published a session");
            Check(ServerService.IsSessionAlive(captured), "contender refusal stopped the owner child");

            signals.Stop.Set();
            await WaitExitAsync(owner.Process, TimeSpan.FromSeconds(20));
            await Task.WhenAll(owner.Output, owner.Error).WaitAsync(TimeSpan.FromSeconds(10));
            Equal(0, owner.Process.ExitCode);
            Check(!ServerService.IsSessionAlive(captured), "owner fixture child leaked after the explicit stop signal");
            Check(store.Read().RunningServer == null, "owner stop did not clear its isolated session");
            await Fixtures.AssertListenerReleasedAsync(mainPort, TimeSpan.FromSeconds(5), "native GPU fixture owner stop");
            await Fixtures.AssertListenerReleasedAsync(contenderPort, TimeSpan.FromSeconds(5), "native GPU contender refusal");
            var result = $"PASS native GPU launch: two Windows helpers, shared isolated root/file lease, owner PID {captured.ProcessId}, " +
                $"explicit Created/Waiting barriers, named-owner refusal, zero contender native spawns; fixture-only, not a CUDA kernel test. Root: {isolated}";
            File.WriteAllText(Path.Combine(isolated, "gpu-native-result.log"), result + Environment.NewLine);
            Console.WriteLine(result);
        }
        finally
        {
            // Release a blocked helper even on an assertion failure. Only identities created by
            // this isolated run may be killed; all evidence and helper logs remain in this root.
            signals.Release.Set();
            signals.Stop.Set();
            try { await Task.WhenAll(FinishHelperAsync(owner), FinishHelperAsync(contender)); }
            finally
            {
                await Task.WhenAll(CleanupEvidenceAsync(isolated, OwnerIdentityFile), CleanupEvidenceAsync(isolated, ContenderSpawnFile));
            }
        }
    }

    /// <summary>Program must dispatch all four arguments before any ConfigService access.</summary>
    public static async Task<int> RunHelperAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != "--gpu-native-helper" || args[2] is not ("owner" or "contender"))
            throw new ArgumentException("Expected --gpu-native-helper <isolated-root> <owner|contender> <event-prefix> (four arguments).");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native GPU lease helper requires Windows.");
        var root = Path.GetFullPath(args[1]);
        ValidateFixtureRoot(root, args[3]);
        Environment.SetEnvironmentVariable("LLAMA_MANAGER_ROOT", root);
        using var signals = new Signals(args[3], create: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var config = ConfigService.ReadConfig(); // First ConfigService access, after environment setup.
            Check(PathIdentity.Equals(ConfigService.GetManagerRoot(), root), "helper initialized the wrong manager root");
            Check(PathIdentity.Equals(config.ModelPath, Path.Combine(root, ModelFile)), "helper config points outside generated fixture model");
            Check(PathIdentity.Equals(ConfigService.GetLlamaServerPath(config.ModelPath), Path.Combine(root, "llama-server.exe")),
                "helper config points outside the fixture executable");
            if (args[2] == "owner") await RunOwnerAsync(root, config, signals, timeout.Token);
            else await RunContenderAsync(root, config, signals, timeout.Token);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            File.WriteAllText(Path.Combine(root, args[2] + "-helper-error.txt"), ex.ToString());
            return 1;
        }
    }

    private static async Task RunOwnerAsync(string root, ManagerConfig config, Signals signals, CancellationToken ct)
    {
        ServerSession? captured = null;
        try
        {
            var ready = await ServerService.StartObservedAsync(config, false, Console.WriteLine, ct, async (session, token) =>
            {
                captured = session;
                ValidateFixtureIdentity(root, session);
                await File.WriteAllTextAsync(Path.Combine(root, OwnerIdentityFile), JsonSerializer.Serialize(session), token);
                signals.Created.Set();
                await WaitSignalAsync(signals.Release, "owner pre-publication release", TimeSpan.FromSeconds(60), token);
            });
            Check(ready && captured != null, "owner did not create and ready its fixture child");
            await WaitSignalAsync(signals.Stop, "owner explicit fixture stop", TimeSpan.FromSeconds(60), ct);
            Equal(captured, ConfigService.ReadConfig().RunningServer);
            Check(ServerService.IsSessionAlive(captured), "owner lost fixture process identity before stop");
            Check(await ServerService.StopAsync(config, Console.WriteLine), "owner fixture stop did not execute");
        }
        finally
        {
            if (captured != null)
            {
                await KillFixtureAsync(root, captured);
                ConfigService.ClearSessionSlotIfMatches(ManagerConfig.SlotMain, captured);
            }
        }
    }

    private static async Task RunContenderAsync(string root, ManagerConfig config, Signals signals, CancellationToken ct)
    {
        var manifest = new PluginManifest
        {
            Schema = 1, Id = BackendId, DisplayName = ContenderName, GpuHeavy = true,
            Executable = new() { ["windows"] = Path.Combine(root, "llama-server.exe") },
            Launch = new() { Arguments = ["-m", "{model}", "--host", "127.0.0.1", "--port", "{port}"] },
            Ready = new() { Type = "http", TimeoutSeconds = 10 }
        };
        ServerSession? spawned = null;
        var plugin = new ProcessPluginBackend(manifest, root)
        {
            OnStartLeaseContention = () => signals.Waiting.Set(),
            BeforePublishAsync = async (session, token) =>
            {
                spawned = Record(session);
                ValidateFixtureIdentity(root, spawned);
                await File.WriteAllTextAsync(Path.Combine(root, ContenderSpawnFile), JsonSerializer.Serialize(spawned), token);
            }
        }.Initialize();
        BackendRegistry.Register(plugin);
        try
        {
            try
            {
                await plugin.StartAsync(new EngineStartRequest { Config = config }, new DelegateLogSink(Console.WriteLine), ct);
                throw new Exception("contender GPU launch was accepted instead of refused");
            }
            catch (InvalidOperationException ex) when (IsExpectedRefusal(ex.Message))
            {
                Check(spawned == null, "contender native process was created before GPU refusal");
                File.WriteAllText(Path.Combine(root, "contender-refusal.txt"), ex.Message);
                Console.WriteLine("GPU_REFUSAL " + ex.Message);
            }
        }
        finally
        {
            if (spawned != null)
            {
                await KillFixtureAsync(root, spawned);
                ConfigService.ClearSessionSlotIfMatches(BackendId, spawned);
            }
        }
    }

    private static bool IsExpectedRefusal(string message) => message.Contains("显卡", StringComparison.Ordinal) &&
        message.Contains("llama.cpp (llama-server)", StringComparison.Ordinal) && message.Contains(ContenderName, StringComparison.Ordinal);

    private static ServerSession Record(EngineSession session) => new()
    {
        ProcessId = session.ProcessId, StartTimeUtcTicks = session.StartTimeUtcTicks,
        EnginePath = session.ExecutablePath, ModelPath = session.ModelPath, Args = session.Arguments, Port = session.Port
    };

    private static void ValidateFixtureRoot(string root, string prefix)
    {
        var name = Path.GetFileName(root);
        Check(name.StartsWith("gpu-native-", StringComparison.Ordinal) && Guid.TryParseExact(name[11..], "N", out _),
            "refusing helper access to a non-native-fixture root");
        var id = name[11..];
        Check(prefix == EventBase + id && File.ReadAllText(Path.Combine(root, "fixture-id.txt")) == id,
            "native helper IPC/root sentinel mismatch");
    }

    private static void ValidateFixtureIdentity(string root, ServerSession session)
    {
        Check(session.ProcessId > 0 && session.StartTimeUtcTicks > 0 &&
            PathIdentity.Equals(session.EnginePath, Path.Combine(root, "llama-server.exe")) &&
            PathIdentity.Equals(session.ModelPath, Path.Combine(root, ModelFile)) && !session.Embedding,
            "refusing to manage a process outside this run's fixture identity");
    }

    private static ServerSession ReadFixtureIdentity(string root, string file)
    {
        var session = JsonSerializer.Deserialize<ServerSession>(File.ReadAllText(Path.Combine(root, file)))
            ?? throw new InvalidDataException("missing native fixture identity");
        ValidateFixtureIdentity(root, session);
        return session;
    }

    private static async Task CleanupEvidenceAsync(string root, string file)
    {
        if (File.Exists(Path.Combine(root, file))) await KillFixtureAsync(root, ReadFixtureIdentity(root, file));
    }

    private static async Task KillFixtureAsync(string root, ServerSession session)
    {
        ValidateFixtureIdentity(root, session);
        using var owned = ServerService.ValidateProcess(session);
        if (owned == null) return; // Never manage a reused PID or a different executable.
        owned.Kill(entireProcessTree: true);
        await WaitExitAsync(owned, TimeSpan.FromSeconds(10));
    }

    private static async Task<NativeHelper> StartHelperAsync(string root, string role, string prefix)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("missing regression runner executable");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["LLAMA_MANAGER_ROOT"] = root;
        foreach (var arg in new[] { "--gpu-native-helper", root, role, prefix }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("failed to create native " + role + " helper");
        var output = CaptureAsync(process.StandardOutput.BaseStream, Path.Combine(root, role + "-helper.stdout.log"));
        var error = CaptureAsync(process.StandardError.BaseStream, Path.Combine(root, role + "-helper.stderr.log"));
        ServerSession? identity = null;
        try
        {
            identity = new ServerSession
            {
                ProcessId = process.Id, StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                EnginePath = executable
            };
            using (var verified = ServerService.ValidateProcess(identity))
                Check(verified != null, "new native helper process identity could not be verified");
            return new NativeHelper(process, identity, output, error);
        }
        catch
        {
            // No helper handle escapes when capturing its identity fails. The parent's cleanup
            // routine validates even the null-capture fallback against this runner executable.
            try { await EngineArbiter.CleanupUnpublishedAsync(process, identity, executable); }
            finally
            {
                try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { process.Dispose(); }
            }
            throw;
        }
    }

    private static async Task CaptureAsync(Stream stream, string path)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await stream.CopyToAsync(file);
        await file.FlushAsync();
    }

    private static async Task FinishHelperAsync(NativeHelper? helper)
    {
        if (helper == null) return;
        try
        {
            if (!helper.Process.HasExited)
            {
                try { await WaitExitAsync(helper.Process, TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException)
                {
                    using var verified = ServerService.ValidateProcess(helper.Identity);
                    if (verified == null)
                    {
                        Check(helper.Process.HasExited, "refusing emergency kill of a native helper with mismatched identity");
                    }
                    else
                    {
                        verified.Kill(entireProcessTree: true);
                        await WaitExitAsync(verified, TimeSpan.FromSeconds(10));
                    }
                }
            }
            await Task.WhenAll(helper.Output, helper.Error).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { helper.Process.Dispose(); }
    }

    private static async Task WaitExitAsync(Process process, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cancel.Token);
    }

    private static Task WaitSignalAsync(EventWaitHandle signal, string operation, TimeSpan timeout, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var result = WaitHandle.WaitAny([signal, ct.WaitHandle], timeout);
            if (result == WaitHandle.WaitTimeout) throw new TimeoutException("Timed out waiting for " + operation);
            ct.ThrowIfCancellationRequested();
        });

    private sealed record NativeHelper(Process Process, ServerSession Identity, Task Output, Task Error);

    private sealed class Signals : IDisposable
    {
        public EventWaitHandle Created { get; }
        public EventWaitHandle Waiting { get; }
        public EventWaitHandle Release { get; }
        public EventWaitHandle Stop { get; }
        public Signals(string prefix, bool create)
        {
            EventWaitHandle Open(string suffix)
            {
                var name = prefix + "." + suffix;
                if (!create) return EventWaitHandle.OpenExisting(name);
                var signal = new EventWaitHandle(false, EventResetMode.ManualReset, name, out var fresh);
                if (!fresh) { signal.Dispose(); throw new InvalidOperationException("Native fixture event name collision"); }
                return signal;
            }
            Created = Open("Created"); Waiting = Open("Waiting"); Release = Open("Release"); Stop = Open("Stop");
        }
        public void Dispose() { Created.Dispose(); Waiting.Dispose(); Release.Dispose(); Stop.Dispose(); }
    }
}
