namespace LlamaManager.Engines;

/// <summary>
/// The single list of engines the launcher knows about. Built-in backends are
/// registered here; plugin backends (phase 4) are added from manifests.
/// GUI, CLI and autostart all resolve engines through this registry.
/// </summary>
public static class BackendRegistry
{
    private static readonly List<IEngineBackend> Registered = [];
    private static readonly object Gate = new();

    static BackendRegistry()
    {
        Register(new LlamaCppBackend());
        Register(new StrataBackend(StrataBackend.PolyProfile));
    }

    public static void Register(IEngineBackend backend)
    {
        lock (Gate)
        {
            Registered.RemoveAll(b => b.Descriptor.Id == backend.Descriptor.Id);
            Registered.Add(backend);
        }
    }

    public static IReadOnlyList<IEngineBackend> All()
    {
        lock (Gate) return Registered.ToArray();
    }

    public static IEngineBackend? ById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (Gate) return Registered.FirstOrDefault(b => b.Descriptor.Id == id);
    }

    public static IServerBackend? ServerById(string? id) => ById(id) as IServerBackend;

    /// <summary>The default backend when nothing is selected: llama.cpp (the built-in).</summary>
    public static IServerBackend Default => (IServerBackend)All().First(b => b.Descriptor.Id == "llamacpp");

    public static async Task<IReadOnlyList<(IEngineBackend Backend, BackendStatus Status)>> ProbeAllAsync(CancellationToken ct)
    {
        var results = new List<(IEngineBackend, BackendStatus)>();
        foreach (var backend in All())
        {
            BackendStatus status;
            try { status = await backend.ProbeAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { status = BackendStatus.Unavailable("探测异常：" + ex.Message); }
            results.Add((backend, status));
        }
        return results;
    }
}
