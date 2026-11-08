using System.IO;
using LlamaManager.Services;
using static Program;

internal static class CudaCacheRegression
{
    public static async Task RunAsync()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var ttl = TimeSpan.FromMinutes(5);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<CudaInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        var cache = new CudaProbeCache(_ =>
        {
            var count = Interlocked.Increment(ref probes);
            entered.TrySetResult();
            return count == 1 ? pending.Task : Task.FromResult(new CudaInfo { Available = true, FreeVramMb = 2000 + count });
        }, () => now, ttl);

        var first = cache.GetAsync("engine|version1", "engine");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        now += ttl + TimeSpan.FromSeconds(1);
        foreach (var request in Enumerable.Range(0, 20).Select(_ => cache.GetAsync("engine|version1", "engine")))
            Check(ReferenceEquals(first, request), "expiry duplicated an in-flight probe");
        Equal(1, probes);
        pending.SetResult(new CudaInfo { Available = true, FreeVramMb = 3000 });
        await first;

        // Once the old probe completes, an expired observation is refreshed automatically.
        var refresh = cache.GetAsync("engine|version1", "engine");
        Equal(2002, (await refresh).FreeVramMb);
        Equal(2, probes);
        now += ttl - TimeSpan.FromMilliseconds(1);
        Check(ReferenceEquals(refresh, cache.GetAsync("engine|version1", "engine")), "fresh result was re-probed");
        now += TimeSpan.FromMilliseconds(1);
        var expired = Enumerable.Range(0, 20).Select(_ => cache.GetAsync("engine|version1", "engine")).ToArray();
        await Task.WhenAll(expired);
        Check(expired.All(t => ReferenceEquals(t, expired[0])), "expired callers did not share a single new probe");
        Equal(3, probes);
        Equal(2003, expired[0].Result.FreeVramMb);
        cache.Clear();
        Equal(2004, (await cache.GetAsync("engine|version1", "engine")).FreeVramMb);
        Equal(4, probes);
        await cache.GetAsync("engine|version2", "engine");
        Equal(5, probes);

        // Failed observations also expire; no executable change or manual invalidation required.
        var attempts = 0;
        var failures = new CudaProbeCache(_ => Task.FromResult(new CudaInfo { Available = ++attempts > 1 }), () => now, ttl);
        Check(!(await failures.GetAsync("broken", "engine")).Available, "failure fixture was wrong");
        now += ttl;
        Check((await failures.GetAsync("broken", "engine")).Available, "failed observation remained cached forever");
    }

    // Opt-in hardware check: the real executable is only asked for devices/help, never to load a model.
    public static async Task RunNativeAsync(string executable)
    {
        executable = Path.GetFullPath(executable);
        Check(File.Exists(executable), "real CUDA executable does not exist");
        var now = DateTimeOffset.UtcNow;
        var ttl = TimeSpan.FromMinutes(5);
        var probes = 0;
        var cache = new CudaProbeCache(async path =>
        {
            Interlocked.Increment(ref probes);
            var devices = await ProcessProbe.RunAsync(path, "--list-devices", TimeSpan.FromSeconds(5));
            var help = await ProcessProbe.RunAsync(path, "--help", TimeSpan.FromSeconds(5));
            return CudaService.Parse(devices) with { SupportsFit = help.Contains("--fit "), SupportsFlash = help.Contains("--flash-attn") };
        }, () => now, ttl);
        var first = await cache.GetAsync(executable, executable);
        Check(first.Available && first.TotalVramMb > 0, "real CUDA device probe failed: " + first.Text);
        var fresh = await cache.GetAsync(executable, executable);
        Check(ReferenceEquals(first, fresh), "fresh native result was not shared");
        Equal(1, probes);
        now += ttl;
        var refreshed = await cache.GetAsync(executable, executable);
        Equal(2, probes);
        Check(refreshed.Available && refreshed.TotalVramMb == first.TotalVramMb, "TTL native refresh changed the device identity");
        cache.Clear();
        await cache.GetAsync(executable, executable);
        Equal(3, probes);
        Console.WriteLine($"PASS native CUDA cache: total={first.TotalVramMb} MiB; free={first.FreeVramMb}->{refreshed.FreeVramMb} MiB; native probes=3; fresh hits=1; automatic TTL refresh and manual refresh verified.");
    }
}
