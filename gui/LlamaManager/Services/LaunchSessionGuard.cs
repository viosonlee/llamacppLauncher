using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>
/// Readiness belongs to a particular published launch, not just to a port. Check before probing
/// and again after awaited HTTP: a concurrent stop/replacement must not produce a late success.
/// This guard never stops a process or overwrites a session.
/// </summary>
internal static class LaunchSessionGuard
{
    public static void EnsureCurrent(string slot, ServerSession expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ConfigService.ReadConfig().GetRunning(slot) != expected)
            throw new OperationCanceledException("此次启动已停止或被替换。", ct);
        using var process = ServerService.ValidateProcess(expected);
        if (process == null)
            throw new InvalidOperationException($"{slot} 服务进程已退出或身份不匹配，请查看引擎日志。");
    }
}
