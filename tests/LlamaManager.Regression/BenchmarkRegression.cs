using System.Diagnostics;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class BenchmarkRegression
{
    public static async Task RunFailureAndCancellationAsync(string model)
    {
        // Only an isolated fake service is started/stopped. The sweep must leave it intact.
        var config = new ManagerConfig { ModelPath = model, Port = Fixtures.FreePort() };
        config.Args = $"--host 127.0.0.1 --port {config.Port} -ngl 0";
        ConfigService.SaveConfig(config);
        Check(await ServerService.StartAsync(config), "fixture service did not start");
        var session = config.RunningServer!;
        var variantPorts = new List<int>();
        void ObserveStart(string message)
        {
            const string marker = "启动：";
            var start = message.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0) variantPorts.Add(new LauncherArguments(message[(start + marker.Length)..]).GetPort());
        }
        try
        {
            var failed = await ParameterBenchmarkService.RunAsync(model, "",
                [("立即退出", "--fake-exit"), ("正常", "--fake-timings")], 1,
                ObserveStart, CancellationToken.None, _ => Task.FromResult(5900));
            Equal(0, failed.Variants[0].SuccessfulRuns);
            Equal(1, failed.Variants[1].SuccessfulRuns);
            Check(failed.Errors.Count == 1 && failed.Errors[0].Contains("fixture early exit"),
                "early-exit diagnostics lost the engine stderr: " + string.Join(" | ", failed.Errors));

            using var cancelled = new CancellationTokenSource();
            var sawFirstRun = false;
            try
            {
                await ParameterBenchmarkService.RunAsync(model, "", [("取消", "--fake-timings")], 2,
                    message =>
                    {
                        ObserveStart(message);
                        if (message.Contains("第 1/2 次")) { sawFirstRun = true; cancelled.Cancel(); }
                    }, cancelled.Token, _ => Task.FromResult(5900));
                throw new Exception("benchmark ignored cancellation after the first run");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            Check(sawFirstRun, "cancellation regression never reached a running variant");
            Check(variantPorts.Count == 3 && variantPorts.All(p => p != 8080 && p != session.Port),
                "a benchmark borrowed the agent/service port");
            foreach (var port in variantPorts)
            {
                var watch = Stopwatch.StartNew();
                while (ServerService.TestPortInUse(port) && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
                Check(!ServerService.TestPortInUse(port), $"benchmark leaked a listener after failure/cancellation: {port}");
            }
            Equal(session, ConfigService.ReadConfig().RunningServer);
            Check(ServerService.IsSessionAlive(session) && await ServerService.TestApiReadyAsync(session.Port),
                "benchmark stopped or replaced the existing fixture service");
        }
        finally { await ServerService.StopAsync(config); }
    }
}
