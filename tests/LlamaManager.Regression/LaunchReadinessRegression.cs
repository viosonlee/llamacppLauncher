using System.Diagnostics;
using System.IO;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class LaunchReadinessRegression
{
    public static async Task RunAsync(string root, string model, bool strata, string transition = "replace")
    {
        var store = new ConfigStore(Path.Combine(root, "llama-manager.config.json"));
        var previous = store.Read();
        var oldGate = Environment.GetEnvironmentVariable("LLAMA_FIXTURE_HEALTH_GATE");
        var id = strata ? "polystrata" : "readiness-fixture";
        var work = Path.Combine(root, "readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var gate = Path.Combine(work, "health");
        var port = Fixtures.FreePort();
        var config = new ManagerConfig { ModelPath = model };
        EngineSession? created = null;
        IServerBackend backend;
        Func<EngineSession, CancellationToken, Task> capture = (session, _) => { created = session; return Task.CompletedTask; };
        if (strata)
        {
            Directory.CreateDirectory(Path.Combine(work, "engine-poly"));
            Directory.CreateDirectory(Path.Combine(work, "serve"));
            File.WriteAllText(Path.Combine(work, "engine-poly", "strata-poly.exe"), "fixture only, never executed");
            File.WriteAllText(Path.Combine(work, "serve", "server.py"), "# fixture only");
            config.Engines[id] = new EngineSettings { ModelPath = model, Parameters = new()
            {
                ["packageRoot"] = work, ["pythonPath"] = Path.Combine(root, "llama-server.exe"), ["servePort"] = port.ToString()
            } };
            var tokenizer = StrataBackend.TokenizerDir(config, model);
            Directory.CreateDirectory(tokenizer);
            File.WriteAllText(Path.Combine(tokenizer, "vocab.json"), "{}");
            backend = new StrataBackend(StrataBackend.PolyProfile) { BeforePublishAsync = capture };
        }
        else
        {
            var manifest = new PluginManifest { Schema = 1, Id = id, DisplayName = "Readiness fixture", GpuHeavy = false,
                Executable = new() { ["windows"] = Path.Combine(root, "llama-server.exe") },
                Launch = new() { Arguments = ["-m", "{model}", "--port", "{port}"] },
                Parameters = [new() { Key = "port", Type = "Integer", Default = port.ToString() }],
                Ready = new() { Type = "http", TimeoutSeconds = 5 } };
            backend = new ProcessPluginBackend(manifest, root) { BeforePublishAsync = capture }.Initialize();
        }
        var messages = new List<string>();
        var log = new DelegateLogSink(messages.Add);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<EngineSession>? launching = null;
        try
        {
            store.Save(config);
            Environment.SetEnvironmentVariable("LLAMA_FIXTURE_HEALTH_GATE", gate);
            launching = backend.StartAsync(new EngineStartRequest { Config = config }, log, timeout.Token);
            var watch = Stopwatch.StartNew();
            while (!File.Exists(gate + ".entered") && !launching.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(10);
            if (launching.IsCompleted) await launching; // Surface the real launch error, not a barrier timeout.
            Check(File.Exists(gate + ".entered") && created != null, "fixture did not reach HTTP readiness barrier");
            // Replace only the persisted slot; the old child remains alive and returns HTTP 200.
            // A stale readiness success must not describe this different session as the old one.
            using var self = Process.GetCurrentProcess();
            var replacement = new ServerSession { ProcessId = self.Id,
                StartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks, EnginePath = ProcessIdentityService.ExecutablePath(self)!,
                ModelPath = "replacement fixture", Port = port };
            if (transition == "replace") ConfigService.SaveSessionSlot(config, id, replacement);
            else if (transition == "cancel") timeout.Cancel();
            else if (transition == "stop") await backend.StopAsync(created!, log);
            else throw new ArgumentException("Unknown fixture transition");
            File.WriteAllText(gate + ".release", "release");
            try { await launching.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("stale launch reported ready after " + transition); }
            catch (OperationCanceledException) { }
            Check(!messages.Any(m => m.Contains("已就绪")), "stale success was written to log");
            if (transition == "replace") Equal(replacement, store.Read().GetRunning(id));
            await backend.StopAsync(created!, log);
            if (transition == "replace") Equal(replacement, store.Read().GetRunning(id));
            else Check(store.Read().GetRunning(id) == null, "explicit stop retained the old session");
            Check(ServerService.IsSessionAlive(replacement), "cleanup touched the replacement process");
        }
        finally
        {
            timeout.Cancel(); File.WriteAllText(gate + ".release", "release");
            if (launching != null) { try { await launching.WaitAsync(TimeSpan.FromSeconds(10)); } catch { } }
            if (created != null) await backend.StopAsync(created, log);
            Environment.SetEnvironmentVariable("LLAMA_FIXTURE_HEALTH_GATE", oldGate);
            store.Save(previous);
        }
    }
}
