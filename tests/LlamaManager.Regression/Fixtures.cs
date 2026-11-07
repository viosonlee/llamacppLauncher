using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LlamaManager.Services;

internal static class Fixtures
{
    /// <summary>Counts stream reads so a test can prove a parser works in bulk.</summary>
    public sealed class CountingStream(Stream inner) : Stream
    {
        public long ReadCalls { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { ReadCalls++; return inner.Read(buffer, offset, count); }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static async Task AssertListenerReleasedAsync(int port, TimeSpan timeout, string operation)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var released = false;
        SocketError? lastError = null;
        while (watch.Elapsed < timeout)
        {
            using var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            try { listener.Start(); released = true; break; }
            catch (SocketException ex) { lastError = ex.SocketErrorCode; }
            finally { listener.Stop(); }
            await Task.Delay(50);
        }
        Program.Check(released, $"{operation}: port {port} could not be exclusively bound within {timeout.TotalSeconds}s ({lastError})");
        Program.Check(!await ServerService.TestApiReadyAsync(port), $"{operation}: port {port} still responds as an HTTP API");
        if (ServerService.TestPortInUse(port))
            Console.WriteLine($"TCP snapshot stale after {operation}: port={port}; exclusive loopback bind succeeded, HTTP offline.");
    }

    public static byte[] Gguf(bool prism = false, uint type = 0, bool moe = true, int layers = 64, int context = 8192, bool mtp = false, int vocabSize = 2)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        void String(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
        void Number(string key, uint value) { String(key); writer.Write(4u); writer.Write(value); }
        writer.Write(0x46554747u); writer.Write(3u); writer.Write(1UL); writer.Write((ulong)((prism ? 7 : 6) + (mtp ? 1 : 0)));
        String("general.architecture"); writer.Write(8u); String("qwen3");
        // Architecture fields deliberately follow tokenizer metadata: KV ordering is not specified.
        String("tokenizer.ggml.tokens"); writer.Write(9u); writer.Write(8u); writer.Write((ulong)vocabSize);
        for (var i = 0; i < vocabSize; i++) String($"tok-{i:D6}-xxxxxxxxxxxxxxxx");
        Number("qwen3.block_count", (uint)layers);
        Number("qwen3.context_length", (uint)context);
        Number("qwen3.expert_count", moe ? 8u : 0u);
        Number("qwen3.attention.head_count_kv", 8);
        if (prism) { String("prism.hadamard.block_size"); writer.Write(4u); writer.Write(128u); }
        if (mtp) Number("qwen3.nextn_predict_layers", 1);
        String("blk.0.weight"); writer.Write(2u); writer.Write(256UL); writer.Write(256UL); writer.Write(type); writer.Write(0UL);
        return stream.ToArray(); // No weight data: the reader must stop at tensor descriptors.
    }

    /// <summary>Builds a minimal qwen35moe GGUF exercising the Strata compatibility analyzer.</summary>
    public static byte[] StrataGguf(uint gateType = 11, uint? upType = null, uint downType = 11,
        uint sharedType = 13, uint embedType = 11, uint headType = 14, bool mtp = false,
        uint experts = 256, uint layers = 40, uint hidden = 2048, string arch = "qwen35moe",
        IEnumerable<KeyValuePair<string, uint>>? tensors = null, bool includeGeometry = true,
        (uint Count, uint Number, uint Tensors)? split = null, bool prism = false)
    {
        upType ??= gateType;
        var descriptors = new List<(string Name, uint Type)>();
        if (tensors != null) descriptors.AddRange(tensors.Select(p => (p.Key, p.Value)));
        else
        {
            descriptors.Add(("token_embd.weight", embedType));
            descriptors.Add(("output.weight", headType));
            for (var i = 0; i < layers + (mtp ? 1 : 0); i++)
            {
                descriptors.Add(($"blk.{i}.ffn_gate_exps.weight", gateType));
                descriptors.Add(($"blk.{i}.ffn_up_exps.weight", upType.Value));
                descriptors.Add(($"blk.{i}.ffn_down_exps.weight", downType));
                foreach (var role in new[] { "gate", "up", "down" })
                    descriptors.Add(($"blk.{i}.ffn_{role}_shexp.weight", sharedType));
                descriptors.Add(($"blk.{i}.ffn_gate_inp.weight", 0));
                descriptors.Add(($"blk.{i}.ffn_gate_inp_shexp.weight", 0));
            }
            if (mtp)
                foreach (var role in new[] { "eh_proj", "enorm", "hnorm", "shared_head_norm" })
                    descriptors.Add(($"blk.{layers}.nextn.{role}.weight", 30));
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        void String(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
        void Number(string key, uint value) { String(key); writer.Write(4u); writer.Write(value); }
        void Tensor(string name, uint type)
        {
            ulong[] shape = name.EndsWith("_exps.weight")
                ? name.Contains("ffn_down") ? [512, hidden, experts] : [hidden, 512, experts]
                : name.EndsWith("_shexp.weight")
                    ? name.Contains("gate_inp") ? [hidden] : name.Contains("ffn_down") ? [512, hidden] : [hidden, 512]
                    : name.EndsWith("ffn_gate_inp.weight") ? [hidden, experts]
                    : name is "token_embd.weight" or "output.weight" ? [hidden, 248320]
                    : name.EndsWith("nextn.eh_proj.weight") ? [2UL * hidden, hidden] : [hidden];
            String(name); writer.Write((uint)shape.Length);
            foreach (var dimension in shape) writer.Write(dimension);
            writer.Write(type); writer.Write(0UL);
        }
        var kvCount = (arch.Length > 0 ? 1UL : 0UL) + (includeGeometry ? 7UL + (mtp ? 1UL : 0UL) : 0UL) +
            (split.HasValue ? 3UL : 0UL) + (prism ? 1UL : 0UL);
        writer.Write(0x46554747u); writer.Write(3u); writer.Write((ulong)descriptors.Count); writer.Write(kvCount);
        if (arch.Length > 0) { String("general.architecture"); writer.Write(8u); String(arch); }
        if (includeGeometry)
        {
            Number(arch + ".block_count", layers + (mtp ? 1u : 0u));
            Number(arch + ".context_length", 4096);
            Number(arch + ".embedding_length", hidden);
            Number(arch + ".attention.head_count", 16);
            Number(arch + ".attention.head_count_kv", 2);
            Number(arch + ".expert_count", experts);
            Number(arch + ".expert_used_count", 8);
            if (mtp) Number(arch + ".nextn_predict_layers", 1);
        }
        if (split is { } parts)
        {
            Number("split.count", parts.Count); Number("split.no", parts.Number); Number("split.tensors.count", parts.Tensors);
        }
        if (prism) Number("prism.hadamard.block_size", 128);
        foreach (var (name, type) in descriptors) Tensor(name, type);
        return stream.ToArray();
    }

    public static void CopyRuntime(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(AppContext.BaseDirectory, file);
            var destination = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
        File.Copy(Environment.ProcessPath!, Path.Combine(directory, "llama-server.exe"), true);
    }

    public static void Cleanup(string root)
    {
        var directory = Path.GetFullPath(root);
        var expectedParent = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "logs"));
        var name = Path.GetFileName(directory);
        if (!PathIdentity.Equals(Path.GetDirectoryName(directory), expectedParent) ||
            !(name.StartsWith("regression-") || name.StartsWith("package-smoke-")) ||
            !Guid.TryParseExact(name[(name.LastIndexOf('-') + 1)..], "N", out _))
            throw new InvalidOperationException("Refusing to clean an unexpected fixture path.");
        Directory.Delete(directory, recursive: true);
    }

    public static async Task RunFakeServerAsync(string[] args)
    {
        var parsed = new LauncherArguments(string.Join(" ", args.Select(LauncherArguments.Quote)));
        var port = parsed.GetPort();
        if (parsed.Has("--fake-exit")) { Console.Error.WriteLine("fixture early exit"); return; }
        var delay = int.TryParse(parsed.Get("--fake-delay"), out var milliseconds) ? milliseconds : 0;
        if (delay > 0) await Task.Delay(delay);
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.Error.WriteLine("load_tensors: offloaded 54/65 layers to GPU");
        Console.Error.WriteLine("load_tensors: CUDA0 model buffer size = 4458.01 MiB");
        Console.Error.WriteLine("load_tensors: CPU_Mapped model buffer size = 1149.43 MiB");
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            var request = await reader.ReadLineAsync() ?? "";
            int bodyLength = 0;
            for (string? line; !string.IsNullOrEmpty(line = await reader.ReadLineAsync());)
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) bodyLength = int.Parse(line.Split(':')[1]);
            if (bodyLength > 0)
            {
                var body = new char[bodyLength];
                await reader.ReadBlockAsync(body, 0, body.Length);
            }
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
            if (request.Contains("/v1/chat/completions"))
            {
                await writer.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n");
                await Task.Delay(60);
                await writer.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"one two three\"}}]}\n\n");
                await Task.Delay(60);
                await writer.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"four five six\"}}]}\n\n");
                if (!parsed.Has("--fake-no-usage"))
                {
                    var timings = parsed.Has("--fake-timings")
                        ? ",\"timings\":{\"predicted_per_second\":42.5,\"prompt_per_second\":210,\"draft_n\":100,\"draft_n_accepted\":78}"
                        : "";
                    await writer.WriteAsync($"data: {{\"choices\":[],\"usage\":{{\"prompt_tokens\":20,\"completion_tokens\":6,\"total_tokens\":26}}{timings}}}\n\n");
                }
                await writer.WriteAsync("data: [DONE]\n\n");
            }
            else
            {
                if (request.Contains("/v1/models")) await Task.Delay(450);
                var body = request.Contains("/v1/models") ? "{\"data\":[{\"id\":\"fixture\"}]}" :
                    request.Contains("/slots") ? "[{\"is_processing\":false}]" : "{\"status\":\"ok\"}";
                await writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
            }
        }
    }
}
