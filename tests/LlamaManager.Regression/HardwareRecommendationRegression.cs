using System.IO;
using System.Text.Json;
using LlamaManager.Engines;
using LlamaManager.Services;
using static Program;

/// <summary>Pure fixtures and injected queries only: no GPU, subprocess, network or real configuration.</summary>
internal static class HardwareRecommendationRegression
{
    private const long Mib = 1L << 20;
    private const long Gib = 1L << 30;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "llamamanager-hardware-fixture");
    private static readonly string Cache = HardwareProfileService.DefaultCachePath(Root);
    private static readonly KvmemModelInfo Model = new(Path.Combine(Root, "Cyber-Tiel-Coder-35B-A3B.gguf"),
        "qwen35moe", (long)(14.2 * Gib), "sample-signature-fixture");

    private static HardwareSnapshot Reference() => new()
    {
        CapturedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), CpuName = "AMD Ryzen 7 5800H",
        PhysicalCores = 8, LogicalProcessors = 16, TotalRamBytes = (long)(23.4 * Gib), AvailableRamBytes = 20 * Gib,
        Gpus = [new GpuHardwareInfo { Index = 0, Uuid = "GPU-fixture-0", Name = "NVIDIA GeForce RTX 3060 Laptop GPU",
            TotalMemoryBytes = 6144 * Mib, FreeMemoryBytes = 5800 * Mib, UsedMemoryBytes = 344 * Mib, ComputeCapability = 8.6 }],
        CacheDisk = new DiskHardwareInfo { CachePath = Cache, VolumeRoot = Path.GetPathRoot(Cache),
            TotalBytes = 500 * Gib, FreeBytes = 100 * Gib, MediaType = null }
    };
    private static KvmemRecommendation Recommend(HardwareSnapshot? snapshot = null, KvmemModelInfo? model = null,
        string version = "v0.19.1", string? path = null) =>
        KvmemRecommendationService.Recommend(snapshot ?? Reference(), model ?? Model, version, path ?? Cache);

    public static void Run()
    {
        var h = Reference();
        var r = Recommend(h);
        Check(r.CanApply && !r.IsMeasured, "known baseline should be an applicable but unmeasured suggestion");
        Check(r.Scope.Reusable, "signed complete fixture must have a reusable model/engine/hardware scope");
        var expected = new Dictionary<string, string>
        {
            ["context"] = "131072", ["kvmemBudget"] = "8192", ["kvmemGenReserve"] = "512",
            ["kvmemHostMib"] = "256", ["kvmemDiskMib"] = "8192", ["kvmemSessions"] = "2",
            ["kv"] = "q8_0", ["promptBatch"] = "128", ["stageMib"] = "128", ["threads"] = "8",
            ["vramReserveMib"] = "512", ["tailSkip"] = "0", ["cacheBias"] = "0",
            ["tensorCoreMma"] = "0", ["polyFlashAttention"] = "1"
        };
        foreach (var (key, value) in expected) Equal(value, r.Parameters[key]);
        Equal(Cache, r.Parameters["kvmemDiskPath"]);
        Check(r.Reasons.Any(s => s.Contains("4751")) && r.Warnings.Any(s => s.Contains("未实测")), "scope evidence must not claim a global optimum");
        Check(r.Warnings.Any(s => s.Contains("介质")), "unknown disk medium must be disclosed");
        var schema = new StrataBackend(StrataBackend.KvmemProfile).Parameters.Select(p => p.Key).ToHashSet();
        Check(r.Parameters.Keys.All(schema.Contains), "recommendation emitted unsupported parameter keys");
        Check(!Recommend(h with { TotalRamBytes = null }).CanApply, "unknown total RAM must reject automatic apply");
        Check(!Recommend(h with { TotalRamBytes = 16 * Gib }).CanApply, "model plus OS/workspace does not fit 16 GiB RAM");
        Check(!Recommend(model: Model with { SizeBytes = null }).CanApply, "unknown model size must reject");
        Check(!Recommend(model: Model with { SizeBytes = 22 * Gib }).CanApply, "oversize model must reserve RAM before KV");
        Check(!Recommend(h with { TotalRamBytes = 256 * Gib }, Model with { SizeBytes = 64 * Gib }).CanApply,
            "large MoE cannot silently inherit the 35B-size heuristic even with plentiful RAM");
        Check(!Recommend(model: Model with { Architecture = "qwen3" }).CanApply, "unsupported architecture must reject");
        Check(!Recommend(version: "v0.20.0").CanApply && !Recommend(version: "").CanApply, "version evidence must not generalize");
        Check(!Recommend(h with { Gpus = [] }).CanApply, "no GPU should reject");
        Check(!Recommend(h with { Gpus = [h.Gpus[0] with { ComputeCapability = null }] }).CanApply, "unknown CC should reject");
        Check(!Recommend(h with { Gpus = [h.Gpus[0] with { ComputeCapability = 8.0 }] }).CanApply, "unsupported CC must not use a guessed package");
        Check(!Recommend(h with { Gpus = [h.Gpus[0] with { TotalMemoryBytes = 3 * Gib }] }).CanApply, "tiny GPU should reject");
        Check(!Recommend(model: Model with { ModelFingerprint = null }).Scope.Reusable, "path/size alone is not reusable model identity");

        var busy = h with { AvailableRamBytes = 1 * Gib, Gpus = [h.Gpus[0] with { FreeMemoryBytes = 64 * Mib, UsedMemoryBytes = 6080 * Mib }] };
        var busyResult = Recommend(busy);
        Equal(JsonSerializer.Serialize(r.Parameters), JsonSerializer.Serialize(busyResult.Parameters));
        Equal(r.Fingerprint, busyResult.Fingerprint);
        Check(busyResult.Warnings.Any(w => w.Contains("占用")), "transient service interference must be visible");
        Equal(h.Fingerprint, busy.Fingerprint);
        Equal(h.Fingerprint, (h with { CapturedAtUtc = h.CapturedAtUtc.AddDays(1), Warnings = ["different"],
            CacheDisk = h.CacheDisk! with { FreeBytes = 5 * Gib } }).Fingerprint);
        Check(h.Fingerprint != (h with { TotalRamBytes = 32 * Gib }).Fingerprint, "stable hardware changes must invalidate fingerprint");
        Check(r.Fingerprint != Recommend(model: Model with { ModelFingerprint = "different-content" }).Fingerprint, "model change must invalidate scope");
        Check(r.Fingerprint != Recommend(version: "v0.20.0").Fingerprint, "engine change must invalidate scope");
        Equal(r.Fingerprint, Recommend(version: "0.19.1").Fingerprint);

        var largerGpu = h.Gpus[0] with { Index = 1, Uuid = "GPU-fixture-1", TotalMemoryBytes = 24 * Gib };
        var multi = h with { Gpus = [largerGpu, h.Gpus[0]] };
        Equal("8192", Recommend(multi).Parameters["kvmemBudget"]);
        Equal(multi.Fingerprint, (multi with { Gpus = [h.Gpus[0], largerGpu] }).Fingerprint);
        Check(!Recommend(h with { Gpus = [largerGpu] }).CanApply, "must not pick GPU 1 if index 0 is missing");
        Check(!Recommend(h with { Gpus = [h.Gpus[0], h.Gpus[0]] }).CanApply, "ambiguous GPU 0 must reject");

        foreach (var vram in new[] { 4096, 6144, 8192, 12288, 24576, 49152 })
        {
            var hw = h with { TotalRamBytes = 64 * Gib, Gpus = [h.Gpus[0] with { TotalMemoryBytes = vram * Mib }] };
            var result = Recommend(hw);
            Check(result.CanApply, "supported known capacity should yield a conservative starting point");
            var b = int.Parse(result.Parameters["kvmemBudget"]);
            var reserve = int.Parse(result.Parameters["kvmemGenReserve"]);
            Check(b % 32 == 0 && reserve % 32 == 0 && b + reserve <= int.Parse(result.Parameters["context"]), "KVMem alignment/context invariant");
            Check(int.Parse(result.Parameters["promptBatch"]) <= reserve, "prompt batch exceeds reserve");
            Check(int.Parse(result.Parameters["kvmemHostMib"]) <= 4096, "RAM policy must never blindly allocate 16 GiB");
        }

        foreach (var freeMib in new long[] { 0, 2047, 2048, 2303, 2304, 4096, 10240, 12288, 102400 })
        {
            var result = Recommend(h with { CacheDisk = h.CacheDisk! with { FreeBytes = freeMib * Mib } });
            if (!result.CanApply) { Check(result.Parameters.Count == 0, "refusal should not expose an apply-able partial plan"); continue; }
            var quota = long.Parse(result.Parameters["kvmemDiskMib"]);
            Check(quota % 256 == 0 && quota <= 8192 && quota + Math.Max(2048, freeMib / 10) <= freeMib,
                "SSD quota must fit target free disk space after safety margin");
        }
        Check(!Recommend(h with { CacheDisk = h.CacheDisk! with { FreeBytes = null } }).CanApply, "6 GiB baseline needs known history storage");
        Check(!Recommend(h with { CacheDisk = h.CacheDisk! with { FreeBytes = 600 * Gib } }).CanApply, "invalid free > total disk should refuse");
        Check(!Recommend(path: Path.Combine(Root, "different-cache")).CanApply, "must not reuse another path's disk observation");
        Check(!Recommend(path: @"\\server\share\cache").CanApply, "network path must not become a local SSD recommendation");
        var unknownCpu = Recommend(h with { PhysicalCores = null, LogicalProcessors = null });
        Equal("1", unknownCpu.Parameters["threads"]);
        Check(unknownCpu.Warnings.Any(w => w.Contains("物理核心数未知")), "unknown physical cores should not be guessed from logical");

        var parsedWarnings = new List<string>();
        var parsed = HardwareProfileService.ParseNvidiaSmi("0, GPU-a, RTX 3060, 6144, N/A, 0, 8.6\n1, GPU-b, GPU, with comma, 24576, 23000, 1576, [N/A]\ninvalid", parsedWarnings);
        Equal(2, parsed.Count);
        Equal(6144 * Mib, parsed[0].TotalMemoryBytes!.Value);
        Check(parsed[0].FreeMemoryBytes == null && parsed[0].UsedMemoryBytes == 0, "N/A must be null but measured zero must stay zero");
        Equal("GPU, with comma", parsed[1].Name);
        Check(parsed[1].ComputeCapability == null && parsedWarnings.Count > 0, "unknown CC/malformed row must warn");
        var roundTrip = JsonSerializer.Deserialize<HardwareSnapshot>(JsonSerializer.Serialize(h))!;
        Equal(h.Fingerprint, roundTrip.Fingerprint);
        Equal(h.CacheDisk!.FreeBytes, roundTrip.CacheDisk!.FreeBytes);
        var unknownRoundTrip = JsonSerializer.Deserialize<HardwareSnapshot>(JsonSerializer.Serialize(new HardwareSnapshot()))!;
        Check(unknownRoundTrip.TotalRamBytes == null, "serialization must preserve unknown values");
    }

    public static async Task RunProbeAsync()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var queries = 0;
        var sawContext = false;
        var service = new HardwareProfileService((path, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref queries);
            sawContext |= SynchronizationContext.Current != null;
            return Task.FromResult(Reference() with { CacheDisk = Reference().CacheDisk! with { CachePath = path } });
        }, TimeSpan.FromSeconds(30), () => now);
        var previousContext = SynchronizationContext.Current;
        Task<HardwareSnapshot> firstTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            firstTask = service.ProbeAsync(Root, CancellationToken.None);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        var first = await firstTask;
        Check(!sawContext, "query's synchronous portion must run off dispatcher synchronization context");
        Equal(Cache, first.CacheDisk!.CachePath);
        Equal(1, queries);
        Check(ReferenceEquals(first, await service.ProbeAsync(Root, CancellationToken.None)), "TTL hit should return cached observation");
        Equal(1, queries);
        now = now.AddSeconds(30);
        await service.ProbeAsync(Root, CancellationToken.None);
        Equal(2, queries);
        await service.ProbeAsync(Root, CancellationToken.None, forceRefresh: true);
        Equal(3, queries);
        await service.ProbeCachePathAsync(Path.Combine(Root, "other-cache"), CancellationToken.None);
        Equal(4, queries);
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        await ExpectCanceled(() => service.ProbeAsync(Root, alreadyCanceled.Token));
        Equal(4, queries);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockCalls = 0;
        var blocking = new HardwareProfileService(async (_, ct) =>
        {
            if (Interlocked.Increment(ref blockCalls) > 1) return Reference();
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Reference();
        });
        using var cancel = new CancellationTokenSource();
        var pending = blocking.ProbeAsync(Root, cancel.Token);
        await entered.Task;
        using var waitCancel = new CancellationTokenSource();
        var gateWait = blocking.ProbeAsync(Root, waitCancel.Token);
        waitCancel.Cancel();
        await ExpectCanceled(() => gateWait);
        cancel.Cancel();
        await ExpectCanceled(() => pending);
        var retried = await blocking.ProbeAsync(Root, CancellationToken.None);
        Check(retried.TotalRamBytes > 0 && blockCalls == 2, "canceled query must not poison cache or retain the gate");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => blocking.ProbeAsync(Root, CancellationToken.None)));
        Check(concurrent.All(item => ReferenceEquals(item, retried)) && blockCalls == 2, "concurrent TTL hits should reuse a single query");

        var release = new TaskCompletionSource<HardwareSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timed = new HardwareProfileService((_, _) => release.Task, queryTimeout: TimeSpan.FromMilliseconds(25));
        var timeout = await timed.ProbeAsync(Root, CancellationToken.None);
        Check(timeout.TotalRamBytes == null && timeout.Warnings.Any(w => w.Contains("超时")), "uncooperative injected query must be bounded and unknown");
        release.TrySetResult(Reference());
        var failure = new HardwareProfileService((_, _) => throw new IOException("fixture query failed"));
        var failed = await failure.ProbeAsync(Root, CancellationToken.None);
        Check(failed.TotalRamBytes == null && failed.Gpus.Count == 0 && failed.Warnings.Any(w => w.Contains("fixture query failed")),
            "probe failure must be explicit unknown rather than fabricated values");
    }

    private static async Task ExpectCanceled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation to propagate instead of becoming successful unknown data");
    }
}
