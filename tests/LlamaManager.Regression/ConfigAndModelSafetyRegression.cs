using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using LlamaManager.Services;
using static Program;

internal static class ConfigAndModelSafetyRegression
{
    public static void RunConfigRecovery()
    {
        var work = Path.Combine(Path.GetTempPath(), "llama-config-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string[] invalid =
            [
                "null", "{bad", """{"Engines":null}""", """{"Engines":{"plugin":null}}""",
                """{"Strata":null}""", """{"ModelPresets":{"model":null}}""",
                """{"ModelPresets":{"model":{"Args":null}}}""",
                """{"Engines":{"plugin":{"ModelPresets":{"model":null}}}}""",
                """{"Engines":{"plugin":{"Parameters":{"flag":null}}}}""",
                """{"Engines":{"plugin":{"ModelPresets":{"model":{"flag":null}}}}}""",
                """{"Engines":{"plugin":{"ModelPath":"bad\u0000path","Parameters":{"flag":"on"}}}}""",
                """{"Running":{"main":null}}""", """{"Args":null}""",
                """{"Port":0}""", """{"EmbeddingPort":65536}"""
            ];
            const string backup = """{"ModelPath":"backup.gguf","Window":{"future":[1,null,"保留"]},"Future":null}""";
            for (var i = 0; i < invalid.Length; i++)
            {
                var path = Path.Combine(work, $"case-{i}.json");
                File.WriteAllText(path, invalid[i]);
                File.WriteAllText(path + ".bak", backup);
                var store = new ConfigStore(path);
                Equal("backup.gguf", store.Read().ModelPath);
                Check(store.RecoveryWarning != null, "missing recovery warning for " + invalid[i]);
                Equal(invalid[i], File.ReadAllText(path)); // Reading recovery must not erase evidence.
                store.Update(c => c.Args = "--threads 2");
                Equal(backup, File.ReadAllText(path + ".bak"));
                var evidence = Directory.GetFiles(work, $"case-{i}.json.*.corrupt");
                Equal(1, evidence.Length);
                Equal(invalid[i], File.ReadAllText(evidence[0]));
                var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                Check(JsonNode.DeepEquals(JsonNode.Parse(backup)!["Window"], saved["Window"]), "unknown metadata lost");
                Check(saved.ContainsKey("Future") && saved["Future"] == null, "unknown null metadata lost");
                Equal("--threads 2", store.Read().Args);
                Check(store.RecoveryWarning == null, "warning survived a healthy read");

                // No valid backup must be InvalidDataException, never Normalize's NRE.
                File.WriteAllText(path, invalid[i]);
                File.WriteAllText(path + ".bak", """{"Engines":{"bad":null}}""");
                Throws<InvalidDataException>(() => store.Read());
                Equal(invalid[i], File.ReadAllText(path));
                File.Delete(path + ".bak");
                Throws<InvalidDataException>(() => store.Read());
            }

            var legacy = Path.Combine(work, "legacy.json");
            // Existing nullable legacy collections are repaired, and omitted fields retain defaults.
            File.WriteAllText(legacy, """{"ModelPresets":null,"Running":null,"Engines":{"plugin":{"Parameters":null,"ModelPresets":null}}}""");
            var old = new ConfigStore(legacy).Read();
            Equal(0, old.ModelPresets.Count);
            Equal(0, old.Running.Count);
            Equal(0, old.Engines["plugin"].Parameters.Count);
            Equal(0, old.Engines["plugin"].ModelPresets.Count);
            File.WriteAllText(legacy, "{}");
            Equal(8080, new ConfigStore(legacy).Read().Port);

            // Real Windows exclusive locks are operational IO failures, not corruption.
            var locked = Path.Combine(work, "locked.json");
            File.WriteAllText(locked, "{}");
            File.WriteAllText(locked + ".bak", backup);
            var lockedStore = new ConfigStore(locked);
            using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Throws<IOException>(() => lockedStore.Read());
            Check(lockedStore.RecoveryWarning == null, "primary IO failure recovered as corruption");
            File.WriteAllText(locked, """{"Engines":null}""");
            using (var handle = new FileStream(locked + ".bak", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Throws<IOException>(() => lockedStore.Read());
            Check(lockedStore.RecoveryWarning == null, "backup IO failure recovered as corruption");
            Equal(backup, File.ReadAllText(locked + ".bak"));
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    public static async Task RunModelDownloadSafetyAsync()
    {
        var work = Path.Combine(Path.GetTempPath(), "llama-model-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var transfers = new List<Task>();
        try
        {
            var good = GeneratedGguf(out var dimensions, out var tableEnd);
            var inspect = Path.Combine(work, "inspect.gguf");
            File.WriteAllBytes(inspect, good);
            ModelDownloadService.VerifyGguf(inspect);
            var v2 = (byte[])good.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(v2.AsSpan(4), 2);
            File.WriteAllBytes(inspect, v2);
            ModelDownloadService.VerifyGguf(inspect);

            var version = (byte[])good.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(version.AsSpan(4), 99);
            var counts = (byte[])good.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(counts.AsSpan(8), ulong.MaxValue);
            var badDimensions = (byte[])good.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(badDimensions.AsSpan(dimensions), 5);
            var zeroDimension = (byte[])good.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(zeroDimension.AsSpan(dimensions + 4), 0);
            var stringLength = (byte[])good.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(stringLength.AsSpan(24), ulong.MaxValue);
            foreach (var invalid in new[] { "GGUF"u8.ToArray(), good[..23], good[..40], good[..(tableEnd - 1)],
                         version, counts, badDimensions, zeroDimension, stringLength })
            {
                File.WriteAllBytes(inspect, invalid);
                Throws<InvalidDataException>(() => ModelDownloadService.VerifyGguf(inspect));
                Check(File.ReadAllBytes(inspect).SequenceEqual(invalid), "inspection deleted/changed evidence");
            }
            // Metadata count/type, array length and nesting are bounded by the shared parser.
            foreach (var malformed in new[] { MalformedMetadata(0), MalformedMetadata(1), MalformedMetadata(2) })
            {
                File.WriteAllBytes(inspect, malformed);
                Throws<InvalidDataException>(() => ModelDownloadService.VerifyGguf(inspect));
            }
            // An oversized declared string must fail before reading/allocating its contents.
            using (var stream = File.Create(inspect))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46554747u); writer.Write(3u); writer.Write(1UL); writer.Write(1UL);
                WriteString(writer, "large"); writer.Write(8u); writer.Write(129UL << 20);
                stream.SetLength(130L << 20);
            }
            Throws<InvalidDataException>(() => ModelDownloadService.VerifyGguf(inspect));
            File.Delete(inspect);

            var target = Path.Combine(work, "model.gguf");
            var original = "original file must survive"u8.ToArray();
            Task<ModelDownloadService.Result> Download(PayloadServer server, CancellationToken? token = null) =>
                ModelDownloadService.DownloadCoreAsync(new ModelDownload("unused", "model.gguf", good.Length, 0, "test"),
                    server.Url, target, _ => { }, token ?? deadline.Token);
            void Unchanged(byte[] bytes)
            {
                Check(File.ReadAllBytes(target).SequenceEqual(bytes), "failed/canceled transfer changed target");
                Check(!Directory.GetFiles(work, "*.part").Any(), "owned staging was not cleaned");
            }

            // Same-length HTML and GGUF-header-only payloads must not replace an existing file.
            var html = Enumerable.Repeat((byte)'x', good.Length).ToArray();
            "<html>wrong</html>"u8.CopyTo(html);
            foreach (var payload in new[] { html, version, good[..tableEnd], good[..^4], good.Concat(new byte[1]).ToArray() })
            {
                File.WriteAllBytes(target, original);
                await using var server = new PayloadServer(payload);
                await RejectAsync<InvalidDataException>(() => Download(server));
                Equal(1, server.Requests);
                Unchanged(original);
            }
            // Honest close-delimited HTTP (no Content-Length): catalog length is still mandatory.
            File.WriteAllBytes(target, original);
            await using (var server = new PayloadServer(good[..^4], omitLength: true))
                await RejectAsync<InvalidDataException>(() => Download(server));
            Unchanged(original);
            // Broken HTTP body (advertised full length but connection closes early).
            await using (var server = new PayloadServer(good[..^4], declaredLength: good.Length))
                await RejectAsync<IOException>(() => Download(server));
            Unchanged(original);
            await using (var server = new PayloadServer([], status: 503))
                await RejectAsync<System.Net.Http.HttpRequestException>(() => Download(server));
            Unchanged(original);

            // A cached metadata observation must not bless a later same-size corrupt file.
            File.WriteAllBytes(target, good);
            var modified = File.GetLastWriteTimeUtc(target);
            Check(GgufService.TryRead(target) != null, "generated fixture is unreadable");
            File.WriteAllBytes(target, badDimensions);
            File.SetLastWriteTimeUtc(target, modified);
            await using (var server = new PayloadServer(html))
                await RejectAsync<InvalidDataException>(() => Download(server));
            Unchanged(badDimensions);
            await using (var server = new PayloadServer(good, omitLength: true))
            {
                var result = await Download(server);
                Equal((long)good.Length, result.Bytes);
                Equal(target, result.Path);
                Check(File.ReadAllBytes(target).SequenceEqual(good), "corrupt cache was reused instead of replaced");
                Equal(1, server.Requests);
                await Download(server);
                Equal(1, server.Requests); // Healthy cached file needs no second request.
                using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    await RejectAsync<IOException>(() => Download(server));
                Equal(1, server.Requests); // Operational IO is not permission to replace the file.
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await RejectAsync<OperationCanceledException>(() => Download(server, canceled.Token));
                Equal(1, server.Requests);
            }

            // Cancellation after actual response bytes have arrived must retain the old file.
            File.WriteAllBytes(target, original);
            using (var canceled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            await using (var server = new PayloadServer(good, pause: true))
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var transfer = ModelDownloadService.DownloadCoreAsync(new("unused", "model.gguf", good.Length, 0, "test"),
                    server.Url, target, _ => { }, canceled.Token, (received, _) => started.TrySetResult());
                transfers.Add(transfer);
                try
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(Directory.GetFiles(work, "*.part").Length == 1, "no actual staging file while receiving");
                    canceled.Cancel();
                    await RejectAsync<OperationCanceledException>(() => transfer);
                }
                finally { canceled.Cancel(); server.Release(); }
            }
            Unchanged(original);

            // Cancellation on the final progress callback also precedes publication.
            using (var canceled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            await using (var server = new PayloadServer(good))
                await RejectAsync<OperationCanceledException>(() => ModelDownloadService.DownloadCoreAsync(
                    new("unused", "model.gguf", good.Length, 0, "test"), server.Url, target, _ => { }, canceled.Token,
                    (received, _) => { if (received == good.Length) canceled.Cancel(); }));
            Unchanged(original);

            // Actual overlapping same-target downloads: failed B must not delete A's staging.
            await using (var first = new PayloadServer(good, pause: true))
            await using (var second = new PayloadServer(html))
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var a = ModelDownloadService.DownloadCoreAsync(new("unused", "model.gguf", good.Length, 0, "test"),
                    first.Url, target, _ => { }, deadline.Token, (_, _) => started.TrySetResult());
                transfers.Add(a);
                try
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var ownedByA = Directory.GetFiles(work, "*.part").Single();
                    await RejectAsync<InvalidDataException>(() => Download(second));
                    Check(File.Exists(ownedByA), "failed B removed A's staging");
                    Check(File.ReadAllBytes(target).SequenceEqual(original), "B replaced the original");
                    first.Release();
                    await a;
                    Check(File.ReadAllBytes(target).SequenceEqual(good), "A did not publish the complete payload");
                }
                finally { first.Release(); }
            }
            Check(!Directory.GetFiles(work, "*.part").Any(), "successful publication left staging");
            File.Delete(target);
            await using (var server = new PayloadServer(html))
                await RejectAsync<InvalidDataException>(() => Download(server));
            Check(!File.Exists(target), "invalid payload was published to an absent target");
            Check(!Directory.GetFiles(work, "*.part").Any(), "invalid new download left staging");
        }
        finally
        {
            deadline.Cancel();
            foreach (var transfer in transfers)
                try { await transfer; } catch (Exception) when (transfer.IsCanceled || transfer.IsFaulted) { }
            Directory.Delete(work, recursive: true);
        }
    }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void WriteString(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((ulong)bytes.Length); writer.Write(bytes);
    }

    private static byte[] GeneratedGguf(out int dimensions, out int tableEnd)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x46554747u); writer.Write(3u); writer.Write(1UL); writer.Write(2UL);
        WriteString(writer, "general.architecture"); writer.Write(8u); WriteString(writer, "test");
        WriteString(writer, "tokenizer.ggml.tokens"); writer.Write(9u); writer.Write(8u); writer.Write(2UL);
        WriteString(writer, "one"); WriteString(writer, "two");
        WriteString(writer, "weight"); dimensions = (int)stream.Position;
        writer.Write(1u); writer.Write(16UL); writer.Write(0u); writer.Write(0UL);
        tableEnd = (int)stream.Position;
        while (stream.Position % 32 != 0) writer.Write((byte)0);
        for (var i = 0; i < 16; i++) writer.Write((float)i);
        return stream.ToArray();
    }

    private static byte[] MalformedMetadata(int mode)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46554747u); writer.Write(3u); writer.Write(1UL); writer.Write(1UL);
        WriteString(writer, "bad"); writer.Write(mode == 0 ? 99u : 9u);
        if (mode == 1) { writer.Write(8u); writer.Write(ulong.MaxValue); }
        if (mode == 2)
            for (var i = 0; i < 6; i++) { writer.Write(9u); writer.Write(1UL); }
        return stream.ToArray();
    }

    private sealed class PayloadServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serve;
        private int _requests;
        public string Url { get; }
        public int Requests => Volatile.Read(ref _requests);

        public PayloadServer(byte[] payload, bool omitLength = false, int? declaredLength = null, int status = 200, bool pause = false)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/model.gguf";
            _serve = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                        Interlocked.Increment(ref _requests);
                        var length = omitLength ? "" : $"Content-Length: {declaredLength ?? payload.Length}\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\n{length}Connection: close\r\n\r\n"), _stop.Token);
                        var prefix = pause ? Math.Min(24, payload.Length) : payload.Length;
                        await stream.WriteAsync(payload.AsMemory(0, prefix), _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                        if (pause)
                        {
                            await _release.Task.WaitAsync(_stop.Token);
                            await stream.WriteAsync(payload.AsMemory(prefix), _stop.Token);
                            await stream.FlushAsync(_stop.Token);
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            });
        }

        public void Release() => _release.TrySetResult();
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            await _serve;
            _stop.Dispose();
        }
    }
}
