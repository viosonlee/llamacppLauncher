using System.IO;
using System.Text.Json;
using LlamaManager.Engines;

namespace LlamaManager.Services;

public static class CommandLineService
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken ct = default)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant();
        var supported = new[] { "--service", "--start", "--stop", "--status", "--open", "--enable-autostart", "--disable-autostart", "--print-preset", "--bootstrap", "--help" };
        if (command == "--help")
        {
            await output.WriteLineAsync("LlamaManager --start|--service|--stop|--status|--open|--print-preset|--bootstrap|--enable-autostart|--disable-autostart [--model PATH] [--port N] [--backend ID]");
            return 0;
        }
        if (command == null || !supported.Contains(command)) throw new ArgumentException("未知命令；使用 --help 查看。");
        var config = ConfigService.ReadConfig();
        var overrideModel = false;
        int? overridePort = null;
        string? overrideBackend = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("命令选项缺少值。");
            switch (args[i])
            {
                case "--model": config.ModelPath = Path.GetFullPath(args[++i]); overrideModel = true; break;
                case "--port": overridePort = new LauncherArguments("--port " + args[++i]).GetPort(); break;
                case "--backend": overrideBackend = args[++i]; break;
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
        // Overrides apply to this command only; status/stop always refer to the persisted process identity.
        switch (command)
        {
            case "--service":
            case "--start":
                var session = await backend.StartAsync(new EngineStartRequest { Config = config },
                    new DelegateLogSink(LogService.Write), ct);
                return session.ProcessId > 0 ? 0 : 2;
            case "--stop":
                var stopSession = config.GetRunning(backendId) is { } slot ? LlamaCppBackend.ToEngineSession(slot) : null;
                await backend.StopAsync(stopSession ?? new EngineSession { BackendId = backendId }, new DelegateLogSink(LogService.Write));
                return 0;
            case "--status":
                using (var process = ServerService.GetManagedProcess(config))
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        running = process != null,
                        ready = process != null && await ServerService.TestApiReadyAsync(config.RunningServer!.Port, ct),
                        backend = backendId,
                        session = process == null ? null : config.RunningServer
                    }));
                }
                return 0;
            case "--open":
                using (var process = ServerService.GetManagedProcess(config))
                {
                    if (process == null) throw new InvalidOperationException("服务尚未启动。");
                    backend.OpenWebUi(LlamaCppBackend.ToEngineSession(config.RunningServer!));
                }
                return 0;
            case "--bootstrap":
                await BootstrapService.RunAsync(backendId, new DelegateLogSink(LogService.Write), ct);
                return 0;
            case "--enable-autostart": await AutostartService.EnableAsync(config); return 0;
            case "--disable-autostart": await AutostartService.DisableAsync(config); return 0;
            case "--print-preset":
                await output.WriteLineAsync(await Task.Run(() => PresetService.GeneratePresetArgs(config.ModelPath, config.Port), ct)); return 0;
            default: return 1;
        }
    }
}
