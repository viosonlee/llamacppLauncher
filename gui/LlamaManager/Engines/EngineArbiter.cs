using System.Diagnostics;
using System.IO;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager.Engines;

/// <summary>
/// One place for engine resource arbitration. Today that is GPU mutual exclusion:
/// only one GPU-heavy backend may run at a time. GUI, CLI and autostart all ask here
/// instead of each writing their own conflict check.
/// </summary>
public static class EngineArbiter
{
    /// <summary>Live owners, including removed/unknown plugins; CPU embedding is a separate slot.</summary>
    private static IEnumerable<string> ActiveOwners(ManagerConfig config)
    {
        foreach (var (slot, session) in config.RunningSnapshot())
        {
            if (slot == ManagerConfig.SlotEmbedding) continue;
            var id = slot == ManagerConfig.SlotMain ? "llamacpp" : slot;
            // Unknown live slots are conservative owners, not silently ignored after plugin removal.
            if (BackendRegistry.ById(id)?.Descriptor.GpuHeavy == false) continue;
            if (ServerService.IsSessionAlive(session)) yield return id;
        }
        var legacy = config.Strata;
        if (legacy.LastPid > 0 && legacy.LastStartedUtcTicks > 0 && !string.IsNullOrWhiteSpace(legacy.LastEnginePath) &&
            ServerService.IsSessionAlive(new ServerSession { ProcessId = legacy.LastPid, StartTimeUtcTicks = legacy.LastStartedUtcTicks,
                EnginePath = legacy.LastEnginePath, ModelPath = legacy.ModelPath }))
            yield return "strata-diagnostic";
    }

    /// <summary>The first validated owner; stale records never occupy GPU resources.</summary>
    public static string? ActiveGpuBackend(ManagerConfig config) => ActiveOwners(config).FirstOrDefault();

    /// <summary>
    /// Refuses a start that would put a second GPU-heavy engine on the same card. Every caller
    /// (GUI, CLI, autostart) goes through this instead of writing its own check; a stale record
    /// with no live process never blocks a start.
    /// </summary>
    public static void EnsureGpuAvailable(ManagerConfig config, string backendId)
    {
        var backend = BackendRegistry.ById(backendId);
        if (backend == null || !backend.Descriptor.GpuHeavy) return;
        EnsureGpuAvailable(config, backendId, backend.Descriptor.GpuHeavy, displayName: backend.Descriptor.DisplayName);
    }

    /// <summary>
    /// Returns the running GPU-heavy backend that conflicts with starting <paramref name="backendId"/>,
    /// or null when the GPU is free. A backend never conflicts with itself.
    /// </summary>
    public static string? ConflictFor(ManagerConfig config, string backendId)
    {
        var backend = BackendRegistry.ById(backendId);
        if (backend == null || !backend.Descriptor.GpuHeavy) return null;
        return ActiveOwners(config).FirstOrDefault(owner => owner != backendId);
    }

    /// <summary>Actual launch policy; directly constructed/unknown plugin descriptors cannot bypass it.</summary>
    public static void EnsureGpuAvailable(ManagerConfig config, string backendId, bool gpuHeavy,
        bool allowSameBackend = false, string? displayName = null)
    {
        if (!gpuHeavy) return;
        var owner = ActiveOwners(config).FirstOrDefault(id => !allowSameBackend || id != backendId);
        if (owner == null) return;
        throw new InvalidOperationException(owner == backendId
            ? $"{DisplayName(owner)} 已在运行；不会启动第二个实例。"
            : $"已有 {DisplayName(owner)} 服务占用显卡；请先停止它，再启动 {displayName ?? DisplayName(backendId)}。");
    }

    public static Task<FileStream> AcquireGpuStartLeaseAsync(CancellationToken ct) =>
        AcquireGpuStartLeaseAsync(ct, null);

    internal static Task<FileStream> AcquireGpuStartLeaseAsync(CancellationToken ct, Action? onContention, TimeSpan? timeout = null) =>
        AcquireFileLeaseAsync("gpu-start.lock", ct, onContention, timeout ?? TimeSpan.FromSeconds(10));

    public static Task<FileStream> AcquireBackendLeaseAsync(string backendId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(backendId) || backendId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("无效的引擎锁标识。", nameof(backendId));
        return AcquireFileLeaseAsync($"backend-{backendId}.lock", ct, null, TimeSpan.FromSeconds(15));
    }

    private static Task<FileStream> AcquireFileLeaseAsync(string name, CancellationToken ct, Action? onContention, TimeSpan timeout)
    {
        var path = Path.Combine(ConfigService.GetManagerRoot(), "logs", name);
        // File handles are not thread-affine. Keep first filesystem access off the dispatcher too.
        return Task.Run(async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var elapsed = Stopwatch.StartNew();
            var notified = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    if (ct.IsCancellationRequested) { stream.Dispose(); ct.ThrowIfCancellationRequested(); }
                    return stream;
                }
                catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
                {
                    if (!notified) { notified = true; onContention?.Invoke(); }
                    if (elapsed.Elapsed >= timeout)
                        throw new InvalidOperationException("另一个启动/实测事务正在使用引擎资源，请稍后再试。", ex);
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>Only the newly created, fully verified process may be cleaned up before publication.</summary>
    public static async Task CleanupUnpublishedAsync(Process process, ServerSession? captured, string expectedExecutable)
    {
        if (process.HasExited) return;
        var identity = captured ?? new ServerSession { ProcessId = process.Id,
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks, EnginePath = expectedExecutable };
        using var owned = ServerService.ValidateProcess(identity);
        if (owned == null) throw new InvalidOperationException("无法确认新启动进程身份；不会停止身份不匹配的进程。");
        owned.Kill(entireProcessTree: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await owned.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Do not offer automatic stop for a removed plugin or a legacy diagnostic owner.</summary>
    internal static IServerBackend AutomaticStopBackend(string backendId) => BackendRegistry.ServerById(backendId)
        ?? throw new InvalidOperationException($"{DisplayName(backendId)} 的存活进程正在占用显卡，但没有已注册的停止接口；不能自动停止。请通过原启动工具停止该服务后重试。");

    /// <summary>Human-readable name of a backend id for conflict dialogs.</summary>
    public static string DisplayName(string backendId) =>
        BackendRegistry.ById(backendId)?.Descriptor.DisplayName ?? backendId;
}
