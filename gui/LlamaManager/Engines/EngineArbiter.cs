using System.Diagnostics;
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
    /// <summary>
    /// The backend whose session currently occupies the GPU, if any. A session record alone does
    /// not count: crashes, power loss and manual kills leave records behind, and those must not
    /// make the launcher demand a needless conflict dialog before the other engine can start.
    /// </summary>
    public static string? ActiveGpuBackend(ManagerConfig config)
    {
        foreach (var backend in BackendRegistry.All())
        {
            if (!backend.Descriptor.GpuHeavy) continue;
            var id = backend.Descriptor.Id;
            // llama.cpp keeps its main session under the "main" slot; other backends use their id.
            var slot = id == "llamacpp" ? ManagerConfig.SlotMain : id;
            if (IsActuallyRunning(config.GetRunning(slot))) return id;
        }
        return null;
    }

    /// <summary>
    /// A recorded session only occupies the GPU while its process is still alive. An unknown
    /// identity (no PID) keeps the conservative old answer, so a missing process id can never
    /// silently allow a second engine onto the card.
    /// </summary>
    private static bool IsActuallyRunning(ServerSession? session)
    {
        if (session == null) return false;
        if (session.ProcessId <= 0) return true;
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the running GPU-heavy backend that conflicts with starting <paramref name="backendId"/>,
    /// or null when the GPU is free. A backend never conflicts with itself.
    /// </summary>
    public static string? ConflictFor(ManagerConfig config, string backendId)
    {
        var backend = BackendRegistry.ById(backendId);
        if (backend == null || !backend.Descriptor.GpuHeavy) return null;
        var active = ActiveGpuBackend(config);
        return active != null && active != backendId ? active : null;
    }

    /// <summary>
    /// Whether this backend currently has a live service. llama.cpp checks both the main and
    /// embedding slots; other backends use their own slot. A stale record is not considered live.
    /// </summary>
    public static bool IsBackendRunning(string backendId)
    {
        var config = ConfigService.ReadConfig();
        if (backendId == "llamacpp")
        {
            using var main = ServerService.GetManagedProcess(config);
            if (main != null) return true;
            using var embedding = ServerService.GetEmbeddingProcess(config);
            return embedding != null;
        }

        return ServerService.IsSessionAlive(config.GetRunning(backendId));
    }

    /// <summary>
    /// Shared start gate for every entry point. The GUI asks <see cref="ConflictFor"/> first so it
    /// can offer to stop the other engine; CLI, autostart and any programmatic start call this so
    /// they can never quietly put two GPU-heavy engines on the same card.
    /// </summary>
    public static void EnsureCanStart(ManagerConfig config, string backendId)
    {
        var conflict = ConflictFor(config, backendId);
        if (conflict == null) return;
        throw new InvalidOperationException(
            $"{DisplayName(conflict)} 正在占用显卡，已拒绝启动 {DisplayName(backendId)}。" +
            $"请先停止 {DisplayName(conflict)} 再启动，避免两个引擎同时抢占显存。");
    }

    /// <summary>Same gate, but re-reads the session store so a stale in-memory copy cannot decide.</summary>
    public static void EnsureCanStart(string backendId) => EnsureCanStart(ConfigService.ReadConfig(), backendId);

    /// <summary>Human-readable name of a backend id for conflict dialogs.</summary>
    public static string DisplayName(string backendId) =>
        BackendRegistry.ById(backendId)?.Descriptor.DisplayName ?? backendId;
}
