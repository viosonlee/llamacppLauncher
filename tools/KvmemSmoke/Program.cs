using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;

// Explicit, opt-in GPU integration check. This never runs in the ordinary regression suite.
// dotnet run --project tools/KvmemSmoke -c Release -- ROOT BUNDLE MODEL REQUESTS_JSON
if (args.FirstOrDefault() == "--acquire")
{
    if (args.Length != 2 || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LLAMA_MANAGER_ROOT")))
        throw new ArgumentException("Explicit acquisition smoke requires --acquire BACKEND and LLAMA_MANAGER_ROOT.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
    Console.WriteLine("Explicit acquisition check: installs a verified new managed revision; never overwrites the old directory.");
    Console.WriteLine(await EngineAcquisitionService.AcquireAsync(args[1], new DelegateLogSink(Console.WriteLine), timeout.Token));
    return 0;
}
if (args.FirstOrDefault() == "--builtin") return await BuiltInSmoke.RunAsync(args);
if (args.Length != 4) throw new ArgumentException("Usage: KvmemSmoke ROOT BUNDLE MODEL REQUESTS_JSON (isolated ROOT under logs)");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new IOException("Refusing to reuse a smoke root or its session/configuration.");
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("LLAMA_MANAGER_ROOT", root);
var bundle = Path.GetFullPath(args[1]);
var model = Path.GetFullPath(args[2]);
var requests = JsonNode.Parse(await File.ReadAllTextAsync(args[3]))!.AsArray();
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var config = new ManagerConfig();
config.Engines["kvmem"] = new EngineSettings
{
    ModelPath = model,
    Parameters = new()
    {
        ["packageRoot"] = bundle, ["servePort"] = port.ToString(), ["host"] = "127.0.0.1",
        // 131072 is the logical limit; the exercised prompts are actual 33K/65K, not 131K.
        ["context"] = "131072", ["kvmem"] = "1", ["kvmemBudget"] = "8192", ["kvmemGenReserve"] = "512",
        ["kvmemHostMib"] = "256", ["kvmemDiskMib"] = "8192", ["kvmemDiskPath"] = Path.Combine(root, "ssd-cache"),
        ["kvmemSessions"] = "2", ["kv"] = "q8_0", ["vramReserveMib"] = "512", ["promptBatch"] = "128",
        ["stageMib"] = "128", ["threads"] = "8", ["tensorCoreMma"] = "0", ["tailSkip"] = "0", ["cacheBias"] = "0"
    }
};
ConfigService.SaveEngineSettings(config, "kvmem", config.Engines["kvmem"]);
var backend = BackendRegistry.ServerById("kvmem")!;
var log = new DelegateLogSink(Console.WriteLine);
EngineSession? session = null;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(40));
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(25) };
using var telemetryStop = new CancellationTokenSource();
var telemetry = Task.Run(async () =>
{
    try
    {
        while (!telemetryStop.IsCancellationRequested)
        {
            try
            {
                var memory = await ProcessProbe.RunAsync("nvidia-smi", "--query-gpu=memory.used,memory.free --format=csv,noheader,nounits", TimeSpan.FromSeconds(5), telemetryStop.Token);
                await File.AppendAllTextAsync(Path.Combine(root, "gpu-memory.csv"), $"{DateTimeOffset.UtcNow:o},{memory.Trim()}\n", telemetryStop.Token);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { Console.WriteLine("Telemetry: " + ex.Message); }
            await Task.Delay(1500, telemetryStop.Token);
        }
    }
    catch (OperationCanceledException) { }
});
var failed = false;
try
{
    session = await backend.StartAsync(new EngineStartRequest { Config = config, Port = port }, log, deadline.Token);
    await File.WriteAllTextAsync(Path.Combine(root, "session.json"), JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
    var baseUrl = $"http://127.0.0.1:{port}";
    await File.WriteAllTextAsync(Path.Combine(root, "initial-props.json"), await http.GetStringAsync(baseUrl + "/props", deadline.Token));
    // The launcher's browser button must only open a chat page the bundle actually ships.
    var hasWebUi = backend is StrataBackend strata && strata.HasWebUi(config);
    await File.WriteAllTextAsync(Path.Combine(root, "web-ui.json"),
        JsonSerializer.Serialize(new { packageDeclaresUi = hasWebUi }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"package declares web UI: {hasWebUi}");
    try
    {
        using var web = await http.GetAsync(baseUrl + "/", deadline.Token);
        var webType = web.Content.Headers.ContentType?.MediaType ?? "";
        var body = await web.Content.ReadAsStringAsync(deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(root, "web-ui.html"), body);
        Console.WriteLine($"web UI: HTTP {(int)web.StatusCode}, {webType}, {body.Length} chars");
        var html = web.IsSuccessStatusCode && webType == "text/html" && body.Contains("<html", StringComparison.OrdinalIgnoreCase);
        if (hasWebUi != html)
        {
            failed = true;
            Console.WriteLine(hasWebUi
                ? "FAIL: the bundle ships serve/web/index.html but the served root is not an HTML chat page."
                : "FAIL: the bundle has no chat UI, yet the served root returned a page.");
        }
        else Console.WriteLine(html ? "root serves the chat UI." : "root cannot serve a chat page; the launcher must not open it.");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine("root request failed (expected when the bundle ships no chat page): " + ex.Message);
        if (hasWebUi) { failed = true; Console.WriteLine("FAIL: a bundle with a chat page dropped the root request."); }
    }
    using (var props = await http.GetAsync(baseUrl + "/props", deadline.Token))
    {
        var propsType = props.Content.Headers.ContentType?.MediaType ?? "";
        Console.WriteLine($"/props: {propsType}");
        if (!propsType.Contains("json", StringComparison.OrdinalIgnoreCase)) { failed = true; Console.WriteLine("FAIL: /props is no longer JSON; re-check the UI contract."); }
    }
    foreach (var request in requests)
    {
        var label = request!["label"]!.GetValue<string>();
        Console.WriteLine($"=== {label} ===");
        var watch = Stopwatch.StartNew();
        using var response = await http.PostAsJsonAsync(baseUrl + "/v1/chat/completions", request["body"], deadline.Token);
        var text = await response.Content.ReadAsStringAsync(deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(root, label + "-response.json"), text);
        Console.WriteLine($"HTTP {(int)response.StatusCode}, {watch.Elapsed.TotalSeconds:0.0}s");
        if (!response.IsSuccessStatusCode) { failed = true; Console.WriteLine(text); break; }
        var result = JsonNode.Parse(text)!;
        var content = result["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        var promptTokens = result["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
        var minTokens = request["minPromptTokens"]?.GetValue<int>() ?? 0;
        var expected = request["expected"]?.GetValue<string>() ?? "";
        Console.WriteLine($"Prompt tokens: {promptTokens}; usage: {result["usage"]}; answer: {content}");
        if (string.IsNullOrWhiteSpace(content) || promptTokens < minTokens || (expected.Length > 0 && !content.Contains(expected, StringComparison.Ordinal)))
        {
            failed = true;
            Console.WriteLine("FAIL: empty answer, insufficient actual prompt tokens, or retrieval answer mismatch.");
        }
        await File.WriteAllTextAsync(Path.Combine(root, label + "-props.json"), await http.GetStringAsync(baseUrl + "/props", deadline.Token));
        try { await File.WriteAllTextAsync(Path.Combine(root, label + "-snapshot.json"), await http.GetStringAsync(baseUrl + "/api/monitor/snapshot", deadline.Token)); }
        catch (HttpRequestException ex) { Console.WriteLine("Snapshot unavailable: " + ex.Message); }
    }
}
finally
{
    if (session != null) await backend.StopAsync(session, log);
    telemetryStop.Cancel();
    await telemetry;
    if (session != null)
    {
        using var live = ServerService.ValidateProcess(new ServerSession { ProcessId = session.ProcessId, StartTimeUtcTicks = session.StartTimeUtcTicks, EnginePath = session.ExecutablePath });
        if (live != null) throw new IOException("Smoke process survived identity-validated stop.");
    }
    Console.WriteLine("Evidence root: " + root);
}
return failed ? 1 : 0;
