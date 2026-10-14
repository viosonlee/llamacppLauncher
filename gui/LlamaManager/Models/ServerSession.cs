using LlamaManager.Services;

namespace LlamaManager.Models;

/// <summary>The process that was launched; independent of the model currently being edited.</summary>
public sealed record ServerSession
{
    public int ProcessId { get; init; }
    public long StartTimeUtcTicks { get; init; }
    public string EnginePath { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public string Args { get; init; } = "";
    public int Port { get; init; }
    public bool Embedding { get; init; }

    /// <summary>Validates the very process that was launched: identity includes PID and start time.</summary>
    public bool Matches(int processId, long startTimeUtcTicks, string enginePath, string commandLine)
    {
        if (processId != ProcessId || startTimeUtcTicks != StartTimeUtcTicks) return false;
        return MatchesFingerprint(enginePath, commandLine);
    }

    /// <summary>
    /// Compares everything except PID and start time, so an externally relaunched
    /// service (autostart, manual restart) can be recognised as the same session.
    /// </summary>
    public bool MatchesFingerprint(string enginePath, string commandLine)
    {
        if (commandLine == null || !PathIdentity.Equals(EnginePath, enginePath)) return false;
        var args = new LauncherArguments(commandLine);
        return PathIdentity.Equals(ModelPath, args.Get("-m")) && args.GetPort() == Port &&
            args.Has("--embedding") == Embedding;
    }
}
