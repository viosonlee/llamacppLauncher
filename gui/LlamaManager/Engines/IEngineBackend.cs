using LlamaManager.Models;

namespace LlamaManager.Engines;

/// <summary>Sink for human-readable lifecycle messages, so engines never write to the UI directly.</summary>
public interface ILogSink
{
    void Write(string message);
}

/// <summary>A log sink backed by a delegate (GUI passes Dispatcher-bound, CLI passes LogService.Write).</summary>
public sealed class DelegateLogSink(Action<string> write) : ILogSink
{
    public void Write(string message) => write(message);
}

/// <summary>
/// Common surface for every engine: what it is, how to probe it, whether a GGUF can
/// run on it, and the live status of a session.
/// </summary>
public interface IEngineBackend
{
    BackendDescriptor Descriptor { get; }
    IReadOnlyList<ParameterSpec> Parameters { get; }

    Task<BackendStatus> ProbeAsync(CancellationToken ct);
    Task<CompatibilityReport> InspectAsync(string modelPath, CancellationToken ct);
    Task<EngineStatus> StatusAsync(EngineSession? session, CancellationToken ct);
}

/// <summary>
/// An engine reachable as a local HTTP service (llama.cpp, Strata serve, PolyStrata).
/// Lifecycle identity is PID + creation time + executable path.
/// </summary>
public interface IServerBackend : IEngineBackend
{
    LaunchPlan BuildLaunch(EngineStartRequest request);
    Task<EngineSession> StartAsync(EngineStartRequest request, ILogSink log, CancellationToken ct);
    Task StopAsync(EngineSession session, ILogSink log);
    Task<bool> ReadyAsync(EngineSession session, CancellationToken ct);
    int ResolvePort(EngineStartRequest request);
    string HealthUrl(EngineSession session);
    void OpenWebUi(EngineSession session);
}
