using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;

internal static class BuiltInSmoke
{
    // Opt-in only: use actual installed paths, but never use the real installation's session slots.
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("--builtin NEW_ISOLATED_ROOT BACKEND MODEL");
        var id = args[2];
        if (id is not ("llamacpp" or "polystrata")) throw new ArgumentException("This smoke checks llamacpp/polystrata; use the full KVMem smoke for paging.");
        var root = Path.GetFullPath(args[1]);
        var logs = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "logs"));
        if (!root.StartsWith(logs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root))
            throw new IOException("Use a new, non-existing isolated directory under this workspace's logs.");
        foreach (var name in new[] { "llama-server", "strata-poly", "ninfer-kvmem-server" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) throw new IOException("A model engine is already running; refusing GPU smoke."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        var actualConfig = new ConfigStore(Path.Combine(Directory.GetCurrentDirectory(), "llama-manager.config.json")).Read();
        var settings = actualConfig.Engines.GetValueOrDefault(id) ?? throw new IOException("No installed engine settings for " + id);
        // Deserialized settings are separate objects and are never saved back to actualConfig's path.
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("LLAMA_MANAGER_ROOT", root);
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var model = Path.GetFullPath(args[3]);
        var config = new ManagerConfig { ModelPath = model, Port = port,
            Args = $"--host 127.0.0.1 --port {port} -c 4096 -ngl 999 -t 8 -b 256 -ub 128 --jinja" };
        settings.ModelPath = model;
        if (id == "polystrata")
        {
            settings.Parameters["context"] = "4096";
            settings.Parameters["servePort"] = port.ToString();
            settings.Parameters["host"] = "127.0.0.1";
            settings.Parameters["vramReserveMib"] = "512";
            settings.Parameters["kv"] = "q8_0";
            settings.Parameters["threads"] = "8";
            settings.Parameters["tailSkip"] = "0";
            settings.Parameters["cacheBias"] = "0";
            settings.Parameters["conversationCache"] = "";
            settings.Parameters["mmproj"] = Path.Combine(root, "text-only-no-projection.gguf");
            settings.Parameters["draftModel"] = Path.Combine(root, "ordinary-decode-no-external-draft.gguf");
            settings.Parameters.Remove("kvmem");
        }
        config.Engines[id] = settings;
        ConfigService.SaveConfig(config);
        ConfigService.SaveEngineSettings(config, id, settings);
        var backend = BackendRegistry.ServerById(id)!;
        EngineSession? session = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var log = new DelegateLogSink(Console.WriteLine);
        try
        {
            session = await backend.StartAsync(new EngineStartRequest { Config = config, Port = port }, log, deadline.Token);
            if (!await backend.ReadyAsync(session, deadline.Token)) throw new IOException("Installed engine did not become ready.");
            var url = $"http://127.0.0.1:{port}";
            await File.WriteAllTextAsync(Path.Combine(root, "props.json"), await http.GetStringAsync(url + "/props", deadline.Token));
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), await http.GetStringAsync(url + "/v1/models", deadline.Token));
            using var response = await http.PostAsJsonAsync(url + "/v1/chat/completions", new
            {
                model = Path.GetFileNameWithoutExtension(model).ToLowerInvariant(),
                messages = new[] { new { role = "user", content = "Reply only with OK." } },
                max_tokens = 32, temperature = 0, reasoning_effort = "none"
            }, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "response.json"), body);
            response.EnsureSuccessStatusCode();
            var result = JsonNode.Parse(body)!;
            var answer = result["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(answer)) throw new IOException("Installed engine produced no answer.");
            var gpu = await ProcessProbe.RunAsync("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits", TimeSpan.FromSeconds(5), deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "gpu-used-mib.txt"), gpu);
            Console.WriteLine($"PASS {id}: HTTP 200, answer={answer}, usage={result["usage"]}, GPU used MiB={gpu.Trim()}");
            return 0;
        }
        finally
        {
            if (session != null) await backend.StopAsync(session, log);
            Console.WriteLine("Evidence root: " + root);
        }
    }
}
