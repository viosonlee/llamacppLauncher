using LlamaManager.Models;

namespace LlamaManager.Engines;

/// <summary>
/// What to launch. In phase 1 the llama.cpp backend still consumes the full
/// <see cref="ManagerConfig"/>; phase 2 replaces this with per-backend settings.
/// </summary>
public sealed record EngineStartRequest
{
    public ManagerConfig Config { get; init; } = new();
    public bool Embedding { get; init; }
    public bool OpenBrowser { get; init; }
}

/// <summary>
/// A backend-specific launch description: executable, arguments, working directory,
/// extra environment and any generated files (e.g. a Strata runconfig).
/// </summary>
public sealed record LaunchPlan(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> GeneratedFiles);
