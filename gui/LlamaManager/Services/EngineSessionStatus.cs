using LlamaManager.Engines;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>Status describes the supplied process identity, never merely the presence of a saved record.</summary>
internal static class EngineSessionStatus
{
    public static async Task<EngineStatus> QueryAsync(EngineSession? session,
        Func<EngineSession, CancellationToken, Task<bool>> ready, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session == null) return new EngineStatus(false, false, null, "未运行");
        var identity = new ServerSession { ProcessId = session.ProcessId, StartTimeUtcTicks = session.StartTimeUtcTicks,
            EnginePath = session.ExecutablePath, ModelPath = session.ModelPath, Args = session.Arguments, Port = session.Port };
        if (!ServerService.IsSessionAlive(identity)) return new EngineStatus(false, false, session.Port, "进程已退出或身份不匹配");
        var isReady = await ready(session, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!ServerService.IsSessionAlive(identity)) return new EngineStatus(false, false, session.Port, "进程已退出或身份不匹配");
        return new EngineStatus(true, isReady, session.Port, isReady ? "已就绪" : "加载中");
    }
}
