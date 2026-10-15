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
                    await writer.WriteAsync("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":6,\"total_tokens\":26}}\n\n");
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
