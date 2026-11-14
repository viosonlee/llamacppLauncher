using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using LlamaManager.Engines;
using LlamaManager.Services;

/// <summary>Real disposable children and loopback HTTP only; no PATH, GPU, installer or config access.</summary>
internal static class ProcessAndStreamRegression
{
    public const string ChildSwitch = "--process-stream-child";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(8);
    private const string Token = "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n";
    private const string Finish = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string Done = "data: [DONE]\n\n";

    // Register these four methods with Program.Test; dispatch ChildSwitch before normal startup.
    public static Task RunPythonInstallerAsync() => CasesAsync(
        PythonDualPipeAsync, PythonNonzeroAsync, PythonCancelTreeAsync, PythonPrecancelAsync, PythonMissingAsync);
    public static Task RunGpuMemoryAsync() => CasesAsync(
        GpuStderrAsync, GpuTimeoutAsync, GpuCancelTreeAsync, GpuFailedExitAsync, GpuLargeOutputAsync, GpuPrecancelAsync);
    public static Task RunSpeedDeadlineAsync() => CasesAsync(
        SpeedHeadersTimeoutAsync, SpeedPartialLineTimeoutAsync, SpeedDiscoveryTimeoutAsync, SpeedCallerCancelAsync);
    public static Task RunSpeedCompletionAsync() => CasesAsync(
        SseErrorAsync, SseEventErrorAsync, SseAbruptEofAsync, SseDoneAsync, SseFinishAsync, SseMalformedAsync, SseEmptyAsync);

    private static async Task CasesAsync(params Func<Task>[] cases)
    {
        var errors = new List<Exception>();
        foreach (var test in cases)
        {
            try { await test(); Console.WriteLine("  CASE PASS " + test.Method.Name); }
            catch (Exception ex)
            {
                Console.WriteLine("  CASE FAIL " + test.Method.Name + ": " + ex.Message);
                errors.Add(new Exception(test.Method.Name, ex));
            }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Sink : ILogSink
    {
        public List<string> Lines { get; } = [];
        public void Write(string message) => Lines.Add(message);
    }

    private static readonly Func<string, IReadOnlyList<string>, ILogSink, CancellationToken, Task<int>> PythonRunner =
        typeof(PythonInstaller).GetMethod("DefaultRunner", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<string, IReadOnlyList<string>, ILogSink, CancellationToken, Task<int>>>();

    private static Task<int> PythonAsync(Child child, CancellationToken ct) =>
        child.Track(PythonRunner(child.Start.FileName, child.Start.ArgumentList.ToArray(), new Sink(), ct));

    private static Task<int> GpuAsync(Child child, TimeSpan timeout, CancellationToken ct) =>
        child.Track((Task<int>)typeof(GpuMemoryService).GetMethod("ReadUsedMibAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [child.Start, timeout, ct])!);

    private static async Task PythonDualPipeAsync()
    {
        await using var child = new Child("output");
        Check(await PythonAsync(child, CancellationToken.None).WaitAsync(Guard) == 0,
            "dual redirected pipes did not complete successfully");
        await child.AssertExitedAsync();
    }

    private static async Task PythonNonzeroAsync()
    {
        await using var child = new Child("nonzero");
        Check(await PythonAsync(child, CancellationToken.None).WaitAsync(Guard) == 37,
            "nonzero installer exit code was lost");
        await child.AssertExitedAsync();
    }

    private static async Task PythonCancelTreeAsync()
    {
        await using var child = new Child("tree");
        using var cancel = new CancellationTokenSource();
        var run = PythonAsync(child, cancel.Token);
        await child.ReadyAsync();
        cancel.Cancel();
        await ExpectCanceledAsync(run);
        await child.AssertExitedAsync();
    }

    private static async Task PythonPrecancelAsync()
    {
        await using var child = new Child("hang");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await ExpectCanceledAsync(PythonAsync(child, cancel.Token));
        await Task.Delay(300);
        Check(!File.Exists(child.Marker), "pre-canceled installer still launched a process");
    }

    private static async Task PythonMissingAsync()
    {
        var sink = new Sink();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-installer.exe");
        Check(await PythonRunner(path, [], sink, CancellationToken.None) == -1, "missing executable must return -1");
        Check(sink.Lines.Count == 1, "missing executable must log a failure");
    }

    private static async Task GpuStderrAsync()
    {
        await using var child = new Child("stderr");
        Check(await GpuAsync(child, Guard, CancellationToken.None).WaitAsync(Guard) == 321,
            "stderr backpressure prevented a valid VRAM sample");
        await child.AssertExitedAsync();
    }

    private static async Task GpuTimeoutAsync()
    {
        await using var child = new Child("hang");
        var watch = Stopwatch.StartNew();
        Check(await GpuAsync(child, TimeSpan.FromSeconds(1), CancellationToken.None).WaitAsync(Guard) == 0,
            "timed-out GPU query must return unavailable (0)");
        Check(watch.Elapsed < TimeSpan.FromSeconds(5), "GPU query ignored its deadline");
        await child.AssertExitedAsync();
    }

    private static async Task GpuCancelTreeAsync()
    {
        await using var child = new Child("tree");
        using var cancel = new CancellationTokenSource();
        var run = GpuAsync(child, Guard, cancel.Token);
        await child.ReadyAsync();
        cancel.Cancel();
        await ExpectCanceledAsync(run);
        await child.AssertExitedAsync();
    }

    private static async Task GpuFailedExitAsync()
    {
        await using var child = new Child("bad-gpu");
        Check(await GpuAsync(child, Guard, CancellationToken.None).WaitAsync(Guard) == 0,
            "failed GPU command was accepted as a valid sample");
        await child.AssertExitedAsync();
    }

    private static async Task GpuLargeOutputAsync()
    {
        await using var child = new Child("large-gpu");
        Check(await GpuAsync(child, Guard, CancellationToken.None).WaitAsync(Guard) == 321,
            "bounded GPU capture did not drain/retain the first device");
        await child.AssertExitedAsync();
    }

    private static async Task GpuPrecancelAsync()
    {
        await using var child = new Child("hang");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await ExpectCanceledAsync(GpuAsync(child, Guard, cancel.Token));
        await Task.Delay(300);
        Check(!File.Exists(child.Marker), "pre-canceled GPU query still launched a process");
    }

    private static async Task ExpectCanceledAsync(Task<int> task)
    {
        try { await task.WaitAsync(Guard); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("caller cancellation did not propagate");
    }

    private static Task SpeedHeadersTimeoutAsync() => DeadlineAsync("", false);
    private static Task SpeedPartialLineTimeoutAsync() => DeadlineAsync("data: {\"choices\":", false);
    private static Task SpeedDiscoveryTimeoutAsync() => DeadlineAsync("", true);

    private static async Task DeadlineAsync(string partial, bool stallModels)
    {
        await using var server = new HttpFixture(partial, hang: true, stallModels: stallModels);
        // A separate watchdog is not the tested timeout. It makes a broken old implementation
        // observable as cancellation instead of hanging the regression runner forever.
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var watch = Stopwatch.StartNew();
        var result = await SpeedTestService.RunAsync(server.Port, TimeSpan.FromSeconds(1), watchdog.Token);
        Check(server.Stalled.Task.IsCompletedSuccessfully, "server never reached the stalled response body");
        Check(!result.Success && result.Error!.Contains("超时", StringComparison.Ordinal),
            "body deadline did not return a timeout Result: " + result.Error);
        Check(watch.Elapsed < TimeSpan.FromSeconds(3), "body deadline only ended at the caller watchdog");
        await server.Disconnected.Task.WaitAsync(Guard);
    }

    private static async Task SpeedCallerCancelAsync()
    {
        await using var server = new HttpFixture(Token, hang: true);
        using var cancel = new CancellationTokenSource();
        var run = SpeedTestService.RunAsync(server.Port, cancel.Token); // Existing API remains valid.
        await server.Stalled.Task.WaitAsync(Guard);
        cancel.Cancel();
        var result = await run.WaitAsync(Guard);
        Check(!result.Success && result.Error == "测速已取消", "speed cancellation must return Result, not throw/succeed");
        await server.Disconnected.Task.WaitAsync(Guard);
    }

    private static async Task<SpeedTestService.Result> ResponseAsync(string body)
    {
        await using var server = new HttpFixture(body);
        return await SpeedTestService.RunAsync(server.Port).WaitAsync(Guard);
    }

    private static async Task SseErrorAsync()
    {
        var result = await ResponseAsync(Token + "data: {\"error\":{\"message\":\"fixture OOM\",\"type\":\"server_error\"}}\n\n" + Done);
        Check(!result.Success && result.Error!.Contains("fixture OOM"), "partial output hid SSE error: " + result.Error);
        Check(result.StreamEvents == 1, "partial output diagnostics were discarded");
    }

    private static async Task SseEventErrorAsync()
    {
        var result = await ResponseAsync(Token + "event: error\ndata: {\"message\":\"fixture stream failed\"}\n\n" + Done);
        Check(!result.Success && result.Error!.Contains("fixture stream failed"), "named SSE error was ignored");
    }

    private static async Task SseAbruptEofAsync()
    {
        var result = await ResponseAsync(Token); // Valid JSON and clean transport EOF, no terminal SSE signal.
        Check(!result.Success, "partial SSE at EOF was falsely successful");
        Check(result.StreamEvents == 1, "partial event count lost");
    }

    private static async Task SseDoneAsync()
    {
        var result = await ResponseAsync(": keepalive\n\n" + Token +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":4,\"total_tokens\":16}," +
            "\"timings\":{\"predicted_per_second\":42,\"prompt_per_second\":123,\"draft_n\":10,\"draft_n_accepted\":7}}\n\n" + Done);
        Check(result.Success, "[DONE] completion failed: " + result.Error);
        Check(result.Model == "fixture-model" && result.StreamEvents == 1 && result.HasUsage && result.CompletionTokens == 4 &&
            result.TotalTokens == 16 && result.PromptTokens == 12 && result.DecodeTps == 42 && result.PrefillTps == 123 &&
            result.DraftAcceptance == 0.7 && result.TtftSeconds.HasValue, "completed stream lost telemetry");
    }

    private static async Task SseFinishAsync()
    {
        var result = await ResponseAsync(Token + Finish);
        Check(result.Success, "finish_reason + EOF compatibility broken: " + result.Error);
    }

    private static async Task SseMalformedAsync()
    {
        var result = await ResponseAsync(Token + "data: {broken\n\n" + Done);
        Check(!result.Success, "malformed chunk after content was successful");
    }

    private static async Task SseEmptyAsync()
    {
        var result = await ResponseAsync(Done);
        Check(!result.Success && result.Error!.Contains("生成内容"), "empty completion must still fail");
    }

    // Exported child dispatch: Main must route ChildSwitch here BEFORE creating its normal root.
    public static async Task<int> DispatchAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != ChildSwitch) throw new ArgumentException("Invalid process-stream fixture arguments.");
        var mode = args[1];
        var directory = args[2];
        Directory.CreateDirectory(directory);
        if (mode == "tree")
        {
            var leaf = SelfStart("hang", Path.Combine(directory, "leaf"));
            using var process = Process.Start(leaf) ?? throw new IOException("Leaf did not start");
            await WaitForFileAsync(Path.Combine(directory, "leaf", "identity"));
        }
        using (var self = Process.GetCurrentProcess())
            File.WriteAllLines(Path.Combine(directory, "identity.tmp"),
                [Environment.ProcessId.ToString(), self.StartTime.ToUniversalTime().Ticks.ToString(), self.MainModule!.FileName]);
        File.Move(Path.Combine(directory, "identity.tmp"), Path.Combine(directory, "identity"));
        if (mode is "hang" or "tree") { await Task.Delay(TimeSpan.FromMinutes(1)); return 0; }
        if (mode is "output" or "nonzero")
        {
            // Much larger than either Windows or Linux anonymous pipe capacity; no newlines.
            await Task.WhenAll(Console.Out.WriteAsync(new string('o', 1024 * 1024)),
                Console.Error.WriteAsync(new string('e', 1024 * 1024)));
            return mode == "nonzero" ? 37 : 0;
        }
        await Console.Out.WriteAsync("321\n999\n");
        if (mode == "stderr") await Console.Error.WriteAsync(new string('e', 1024 * 1024));
        if (mode == "large-gpu") await Console.Out.WriteAsync(new string('x', 2 * 1024 * 1024));
        return mode == "bad-gpu" ? 9 : 0;
    }

    private static ProcessStartInfo SelfStart(string mode, string directory)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No current executable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add(ChildSwitch);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(directory);
        return start;
    }

    private static async Task WaitForFileAsync(string path)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (watch.Elapsed > Guard) throw new TimeoutException("Fixture did not reach marker: " + path);
            await Task.Delay(20);
        }
    }

    private sealed class Child : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "process-stream-" + Guid.NewGuid().ToString("N"));
        private Task<int>? _run;
        public Task<int> Track(Task<int> run) => _run = run;
        public ProcessStartInfo Start { get; }
        public string Marker => Path.Combine(_directory, "identity");
        public Child(string mode)
        {
            Directory.CreateDirectory(_directory);
            Start = SelfStart(mode, _directory);
            Start.RedirectStandardOutput = Start.RedirectStandardError = true;
        }
        public Task ReadyAsync() => WaitForFileAsync(Marker);

        private IEnumerable<Process> LiveOwnedProcesses()
        {
            foreach (var marker in Directory.EnumerateFiles(_directory, "identity", SearchOption.AllDirectories))
            {
                var identity = File.ReadAllLines(marker);
                if (identity.Length != 3) throw new IOException("Incomplete fixture identity: " + marker);
                Process process;
                try { process = Process.GetProcessById(int.Parse(identity[0])); }
                catch (ArgumentException) { continue; }
                bool matches;
                try
                {
                    matches = !process.HasExited && process.Id != Environment.ProcessId &&
                        process.StartTime.ToUniversalTime().Ticks == long.Parse(identity[1]) &&
                        string.Equals(process.MainModule!.FileName, identity[2], StringComparison.OrdinalIgnoreCase);
                }
                catch (InvalidOperationException) { matches = false; }
                if (matches) yield return process;
                else process.Dispose();
            }
        }

        public async Task AssertExitedAsync()
        {
            await ReadyAsync();
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var live = LiveOwnedProcesses().ToArray();
                foreach (var process in live) process.Dispose();
                if (live.Length == 0) return;
                if (watch.Elapsed > TimeSpan.FromSeconds(2)) throw new InvalidOperationException("fixture process tree still alive after operation returned");
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            // A failing assertion must not leave fake processes behind. Only captured, complete
            // PID/start-time/executable identities from this fixture's private directory are killed.
            foreach (var process in LiveOwnedProcesses().ToArray())
            {
                using (process)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    await process.WaitForExitAsync().WaitAsync(Guard);
                }
            }
            if (_run != null)
            {
                // Observe completion even when a guard/assertion failed before the caller awaited
                // it. A cleanup timeout remains a failure, not an ignored background operation.
                try { await _run.WaitAsync(Guard); }
                catch (Exception ex) when (ex is not TimeoutException) { }
            }
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class HttpFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private readonly string _body;
        private readonly bool _hang, _stallModels;
        public int Port { get; }
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpFixture(string body, bool hang = false, bool stallModels = false)
        {
            _body = body; _hang = hang; _stallModels = stallModels;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                for (var request = 0; request < 2; request++)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync(_stop.Token);
                    var length = 0;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                    // TestPrompt/payload are ASCII. Fully consume the POST before responding.
                    var buffer = new char[1024];
                    while (length > 0)
                    {
                        var count = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(length, buffer.Length)), _stop.Token);
                        if (count == 0) throw new IOException("Incomplete fixture request");
                        length -= count;
                    }
                    var models = requestLine?.StartsWith("GET /v1/models ", StringComparison.Ordinal) == true;
                    Check(models || requestLine?.StartsWith("POST /v1/chat/completions ", StringComparison.Ordinal) == true,
                        "Unexpected fixture HTTP route: " + requestLine);
                    var stall = models ? _stallModels : _hang;
                    var body = models ? (_stallModels ? "{\"data\":" : "{\"data\":[{\"id\":\"fixture-model\"}]}") : _body;
                    var bytes = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: " +
                        (models ? "application/json" : "text/event-stream") + "\r\n\r\n" + body);
                    await stream.WriteAsync(bytes, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                    if (stall)
                    {
                        Stalled.TrySetResult();
                        // Observe actual client disposal/cancel as EOF or a connection reset.
                        try
                        {
                            var one = new byte[1];
                            Check(await stream.ReadAsync(one, _stop.Token) == 0, "Unexpected data after HTTP request");
                        }
                        catch (IOException) { }
                        Disconnected.TrySetResult();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _serve.WaitAsync(Guard); }
            finally { _stop.Dispose(); }
        }
    }
}
