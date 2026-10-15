namespace LlamaManager.Engines;

/// <summary>
/// The process that was launched, independent of the model currently being edited.
/// Identity is PID + creation time + executable path (the same three-part check the
/// llama.cpp path already uses); <see cref="Extra"/> carries backend-specific state.
/// </summary>
public sealed record EngineSession
{
    public string BackendId { get; init; } = "";
    public int ProcessId { get; init; }
    public long StartTimeUtcTicks { get; init; }
    public string ExecutablePath { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public string Arguments { get; init; } = "";
    public int Port { get; init; }
    public Dictionary<string, string> Extra { get; init; } = new();
}
