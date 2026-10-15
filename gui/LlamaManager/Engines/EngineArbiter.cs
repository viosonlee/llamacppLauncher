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
            if (config.GetRunning(slot) != null) return id;
        }
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
