using System.IO;
using System.Text.Json;
using LlamaManager.Engines;
using LlamaManager.Models;

namespace LlamaManager.Services;

public static class CommandLineService
{
    /// <summary>Consumes the value that must follow the option at <paramref name="i"/>.</summary>
    private static string TakeValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException("命令选项缺少值：" + args[i]);
        return args[++i];
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken ct = default)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant();
        var supported = new[] { "--service", "--start", "--stop", "--status", "--open", "--enable-autostart", "--disable-autostart", "--print-preset", "--bootstrap", "--update", "--doctor", "--help" };
        if (command == "--help")
        {
            await output.WriteLineAsync("LlamaManager --start|--service|--stop|--status|--open|--print-preset|--bootstrap [--all]|--update [--all]|--doctor [--offline]|--enable-autostart|--disable-autostart [--model PATH] [--port N] [--backend ID] [--auto-bootstrap]");
            await output.WriteLineAsync("--update --all：全部注册内置 llamacpp / polystrata / kvmem 及匹配运行库；版本目录验证后原子切换，旧版本只保留最近可回退版本（运行/手工/专用路径受保护）。");
            await output.WriteLineAsync("独立 KVMem：--bootstrap --backend kvmem / --update --backend kvmem；使用完整版本/显卡专用 unified 包，保留原 polystrata；Prism 是手工可选变体。");
            return 0;
        }
        if (command == null || !supported.Contains(command)) throw new ArgumentException("未知命令；使用 --help 查看。");
        var config = ConfigService.ReadConfig();
        var overrideModel = false;
        int? overridePort = null;
        string? overrideBackend = null;
        var autoBootstrap = false;
        var bootstrapAll = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model": config.ModelPath = Path.GetFullPath(TakeValue(args, ref i)); overrideModel = true; break;
                case "--port": overridePort = new LauncherArguments("--port " + TakeValue(args, ref i)).GetPort(); break;
                case "--backend": overrideBackend = TakeValue(args, ref i); break;
                case "--offline": break; // value-less; only meaningful for --doctor
                case "--all": bootstrapAll = true; break; // value-less; for --bootstrap / --update
                case "--bootstrap":
                case "--auto-bootstrap": autoBootstrap = true; break; // value-less; for --start / --service
                default: throw new ArgumentException("未知命令选项：" + args[i]);
            }
        }
        if (overrideModel)
        {
            var preset = ConfigService.GetModelPreset(config, config.ModelPath);
            config.Args = preset?.Args ?? await Task.Run(() => PresetService.GeneratePresetArgs(config.ModelPath, config.Port), ct);
            config.Port = preset?.Port ?? config.Port;
        }
        if (overridePort.HasValue)
        {
            config.Port = overridePort.Value;
            config.Args = new LauncherArguments(config.Args).Set("--port", config.Port.ToString());
        }
        var backendId = overrideBackend ?? config.ActiveBackendId;
        var backend = BackendRegistry.ServerById(backendId)
            ?? throw new ArgumentException($"未知引擎：{backendId}");
        if (overrideModel)
        {
            if (!config.Engines.TryGetValue(backendId, out var engineCfg))
            {
                engineCfg = new EngineSettings();
                config.Engines[backendId] = engineCfg;
            }
            engineCfg.ModelPath = config.ModelPath;
        }
        // Each backend keeps its own session slot ("main" for llama.cpp, its id otherwise), so
        // status/stop/open must resolve through the same mapping StartAsync used - reading the
        // main slot unconditionally reports every other backend as stopped.
        var slot = backendId == "llamacpp" ? ManagerConfig.SlotMain : backendId;
        var recorded = config.GetRunning(slot);
        // Overrides apply to this command only; status/stop always refer to the persisted process identity.
        switch (command)
        {
            case "--service":
            case "--start":
                if (autoBootstrap)
                {
                    var setup = await BootstrapService.RunAsync(backendId, new DelegateLogSink(LogService.Write), ct);
                    foreach (var step in setup)
                        await output.WriteLineAsync($"[{(step.Ok ? "ok" : "failed")}] {backendId}/{step.Name}: {step.Detail}");
                    if (setup.Any(step => !step.Ok)) return 1;
                    // Bootstrap commits only installation paths; launch from those newly recorded
                    // settings while keeping command-only model/port overrides and the active backend.
                    var latest = ConfigService.ReadConfig();
                    if (latest.Engines.TryGetValue(backendId, out var installedSettings))
                    {
                        if (overrideModel) installedSettings.ModelPath = config.ModelPath;
                        config.Engines[backendId] = installedSettings;
                    }
                }
                var session = await backend.StartAsync(new EngineStartRequest { Config = config, Port = overridePort },
                    new DelegateLogSink(LogService.Write), ct);
                return session.ProcessId > 0 ? 0 : 2;
            case "--stop":
                var stopSession = recorded is { } stop ? LlamaCppBackend.ToEngineSession(stop) with { BackendId = backendId } : null;
                await backend.StopAsync(stopSession ?? new EngineSession { BackendId = backendId }, new DelegateLogSink(LogService.Write));
                return 0;
            case "--status":
                using (var process = recorded is { } rec ? ServerService.ValidateProcess(rec) : null)
                {
                    var running = process != null;
                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        running,
                        ready = running && await backend.ReadyAsync(LlamaCppBackend.ToEngineSession(recorded!) with { BackendId = backendId }, ct),
                        backend = backendId,
                        session = running ? recorded : null
                    }));
                }
                return 0;
            case "--open":
                using (var process = recorded is { } open ? ServerService.ValidateProcess(open) : null)
                {
                    if (process == null) throw new InvalidOperationException("服务尚未启动。");
                    backend.OpenWebUi(LlamaCppBackend.ToEngineSession(recorded!) with { BackendId = backendId });
                }
                return 0;
            case "--bootstrap":
                if (bootstrapAll)
                {
                    var allResults = await BootstrapService.RunAllAsync(new DelegateLogSink(LogService.Write), ct);
                    foreach (var (id, steps) in allResults)
                        foreach (var step in steps)
                            await output.WriteLineAsync($"[{(step.Ok ? "ok" : "failed")}] {id}/{step.Name}: {step.Detail}");
                    var anyFailed = allResults.Values.Any(steps => steps.Any(s => !s.Ok));
                    return anyFailed ? 1 : 0;
                }
                var bootstrapSteps = await BootstrapService.RunAsync(backendId, new DelegateLogSink(LogService.Write), ct);
                foreach (var step in bootstrapSteps)
                    await output.WriteLineAsync($"[{(step.Ok ? "ok" : "failed")}] {backendId}/{step.Name}: {step.Detail}");
                return bootstrapSteps.Any(s => !s.Ok) ? 1 : 0;
            case "--update":
            {
                var log = new DelegateLogSink(LogService.Write);
                IReadOnlyList<EngineAcquisitionService.EngineMaintenanceResult> results;
                if (bootstrapAll) results = await EngineAcquisitionService.UpdateAllAsync(log, ct).ConfigureAwait(false);
                else
                {
                    try { results = [new(backendId, await EngineAcquisitionService.UpdateAsync(backendId, log, ct).ConfigureAwait(false), null)]; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { results = [new(backendId, null, ex.Message)]; }
                }
                foreach (var item in results)
                {
                    if (!item.Ok || item.Update == null)
                    {
                        await output.WriteLineAsync($"{item.BackendId}: 更新未完成 — {item.Error ?? "没有可验证结果"}");
                        continue;
                    }
                    var result = item.Update;
                    await output.WriteLineAsync(result.Updated
                        ? $"{item.BackendId}: 已安装/更新 {result.InstalledVersion ?? "未安装"} → {result.LatestVersion}；{result.Detail}"
                        : $"{item.BackendId}: 无需更新；{result.Detail}");
                    if (result.PackageRoot != null) await output.WriteLineAsync("    引擎版本目录：" + result.PackageRoot);
                }
                return results.Any(r => !r.Ok || r.Update == null) ? 1 : 0;
            }
            case "--doctor":
            {
                var checkUpdates = !args.Contains("--offline", StringComparer.OrdinalIgnoreCase);
                var report = await DoctorService.RunAsync(checkUpdates, ct);
                foreach (var finding in report.Findings)
                {
                    await output.WriteLineAsync($"[{finding.Status}] {finding.Engine}: {finding.Detail}");
                    if (finding.Action != null) await output.WriteLineAsync($"    → {finding.Action}");
                }
                await output.WriteLineAsync(report.Summary);
                return report.Healthy ? 0 : 1;
            }
            case "--enable-autostart": await AutostartService.EnableAsync(config); return 0;
            case "--disable-autostart": await AutostartService.DisableAsync(config); return 0;
            case "--print-preset":
                await output.WriteLineAsync(await Task.Run(() => PresetService.GeneratePresetArgs(config.ModelPath, config.Port), ct)); return 0;
            default: return 1;
        }
    }
}
