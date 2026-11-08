using System.Diagnostics;
using System.IO;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class GpuArbitrationRegression
{
    /// <summary>Uses this test process as the "live" session; no GPU, model or engine is started.</summary>
    public static void Run()
    {
        using var self = Process.GetCurrentProcess();
        var live = new ServerSession
        {
            ProcessId = self.Id,
            StartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks,
            EnginePath = ProcessIdentityService.ExecutablePath(self) ?? Environment.ProcessPath!,
            Port = 65500, ModelPath = Path.Combine(Path.GetTempPath(), "arbiter.gguf")
        };
        var stale = live with { ProcessId = 999999, StartTimeUtcTicks = 1 };

        var clean = new ManagerConfig();
        Check(EngineArbiter.ActiveGpuBackend(clean) == null, "an empty config claimed the GPU");
        EngineArbiter.EnsureGpuAvailable(clean, "llamacpp");
        EngineArbiter.EnsureGpuAvailable(clean, "kvmem");

        // A crash/reboot leaves a record with no live process. It must not reserve the GPU,
        // otherwise every later start would be refused until the user hand-edits the config.
        var crashed = new ManagerConfig();
        crashed.Running[ManagerConfig.SlotMain] = stale;
        crashed.Running["kvmem"] = stale with { Port = 65501 };
        Check(EngineArbiter.ActiveGpuBackend(crashed) == null, "a dead record still claimed the GPU");
        Check(EngineArbiter.ConflictFor(crashed, "polystrata") == null, "a dead record blocked another engine");
        EngineArbiter.EnsureGpuAvailable(crashed, "polystrata");

        // A live record does hold the GPU: another engine is refused, and the same backend
        // cannot start a second instance either.
        var busy = new ManagerConfig();
        busy.Running[ManagerConfig.SlotMain] = live;
        Equal("llamacpp", EngineArbiter.ActiveGpuBackend(busy));
        // Replacing itself is the GUI's stop-then-start path, not a conflict dialog.
        Check(EngineArbiter.ConflictFor(busy, "llamacpp") == null, "a backend conflicted with itself");
        Equal("llamacpp", EngineArbiter.ConflictFor(busy, "polystrata"));
        Throws<InvalidOperationException>(() => EngineArbiter.EnsureGpuAvailable(busy, "polystrata"));
        Throws<InvalidOperationException>(() => EngineArbiter.EnsureGpuAvailable(busy, "kvmem"));
        Throws<InvalidOperationException>(() => EngineArbiter.EnsureGpuAvailable(busy, "llamacpp"));
        var message = "";
        try { EngineArbiter.EnsureGpuAvailable(busy, "polystrata"); }
        catch (InvalidOperationException ex) { message = ex.Message; }
        Check(message.Contains(EngineArbiter.DisplayName("llamacpp")) && message.Contains("停止"), "the refusal does not name the blocking engine");
    }

    /// <summary>
    /// The CLI and autostart call the backend's StartAsync directly, bypassing the GUI dialog.
    /// A live GPU engine must still be refused there, before any engine process is created.
    /// </summary>
    public static async Task RunStartRefusalAsync(string root)
    {
        using var self = Process.GetCurrentProcess();
        var store = new ConfigStore(Path.Combine(root, "llama-manager.config.json"));
        var original = store.Read();
        var model = Path.Combine(root, "arbiter-model.gguf");
        File.WriteAllBytes(model, Fixtures.StrataGguf());
        var config = new ManagerConfig { ModelPath = model };
        config.Strata.ModelPath = model;
        config.Engines["polystrata"] = new EngineSettings { ModelPath = model };
        config.Running[ManagerConfig.SlotMain] = new ServerSession
        {
            ProcessId = self.Id,
            StartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks,
            EnginePath = ProcessIdentityService.ExecutablePath(self) ?? Environment.ProcessPath!,
            Port = 65502, ModelPath = model
        };
        try
        {
            store.Save(config);
            var log = new DelegateLogSink(_ => { });
            var strata = new StrataBackend(StrataBackend.PolyProfile);
            try
            {
                await strata.StartAsync(new EngineStartRequest { Config = config }, log, CancellationToken.None);
                throw new Exception("a second GPU engine was allowed to start next to a live one");
            }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("占用显卡"), "wrong refusal: " + ex.Message); }
            // Nothing was launched, so the record must still be the one we wrote.
            Equal(self.Id, ConfigService.ReadConfig().Running[ManagerConfig.SlotMain].ProcessId);
        }
        finally { store.Save(original); }
    }
}