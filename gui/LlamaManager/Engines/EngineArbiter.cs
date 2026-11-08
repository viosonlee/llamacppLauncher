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
    /// <summary>Slots that a GPU-heavy backend occupies. Keyed by backend id.</summary>
    private static readonly object Gate = new();

    /// <summary>The backend whose session currently occupies the GPU, if any.</summary>
    public static string? ActiveGpuBackend(ManagerConfig config)
    {
        foreach (var backend in BackendRegistry.All())
        {
            if (!backend.Descriptor.GpuHeavy) continue;
            var id = backend.Descriptor.Id;
            // llama.cpp keeps its main session under the "main" slot; other backends use their id.
            var slot = id == "llamacpp" ? ManagerConfig.SlotMain : id;
            // A crash/reboot/manual kill leaves a record behind. Only a process that still matches
            // the recorded identity may hold the GPU, or nothing else could ever start again.
            if (ServerService.IsSessionAlive(config.GetRunning(slot))) return id;
        }
        return null;
    }

    /// <summary>
    /// Refuses a start that would put a second GPU-heavy engine on the same card. Every caller
    /// (GUI, CLI, autostart) goes through this instead of writing its own check; a stale record
    /// with no live process never blocks a start.
    /// </summary>
    public static void EnsureGpuAvailable(ManagerConfig config, string backendId)
    {
        var backend = BackendRegistry.ById(backendId);
        if (backend == null || !backend.Descriptor.GpuHeavy) return;
        var active = ActiveGpuBackend(config);
        if (active == null) return;
        throw new InvalidOperationException(active == backendId
            ? $"{DisplayName(active)} 已在运行；不会同时启动第二个占用显卡的实例。"
            : $"已有 {DisplayName(active)} 服务占用显卡；请先停止它，再启动 {backend.Descriptor.DisplayName}。");
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
        if (active == backendId) return null; // replacing itself is the GUI's stop-then-start path
        return active;
    }

    /// <summary>Human-readable name of a backend id for conflict dialogs.</summary>
    public static string DisplayName(string backendId) =>
        BackendRegistry.ById(backendId)?.Descriptor.DisplayName ?? backendId;
}
