using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LlamaManager.Services;
using static Program;

internal static class DownloadConcurrencyRegression
{
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string[] Staging(string destination) => Directory.GetFiles(Path.GetDirectoryName(destination)!, Path.GetFileName(destination) + "*.part");

    public static async Task RunOwnershipAsync(string root)
    {
        var directory = Path.Combine(root, "same-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "archive.bin");
        File.WriteAllText(destination, "old verified archive");
        const string payloadA = "AAAA complete verified archive";
        const string payloadB = "BBBB complete verified archive";
        var releaseA = Signal();
        var wroteA = Signal();
        await using var serverA = new PayloadServer(payloadA, releaseA.Task);
        var taskA = EngineDownloadService.DownloadAsync(serverA.Url, destination, Digest(payloadA), _ => { }, CancellationToken.None,
            (_, _) => wroteA.TrySetResult());
        try
        {
            await wroteA.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stagingA = Staging(destination).Single();
            // A is still writing the ACTUAL staging file; B must reach its own checksum failure.
            await using (var badServer = new PayloadServer(payloadB))
            {
                try
                {
                    await EngineDownloadService.DownloadAsync(badServer.Url, destination, new string('0', 64), _ => { }, CancellationToken.None);
                    throw new Exception("same-target bad checksum accepted");
                }
                catch (EngineDownloadService.ChecksumMismatchException) { }
            }
            Check(Staging(destination).SequenceEqual(new[] { stagingA }), "failed task touched the other writer's staging file");
            Equal("old verified archive", File.ReadAllText(destination));
            using (var cancelled = new CancellationTokenSource())
            await using (var cancelServer = new PayloadServer(payloadB))
            {
                try
                {
                    await EngineDownloadService.DownloadAsync(cancelServer.Url, destination, Digest(payloadB), _ => { }, cancelled.Token,
                        (_, _) => cancelled.Cancel());
                    throw new Exception("cancelled same-target transfer committed");
                }
                catch (OperationCanceledException) { }
            }
            Check(Staging(destination).SequenceEqual(new[] { stagingA }), "cancelled task touched the other writer's staging file");
            Equal("old verified archive", File.ReadAllText(destination));
            // Valid B can commit while A is suspended; A later replaces it with complete verified bytes.
            await using (var goodServer = new PayloadServer(payloadB))
                Equal(destination, await EngineDownloadService.DownloadAsync(goodServer.Url, destination, Digest(payloadB), _ => { }, CancellationToken.None));
            Equal(payloadB, File.ReadAllText(destination));
            Check(File.Exists(stagingA), "successful B removed suspended A's staging file");
            releaseA.TrySetResult();
            Equal(destination, await taskA.WaitAsync(TimeSpan.FromSeconds(10)));
            Equal(payloadA, File.ReadAllText(destination));
            Equal(0, Staging(destination).Length);
        }
        finally
        {
            releaseA.TrySetResult();
            try { await taskA.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
    }

    public static async Task RunPublicationPolicyAsync(string root)
    {
        var directory = Path.Combine(root, "publish-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "owned.part");
        var destination = Path.Combine(directory, "archive.bin");
        File.WriteAllText(source, "verified new bytes");
        File.WriteAllText(destination, "old bytes");
        var moves = 0;
        var delays = new List<TimeSpan>();
        await EngineDownloadService.PublishVerifiedAsync(source, destination, CancellationToken.None, (from, to) =>
        {
            if (++moves == 1) throw new UnauthorizedAccessException("transient replacement access denied");
            if (moves == 2) throw new IOException("transient replacement sharing violation", unchecked((int)0x80070020));
            if (moves == 3) throw new IOException("transient replacement lock violation", unchecked((int)0x80070021));
            File.Move(from, to, true);
        }, (wait, _) => { delays.Add(wait); return Task.CompletedTask; });
        Equal(4, moves);
        Check(delays.SequenceEqual(new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150) }), "retry schedule was wrong");
        Equal("verified new bytes", File.ReadAllText(destination));

        File.WriteAllText(source, "next verified bytes");
        var denied = new UnauthorizedAccessException("permanent permission denied");
        moves = 0; delays.Clear();
        try
        {
            await EngineDownloadService.PublishVerifiedAsync(source, destination, CancellationToken.None,
                (_, _) => { moves++; throw denied; }, (wait, _) => { delays.Add(wait); return Task.CompletedTask; });
            throw new Exception("permanent denial was mistaken for success");
        }
        catch (UnauthorizedAccessException ex) { Check(ReferenceEquals(denied, ex), "exhaustion hid the original failure"); }
        Equal(6, moves); Equal(5, delays.Count);
        Equal(TimeSpan.FromMilliseconds(750), TimeSpan.FromTicks(delays.Sum(d => d.Ticks)));
        Equal("verified new bytes", File.ReadAllText(destination));
        Check(File.Exists(source), "publication helper deleted uncommitted source");

        moves = 0;
        try
        {
            await EngineDownloadService.PublishVerifiedAsync(source, destination, CancellationToken.None,
                (_, _) => { moves++; throw new IOException("disk full", unchecked((int)0x80070070)); }, (_, _) => throw new Exception("nonretryable error was retried"));
            throw new Exception("disk full was mistaken for success");
        }
        catch (IOException ex) { Equal(unchecked((int)0x80070070), ex.HResult); }
        Equal(1, moves);
        using var cancelled = new CancellationTokenSource();
        moves = 0;
        try
        {
            await EngineDownloadService.PublishVerifiedAsync(source, destination, cancelled.Token,
                (_, _) => { moves++; throw denied; }, (_, token) => { cancelled.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
            throw new Exception("cancellation while waiting was ignored");
        }
        catch (OperationCanceledException) { }
        Equal(1, moves);
        Equal("verified new bytes", File.ReadAllText(destination));
        // Cancellation before the first move cannot alter the old destination.
        await AssertCancelledBeforeCommit(source, destination, cancelled.Token);

        // Exercise the download's cleanup, not only the publication helper: exhaustion/cancellation
        // after HTTP+hash must remove only this transfer's staging and preserve the previous archive.
        await using (var deniedServer = new PayloadServer("HTTP verified but permanently denied"))
        {
            try
            {
                await EngineDownloadService.DownloadCoreAsync(deniedServer.Url, destination, Digest("HTTP verified but permanently denied"), _ => { }, CancellationToken.None, null,
                    (from, to, token) => EngineDownloadService.PublishVerifiedAsync(from, to, token, (_, _) => throw denied, (_, _) => Task.CompletedTask));
                throw new Exception("denied HTTP publication was accepted");
            }
            catch (UnauthorizedAccessException ex) { Check(ReferenceEquals(denied, ex), "download hid terminal publication error"); }
            Equal(1, deniedServer.Requests);
        }
        Equal(0, Staging(destination).Length);
        Equal("verified new bytes", File.ReadAllText(destination));
        using var waitCancellation = new CancellationTokenSource();
        await using (var cancelServer = new PayloadServer("cancel after hash"))
        {
            try
            {
                await EngineDownloadService.DownloadCoreAsync(cancelServer.Url, destination, Digest("cancel after hash"), _ => { }, waitCancellation.Token, null,
                    (from, to, token) => EngineDownloadService.PublishVerifiedAsync(from, to, token, (_, _) => throw denied,
                        (_, pendingToken) => { waitCancellation.Cancel(); pendingToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
                throw new Exception("HTTP publication ignored cancellation while waiting");
            }
            catch (OperationCanceledException) { }
            Equal(1, cancelServer.Requests);
        }
        Equal(0, Staging(destination).Length);
        Equal("verified new bytes", File.ReadAllText(destination));
    }

    private static async Task AssertCancelledBeforeCommit(string source, string destination, CancellationToken ct)
    {
        try
        {
            await EngineDownloadService.PublishVerifiedAsync(source, destination, ct, (_, _) => throw new Exception("cancelled task attempted to commit"));
            throw new Exception("cancelled publication accepted");
        }
        catch (OperationCanceledException) { }
    }

    public static async Task RunWindowsLockAsync(string root)
    {
        var directory = Path.Combine(root, "native-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "archive.bin");
        File.WriteAllText(destination, "old bytes");
        const string payload = "verified HTTP payload after native Windows lock";
        await using var server = new PayloadServer(payload);
        var retry = Signal();
        var release = Signal();
        var waits = 0;
        using var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read); // denies replacement
        var oneShot = Path.Combine(directory, "one-shot.part");
        File.WriteAllText(oneShot, payload);
        try
        {
            File.Move(oneShot, destination, overwrite: true);
            throw new Exception("Windows fixture did not block atomic replacement");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Observed native one-shot replacement failure: {ex.GetType().Name}, HRESULT=0x{ex.HResult:X8}");
        }
        var download = EngineDownloadService.DownloadCoreAsync(server.Url, destination, Digest(payload), _ => { }, CancellationToken.None, null,
            (from, to, ct) => EngineDownloadService.PublishVerifiedAsync(from, to, ct, delay: async (_, token) =>
            {
                Interlocked.Increment(ref waits);
                retry.TrySetResult();
                await release.Task.WaitAsync(token);
            }));
        try
        {
            await retry.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Equal("old bytes", File.ReadAllText(destination));
            Check(!download.IsCompleted, "native lock failure did not enter retry");
            locked.Dispose();
            release.TrySetResult();
            Equal(destination, await download.WaitAsync(TimeSpan.FromSeconds(10)));
            Equal(payload, File.ReadAllText(destination));
            Equal(1, server.Requests);
            Equal(1, waits);
            // The artificial one-shot fixture is not owned by the download and must survive.
            Check(Staging(destination).Length == 0 && File.Exists(oneShot), "download cleanup escaped its own staging file");
            Console.WriteLine("PASS native Windows download: real HTTP+SHA256+File.Move; locked target preserved; release resumes publication; HTTP requests=1; retries=1; no owned .part remains.");
        }
        finally
        {
            locked.Dispose(); release.TrySetResult();
            try { await download.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
    }

    private sealed class PayloadServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        public int Requests { get; private set; }
        public string Url { get; }
        public PayloadServer(string payload, Task? releaseBody = null)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/payload.bin";
            _serve = ServeAsync(payload, releaseBody);
        }
        private async Task ServeAsync(string payload, Task? releaseBody)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
            Requests++;
            var bytes = Encoding.UTF8.GetBytes(payload);
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, _stop.Token);
            await stream.WriteAsync(bytes.AsMemory(0, 1), _stop.Token);
            await stream.FlushAsync(_stop.Token);
            if (releaseBody != null) await releaseBody.WaitAsync(_stop.Token);
            await stream.WriteAsync(bytes.AsMemory(1), _stop.Token);
            await stream.FlushAsync(_stop.Token);
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            try { await _serve; } catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
            _stop.Dispose();
        }
    }
}
