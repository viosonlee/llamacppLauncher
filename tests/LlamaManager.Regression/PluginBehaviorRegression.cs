using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class PluginBehaviorRegression
{
    private static ProcessPluginBackend Plugin(string root, PluginManifest? manifest = null) =>
        new ProcessPluginBackend(manifest ?? Manifest(), root).Initialize();
    private static PluginManifest Manifest() => new()
    {
        Schema = 1, Id = "behavior-fixture", DisplayName = "Behavior fixture", GpuHeavy = false,
        Executable = new() { ["windows"] = Environment.ProcessPath! },
        Launch = new() { Arguments = ["-m", "{model}", "--port", "{port}"] },
        Parameters = [new() { Key = "port", Type = "Integer", Default = "18080" }]
    };

    public static void RunModelSelection(string root, string model)
    {
        var config = new ManagerConfig { ModelPath = model };
        var selected = Path.Combine(root, "plugin selected.gguf"); File.WriteAllBytes(selected, Fixtures.Gguf());
        config.Engines["behavior-fixture"] = new EngineSettings { ModelPath = selected };
        var backend = Plugin(root);
        var plan = backend.BuildLaunch(new EngineStartRequest { Config = config });
        Equal(selected, new LauncherArguments(string.Join(" ", plan.Arguments.Select(LauncherArguments.Quote))).Get("-m"));
        foreach (var unset in new[] { "", "  " })
        {
            config.Engines["behavior-fixture"].ModelPath = unset;
            Check(backend.BuildLaunch(new EngineStartRequest { Config = config }).Arguments.Contains(model), "legacy main model fallback lost");
        }
    }

    public static void RunPortOverrides(string root)
    {
        var config = new ManagerConfig();
        config.Engines["behavior-fixture"] = new EngineSettings { Port = 18081, Parameters = new() { ["port"] = "18082" } };
        var backend = Plugin(root);
        var request = new EngineStartRequest { Config = config, Port = 18083 };
        Equal(18083, backend.ResolvePort(request));
        Check(backend.BuildLaunch(request).Arguments.Contains("18083"), "one-shot port not passed to actual arguments");
        Equal(18082, backend.ResolvePort(request with { Port = null }));
        config.Engines["behavior-fixture"].Parameters.Remove("port");
        Equal(18081, backend.ResolvePort(request with { Port = null }));
        config.Engines["behavior-fixture"].Port = 0;
        Equal(18080, backend.ResolvePort(request with { Port = null }));
        foreach (var port in new[] { 0, -1, 65536 })
            Throws<ArgumentException>(() => backend.ResolvePort(request with { Port = port }));
        config.Engines["behavior-fixture"].Parameters["Port"] = "18084";
        Equal(18084, backend.ResolvePort(request with { Port = null }));
        var literal = Manifest(); literal.Launch.Arguments = ["-m", "{model}", "--port", "18080"];
        var fixedBackend = Plugin(root, literal);
        Throws<ArgumentException>(() => fixedBackend.BuildLaunch(request));
    }

    public static async Task RunReadinessContractAsync(string root)
    {
        using var server = new HealthFixture();
        var manifest = Manifest(); manifest.Ready.Path = "/ready-custom";
        var backend = Plugin(root, manifest);
        var session = new EngineSession { Port = server.Port };
        Check(await backend.ReadyAsync(session, CancellationToken.None), "custom health path ignored");
        Equal("/ready-custom", server.LastPath);
        manifest.Ready.Type = "tcp";
        server.RejectHttp = true;
        Check(await backend.ReadyAsync(session, CancellationToken.None), "TCP-only readiness incorrectly requires HTTP health");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await backend.ReadyAsync(session, cancelled.Token); throw new Exception("TCP probe ignored cancellation"); }
        catch (OperationCanceledException) { }
        if (Socket.OSSupportsIPv6)
        {
            using var ipv6 = new TcpListener(IPAddress.IPv6Loopback, 0);
            ipv6.Server.DualMode = false; ipv6.Start();
            Check(await backend.ReadyAsync(session with { Port = ((IPEndPoint)ipv6.LocalEndpoint).Port, Arguments = "--host ::1" }, CancellationToken.None),
                "IPv6-only TCP service was probed on IPv4");
        }
    }

    public static void RunInvalidManifest()
    {
        foreach (var change in new Action<PluginManifest>[]
        {
            m => m.Id = "../escape", m => m.Ready.Type = "udp", m => m.Ready.TimeoutSeconds = 0,
            m => m.Ready.TimeoutSeconds = int.MaxValue, m => m.Ready.Path = "https://example.invalid/health",
            m => m.Launch = null!, m => m.Ready = null!, m => m.Parameters = null!,
            m => m.Executable = null!, m => m.Parameters.Add(null!),
            m => m.Launch.Environment["bad=key"] = "bad", m => m.Launch.Arguments.Add(null!)
        })
        {
            var manifest = Manifest(); change(manifest);
            Check(manifest.Validate() != null, "unsafe/malformed manifest accepted");
        }
        Check(Manifest().Validate() == null, "valid manifest rejected");
        var tcp = Manifest(); tcp.Ready.Type = "tcp"; tcp.Ready.Path = "";
        Check(tcp.Validate() == null, "TCP manifest rejected for its unused empty HTTP path");
    }

    public static async Task RunStatusIdentityAsync(string root)
    {
        var backends = new IServerBackend[] { Plugin(root), new StrataBackend(StrataBackend.PolyProfile), new LlamaCppBackend() };
        using var self = Process.GetCurrentProcess();
        var session = new EngineSession { ProcessId = self.Id, StartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks,
            ExecutablePath = ProcessIdentityService.ExecutablePath(self)!, Port = Fixtures.FreePort() };
        foreach (var backend in backends)
        {
            var dead = await backend.StatusAsync(session with { StartTimeUtcTicks = 1 }, CancellationToken.None);
            Check(!dead.Running && !dead.Ready, "stale identity reported running by " + backend.Descriptor.Id);
            var live = await backend.StatusAsync(session, CancellationToken.None);
            Check(live.Running && !live.Ready, "live but unready identity misreported");
        }
    }

    public static async Task RunBoundedProbeAsync(string root)
    {
        var manifest = Manifest(); manifest.Detect = new() { VersionArg = ["--plugin-probe-fixture", "fail"], VersionRegex = @"version: (\S+)" };
        var backend = Plugin(root, manifest);
        Check(!(await backend.ProbeAsync(CancellationToken.None)).Available, "failing version subprocess was reported available");
        manifest.Detect.VersionArg = ["--plugin-probe-fixture", "ok"];
        Equal("1.2.3", (await backend.ProbeAsync(CancellationToken.None)).Version);
        manifest.Detect.VersionRegex = "[";
        Check(!(await backend.ProbeAsync(CancellationToken.None)).Available, "invalid regex was not a controlled failure");
        manifest.Detect.VersionRegex = "(?s)^(.+)$";
        manifest.Detect.VersionArg = ["--plugin-probe-fixture", "path"];
        Check((await backend.ProbeAsync(CancellationToken.None)).Version?.Contains(ConfigService.GetManagerRoot(), StringComparison.OrdinalIgnoreCase) == true,
            "version probe lost the launcher dependency PATH");
        manifest.Detect.VersionRegex = @"version: (\S+)";
        var pidFile = Path.Combine(root, "plugin-probe.pid");
        manifest.Detect.VersionArg = ["--plugin-probe-fixture", "hang", pidFile];
        using var cancelled = new CancellationTokenSource();
        var probing = backend.ProbeAsync(cancelled.Token);
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(pidFile) && !probing.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Check(File.Exists(pidFile), "probe fixture never started");
        using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
        cancelled.Cancel();
        try { await probing.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("running probe ignored cancellation"); }
        catch (OperationCanceledException)
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check(child.HasExited, "cancelled probe left a child alive");
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }

    public static async Task<int> ProbeHelperAsync(string[] args)
    {
        if (args[1] == "fail") { Console.Error.WriteLine("fixture version failure"); return 7; }
        if (args[1] == "hang")
        {
            // File existence is the parent's barrier: publish only after the writer closes.
            File.WriteAllText(args[2] + ".tmp", Environment.ProcessId.ToString());
            File.Move(args[2] + ".tmp", args[2], overwrite: true);
            await Task.Delay(30000); return 0;
        }
        if (args[1] == "path") { Console.WriteLine(Environment.GetEnvironmentVariable("PATH")); return 0; }
        Console.WriteLine("version: 1.2.3"); return 0;
    }

    private sealed class HealthFixture : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public int Port { get; }
        public string? LastPath { get; private set; }
        public bool RejectHttp { get; set; }
        public HealthFixture()
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        using var reader = new StreamReader(client.GetStream(), leaveOpen: true);
                        var line = await reader.ReadLineAsync(_stop.Token);
                        if (line == null) continue; // TCP-only readiness connects and closes, no HTTP request.
                        LastPath = line.Split(' ')[1];
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                        var status = !RejectHttp && LastPath == "/ready-custom" ? "200 OK" : "404 Not Found";
                        var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}");
                        await client.GetStream().WriteAsync(bytes, _stop.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException) { }
            });
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _loop.GetAwaiter().GetResult(); _stop.Dispose(); }
    }
}
