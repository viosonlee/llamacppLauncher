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
            var slot = backend.Descriptor.Id;
            if (config.GetRunning(slot) != null) return slot;
        }
        // llama.cpp keeps its main session under the "main" slot.
        if (config.RunningServer != null) return "llamacpp";
        return null;
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

    /// <summary>Human-readable name of a backend id for conflict dialogs.</summary>
    public static string DisplayName(string backendId) =>
        BackendRegistry.ById(backendId)?.Descriptor.DisplayName ?? backendId;
}
