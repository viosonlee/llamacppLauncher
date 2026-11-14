using System.IO;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class LlamaFeatureGateRegression
{
    public static async Task RunCapabilitiesAsync()
    {
        const string help = "--moe-cache-mib N\n--moe-cache-cpu-share F\n-ctk TYPE -ctv TYPE f16 q8_0 f8_e4m3";
        LlamaCppFeatureGate.ValidateCapabilities("--moe-cache-mib 512 --moe-cache-cpu-share 0.5 -ctk f8_e4m3 --n-cpu-moe 22", help);
        LlamaCppFeatureGate.ValidateCapabilities("--moe-cache-mib=0 --moe-cache-cpu-share=0", help);
        LlamaCppFeatureGate.ValidateCapabilities("--moe-cache-mib 512 -sm tensor --split-mode layer", help);
        Throws<ArgumentException>(() => LlamaCppFeatureGate.ValidateCapabilities("--moe-cache-mib 512 --split-mode layer -sm tensor", help));
        LlamaCppFeatureGate.ValidateCapabilities("-ctk q8_0 --prompt \"literal --moe-cache-mib\"", "old help");
        Throws<NotSupportedException>(() => LlamaCppFeatureGate.ValidateCapabilities("--moe-cache-mib 0", "old help"));
        Throws<NotSupportedException>(() => LlamaCppFeatureGate.ValidateCapabilities("--moe-cache-mib 512", "--moe-cache-mib-extra"));
        Throws<NotSupportedException>(() => LlamaCppFeatureGate.ValidateCapabilities("-ctv f8_e4m3", "f16 q8_0"));
        foreach (var args in new[] { "--moe-cache-mib -1", "--moe-cache-mib nope", "--moe-cache-cpu-share NaN", "--moe-cache-cpu-share 1.01", "--moe-cache-mib 512 -sm tensor" })
            Throws<ArgumentException>(() => LlamaCppFeatureGate.ValidateCapabilities(args, help));
        // No new flags: no filesystem/native probe at all, including on an older optional backend.
        await LlamaCppFeatureGate.EnsureAsync("not-an-engine.exe", "-ctk q8_0 -ngl 1", CancellationToken.None);
    }

    public static async Task RunCacheAsync()
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new FeatureHelpCache(_ => { Interlocked.Increment(ref calls); entered.TrySetResult(); return release.Task; });
        using var cancel = new CancellationTokenSource();
        var cancelled = cache.GetAsync("version1", "fixture", cancel.Token);
        var keep = cache.GetAsync("version1", "fixture", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        try { await cancelled; throw new Exception("cancelled feature waiter succeeded"); } catch (OperationCanceledException) { }
        release.SetResult("help");
        Equal("help", await keep); Equal(1, calls);
        Equal("help", await cache.GetAsync("version2", "fixture", CancellationToken.None)); Equal(2, calls);
        var attempts = 0;
        var retry = new FeatureHelpCache(_ => ++attempts == 1 ? Task.FromException<string>(new IOException("probe failed")) : Task.FromResult("ok"));
        try { await retry.GetAsync("same", "fixture", CancellationToken.None); throw new Exception("failed probe was accepted"); } catch (IOException) { }
        Equal("ok", await retry.GetAsync("same", "fixture", CancellationToken.None));
    }

    public static async Task RunProbeBoundsAsync()
    {
        var start = LlamaManager.Engines.BuiltInInstallation.CreateProbeStart(Environment.ProcessPath!, "--probe-output");
        var large = await ProcessProbe.RunAsync(start, TimeSpan.FromSeconds(5), CancellationToken.None, maxOutputCharacters: 256 * 1024);
        Check(large.Length >= 200000, "bounded feature probe truncated ordinary large help output");
        start = LlamaManager.Engines.BuiltInInstallation.CreateProbeStart(Environment.ProcessPath!, "--probe-output");
        try
        {
            await ProcessProbe.RunAsync(start, TimeSpan.FromSeconds(1), CancellationToken.None, maxOutputCharacters: 1024);
            throw new Exception("oversized feature probe was accepted");
        }
        catch (IOException ex) { Check(ex.Message.Contains("上限"), "output ceiling lost its diagnostic"); }
    }

    public static async Task RunLaunchRefusalAsync(string root, string model)
    {
        var store = new ConfigStore(Path.Combine(root, "llama-manager.config.json"));
        var previous = store.Read(); var spawns = 0;
        var config = new ManagerConfig { ModelPath = model, Port = Fixtures.FreePort() };
        config.Args = $"--host 127.0.0.1 --port {config.Port} --moe-cache-mib 512";
        store.Save(config);
        try
        {
            try
            {
                await ServerService.StartObservedAsync(config, false, null, CancellationToken.None,
                    (_, _) => { spawns++; return Task.CompletedTask; });
                throw new Exception("old engine accepted unsupported MoE cache feature");
            }
            catch (NotSupportedException ex) { Check(ex.Message.Contains("--moe-cache-mib"), "feature error did not identify flag"); }
            Equal(0, spawns);
            Check(store.Read().RunningServer == null, "unsupported feature published a session");
        }
        finally { if (spawns > 0) await ServerService.StopAsync(config); store.Save(previous); }
    }
}
