using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class GpuLaunchRegression
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly DelegateLogSink Quiet = new(_ => { });
    private static ServerSession Record(EngineSession s) => new() { ProcessId = s.ProcessId, StartTimeUtcTicks = s.StartTimeUtcTicks,
        EnginePath = s.ExecutablePath, ModelPath = s.ModelPath, Args = s.Arguments, Port = s.Port };
    private static ManagerConfig Config(string model)
    {
        var port = Fixtures.FreePort();
        return new ManagerConfig { ModelPath = model, Port = port, Args = $"--host 127.0.0.1 --port {port} -ngl 0",
            EmbeddingModelPath = model, EmbeddingPort = Fixtures.FreePort() };
    }
    private static ProcessPluginBackend Plugin(string model, bool gpu, Func<EngineSession, CancellationToken, Task>? observe = null, Action? waiting = null)
    {
        var engine = ConfigService.GetLlamaServerPath(model);
        var manifest = new PluginManifest { Schema = 1, Id = (gpu ? "gpu-" : "cpu-") + Guid.NewGuid().ToString("N"),
            DisplayName = gpu ? "GPU fixture plugin" : "CPU fixture plugin", GpuHeavy = gpu,
            Executable = new() { ["windows"] = engine },
            Launch = new() { Arguments = ["-m", "{model}", "--port", "{port}"] },
            Ready = new() { Type = "http", TimeoutSeconds = 10 },
            Parameters = [new() { Key = "port", Default = Fixtures.FreePort().ToString(), Type = "Integer" }] };
        var plugin = new ProcessPluginBackend(manifest, Path.GetDirectoryName(engine)!) { BeforePublishAsync = observe, OnStartLeaseContention = waiting }.Initialize();
        BackendRegistry.Register(plugin);
        return plugin;
    }
    private static async Task Refused(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15)); throw new Exception("competing GPU launch was accepted"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("显卡"), "wrong GPU refusal: " + ex.Message); }
    }

    // Ownership comes only from creation hooks, never from mutable config slots or PID scans.
    private sealed class FixtureChildren(string root, string model)
    {
        private readonly ConcurrentQueue<ServerSession> _created = new();
        private readonly List<Task> _starts = [];

        public void Capture(ServerSession session)
        {
            _created.Enqueue(session); // Keep the evidence even if a safety check rejects it.
            EnsureOwned(session);
        }
        public void Capture(EngineSession session) => Capture(Record(session));
        public Task<T> Track<T>(Task<T> start) { _starts.Add(start); return start; }

        public void EnsureOwned(ServerSession session)
        {
            Check(_created.Contains(session) && session.ProcessId > 0 && session.ProcessId != Environment.ProcessId &&
                session.StartTimeUtcTicks > 0 && PathIdentity.Equals(session.ModelPath, model) &&
                PathIdentity.Equals(session.EnginePath, Path.Combine(root, "llama-server.exe")),
                "refusing to manage an identity not created by this test's fixture hooks");
        }

        private async Task StopOwnedAsync(ServerSession session)
        {
            EnsureOwned(session);
            Process process;
            try { process = Process.GetProcessById(session.ProcessId); }
            catch (ArgumentException) { return; } // The recorded PID no longer exists.
            using (process)
            {
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != session.StartTimeUtcTicks)
                    return; // The original child exited; never kill a reused PID.
                // Unlike IsSessionAlive(false), a failed image query is not proof of exit.
                var image = ProcessIdentityService.ExecutablePath(process);
                if (process.HasExited) return;
                Check(PathIdentity.Equals(image, session.EnginePath),
                    "fixture image could not be validated; refusing kill and config restoration");
                try { process.Kill(entireProcessTree: false); } // Fake servers have no child processes to own.
                catch (Exception ex) when ((ex is InvalidOperationException or System.ComponentModel.Win32Exception) && process.HasExited) { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
            }
        }

        public async Task CleanupAsync(ConfigStore store, ManagerConfig previous, TaskCompletionSource release,
            params CancellationTokenSource[] cancellations)
        {
            var errors = new List<Exception>();
            // Cancel *all* contenders before opening the barrier; one failing callback must not
            // prevent cancelling another start or cleaning another child.
            foreach (var cancellation in cancellations)
            {
                try { cancellation.Cancel(); }
                catch (Exception ex) { errors.Add(ex); }
            }
            release.TrySetResult();
            // Drain the actual starts, not their timed-out WaitAsync proxies. Their cancellation-
            // aware hooks/readiness waits must finish before taking the final ownership snapshot.
            try { await Task.WhenAll(_starts).WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException ex) { errors.Add(new InvalidOperationException("Fixture starts did not drain after cancellation; keep config/evidence.", ex)); }
            catch { /* Start failures/cancellation are asserted by the test, not by teardown. */ }
            foreach (var session in _created.Distinct())
            {
                try { await StopOwnedAsync(session); }
                catch (Exception ex)
                {
                    errors.Add(new InvalidOperationException(
                        $"Fixture cleanup failed: PID {session.ProcessId}, start ticks {session.StartTimeUtcTicks}, engine {session.EnginePath}", ex));
                }
            }
            if (errors.Count > 0)
                throw new AggregateException("Fixture teardown failed; previous config was NOT restored, preserving remaining session records.", errors);
            store.Save(previous);
        }
    }

    public static async Task RunMainVsPluginAsync(string root, string model)
    {
        var store = new ConfigStore(Path.Combine(root, "llama-manager.config.json")); var previous = store.Read();
        Check(EngineArbiter.ActiveGpuBackend(previous) == null, "isolated test root has an unexpected live GPU owner");
        var config = Config(model); store.Save(config);
        var created = Signal(); var release = Signal(); var contenderWaiting = Signal();
        using var cancel = new CancellationTokenSource();
        var children = new FixtureChildren(root, model);
        ServerSession? captured = null; var mainStarted = false; var bSpawns = 0;
        try
        {
            var plugin = Plugin(model, true, (s, _) => { children.Capture(s); Interlocked.Increment(ref bSpawns); return Task.CompletedTask; }, () => contenderWaiting.TrySetResult());
            var startA = children.Track(ServerService.StartObservedAsync(config, false, null, cancel.Token, async (session, ct) =>
            { children.Capture(session); captured = session; created.TrySetResult(); await release.Task.WaitAsync(ct); }));
            await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(captured != null && ServerService.IsSessionAlive(captured), "A did not create a real fixture child");
            Check(store.Read().RunningServer == null, "A was published before the test barrier");
            var startB = children.Track(plugin.StartAsync(new EngineStartRequest { Config = config }, Quiet, cancel.Token));
            await contenderWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, bSpawns); Check(!startB.IsCompleted, "B did not wait for A's launch transaction");
            // Forced-CPU embedding and honestly declared CPU plugins must not wait for the GPU lease.
            Check(await children.Track(ServerService.StartObservedAsync(config, true, null, cancel.Token,
                (s, _) => { children.Capture(s); return Task.CompletedTask; })).WaitAsync(TimeSpan.FromSeconds(10)), "CPU embedding blocked by GPU start");
            Check(config.RunningEmbedding!.Args.Contains("-ngl 0"), "embedding was not forced CPU");
            var cpuBackend = Plugin(model, false, (s, _) => { children.Capture(s); return Task.CompletedTask; });
            var cpu = await children.Track(cpuBackend.StartAsync(new EngineStartRequest { Config = config }, Quiet, cancel.Token)).WaitAsync(TimeSpan.FromSeconds(10));
            Check(ServerService.IsSessionAlive(Record(cpu)), "non-GPU plugin failed during pending GPU launch");
            children.EnsureOwned(Record(cpu));
            await cpuBackend.StopAsync(cpu, Quiet);
            release.TrySetResult();
            Check(await startA.WaitAsync(TimeSpan.FromSeconds(10)), "A did not become ready"); mainStarted = true;
            await Refused(startB); Equal(0, bSpawns);
            Equal(captured, store.Read().RunningServer);
            // Adoption remains same PID even though callers route through the new shared lease.
            var adoptedSpawns = 0;
            Check(await children.Track(ServerService.StartObservedAsync(config, false, null, cancel.Token,
                (s, _) => { children.Capture(s); adoptedSpawns++; return Task.CompletedTask; })), "same-backend adoption failed");
            Equal(0, adoptedSpawns); Equal(captured!.ProcessId, config.RunningServer!.ProcessId);
        }
        finally
        {
            await children.CleanupAsync(store, previous, release, cancel);
        }
        Check(mainStarted, "main/plugin race did not execute");
    }

    public static async Task RunPluginVsMainAsync(string root, string model)
    {
        var store = new ConfigStore(Path.Combine(root, "llama-manager.config.json")); var previous = store.Read();
        var config = Config(model); store.Save(config);
        var created = Signal(); var release = Signal(); var waiting = Signal(); var bSpawns = 0;
        using var cancel = new CancellationTokenSource();
        var children = new FixtureChildren(root, model);
        EngineSession? captured = null;
        try
        {
            var plugin = Plugin(model, true, async (s, ct) => { children.Capture(s); captured = s; created.TrySetResult(); await release.Task.WaitAsync(ct); });
            var startA = children.Track(plugin.StartAsync(new EngineStartRequest { Config = config }, Quiet, cancel.Token));
            await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(captured != null && ServerService.IsSessionAlive(Record(captured)), "plugin A child not alive");
            Check(store.Read().GetRunning(plugin.Descriptor.Id) == null, "plugin A was published too early");
            var startB = children.Track(ServerService.StartObservedAsync(config, false, null, cancel.Token,
                (s, _) => { children.Capture(s); bSpawns++; return Task.CompletedTask; }, () => waiting.TrySetResult()));
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); Equal(0, bSpawns);
            release.TrySetResult(); await startA.WaitAsync(TimeSpan.FromSeconds(10));
            await Refused(startB); Equal(0, bSpawns);
            // Wrong image/time cannot kill the genuine process or clear its replacement record.
            await plugin.StopAsync(captured! with { ExecutablePath = Path.Combine(root, "wrong-image.exe") }, Quiet);
            Check(ServerService.IsSessionAlive(Record(captured!)), "wrong-image stop killed the plugin");
            Equal(Record(captured!), store.Read().GetRunning(plugin.Descriptor.Id));
            await plugin.StopAsync(captured! with { StartTimeUtcTicks = 1 }, Quiet);
            Check(ServerService.IsSessionAlive(Record(captured!)), "stale identity stop killed the plugin");
            // Existing transient comparison may not overlap a live GPU owner (even if main arguments say -ngl0).
            var variantStarted = false;
            try
            {
                await ParameterBenchmarkService.RunAsync(model, "", [("fixture", "--fake-timings")], 1,
                    message => { if (message.Contains("启动：")) variantStarted = true; }, CancellationToken.None, _ => Task.FromResult(5900));
                throw new Exception("transient comparison bypassed GPU owner");
            }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("显卡"), "wrong comparison refusal"); }
            Check(!variantStarted && ServerService.IsSessionAlive(Record(captured!)), "comparison touched a user's running service");
        }
        finally
        {
            await children.CleanupAsync(store, previous, release, cancel);
        }
    }

    public static async Task RunCancellationAndSaveFailureAsync(string root, string model)
    {
        var store = new ConfigStore(Path.Combine(root, "llama-manager.config.json")); var previous = store.Read();
        var config = Config(model); store.Save(config);
        var entered = Signal(); var release = Signal(); var waiting = Signal();
        using var cancelA = new CancellationTokenSource(); using var cancelB = new CancellationTokenSource();
        using var cancelFailure = new CancellationTokenSource();
        var children = new FixtureChildren(root, model);
        EngineSession? captured = null; var bSpawns = 0;
        try
        {
            var a = Plugin(model, true, async (s, token) => { children.Capture(s); captured = s; entered.TrySetResult(); await release.Task.WaitAsync(token); });
            var b = Plugin(model, true, (s, _) => { children.Capture(s); bSpawns++; return Task.CompletedTask; }, () => waiting.TrySetResult());
            var taskA = children.Track(a.StartAsync(new EngineStartRequest { Config = config }, Quiet, cancelA.Token));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var taskB = children.Track(b.StartAsync(new EngineStartRequest { Config = config }, Quiet, cancelB.Token));
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelB.Cancel();
            try { await taskB; throw new Exception("cancelled contender accepted"); } catch (OperationCanceledException) { }
            Equal(0, bSpawns); Check(ServerService.IsSessionAlive(Record(captured!)), "contender cancellation touched owner A");
            cancelA.Cancel();
            try { await taskA; throw new Exception("prepublication cancellation accepted"); } catch (OperationCanceledException) { }
            Check(!ServerService.IsSessionAlive(Record(captured!)), "cancelled unpublished child leaked");
            Check(store.Read().GetRunning(a.Descriptor.Id) == null, "cancelled unpublished child left a slot");
            using (await EngineArbiter.AcquireGpuStartLeaseAsync(CancellationToken.None)) { }

            FileStream? denied = null; EngineSession? failed = null;
            var failure = Plugin(model, true, (s, _) =>
            {
                children.Capture(s); failed = s;
                denied = new FileStream(Path.Combine(root, "llama-manager.config.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
                return Task.CompletedTask;
            });
            try
            {
                try
                {
                    await children.Track(failure.StartAsync(new EngineStartRequest { Config = config }, Quiet, cancelFailure.Token));
                    throw new Exception("actual config-file write denial was ignored");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Check(failed != null && !ServerService.IsSessionAlive(Record(failed)), "save failure leaked unrecorded process");
                Check(store.Read().GetRunning(failure.Descriptor.Id) == null, "failed save left a running slot");
            }
            finally { denied?.Dispose(); }
            using (await EngineArbiter.AcquireGpuStartLeaseAsync(CancellationToken.None)) { }
        }
        finally
        {
            await children.CleanupAsync(store, previous, release, cancelB, cancelFailure, cancelA);
        }
    }

    public static async Task RunCleanupSafetyAsync(string root, string model)
    {
        var store = new ConfigStore(Path.Combine(root, "llama-manager.config.json"));
        var previous = store.Read();
        var config = Config(model); store.Save(config);
        var children = new FixtureChildren(root, model);
        var release = Signal(); using var cancel = new CancellationTokenSource();
        var identities = new List<ServerSession>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var plugin = Plugin(model, false, (s, _) =>
                { children.Capture(s); identities.Add(Record(s)); return Task.CompletedTask; });
                await children.Track(plugin.StartAsync(new EngineStartRequest { Config = config }, Quiet, cancel.Token));
            }
            // Simulate an assertion failing after unexpected successful spawns: the ledger,
            // not successful-return variables or current config selection, must clean both.
            Check(identities.All(ServerService.IsSessionAlive), "cleanup scenario has no live children");
        }
        finally { await children.CleanupAsync(store, previous, release, cancel); }
        Check(identities.Count == 2 && identities.All(s => !ServerService.IsSessionAlive(s)), "fixture teardown leaked a captured child");
        foreach (var s in identities)
            await Fixtures.AssertListenerReleasedAsync(s.Port, TimeSpan.FromSeconds(5), "captured fixture cleanup");
    }

    public static async Task RunSnapshotAsync()
    {
        using var self = Process.GetCurrentProcess();
        var identity = new ServerSession { ProcessId = self.Id, StartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks,
            EnginePath = ProcessIdentityService.ExecutablePath(self)!, ModelPath = "fixture" };
        var config = new ManagerConfig();
        config.SetRunning("removed-plugin", identity);
        Equal("removed-plugin", EngineArbiter.ActiveGpuBackend(config));
        try { EngineArbiter.EnsureGpuAvailable(config, "llamacpp", true); throw new Exception("removed live plugin was ignored"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("removed-plugin"), "unknown owner was not named"); }
        config.SetRunning("removed-plugin", null);
        await Task.WhenAll(Task.Run(() => { for (var n = 0; n < 1000; n++) config.SetRunning(ManagerConfig.SlotEmbedding, n % 2 == 0 ? identity : null); }),
            Task.Run(() => { for (var n = 0; n < 1000; n++) Check(EngineArbiter.ActiveGpuBackend(config) == null, "CPU embedding reserved the GPU"); }));
    }
}
