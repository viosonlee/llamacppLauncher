using System.IO;
using System.Text.Json;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class LaunchEnvironmentRegression
{
    public static async Task RunAsync(string root, string model)
    {
        var store = new ConfigStore(Path.Combine(root, "llama-manager.config.json"));
        var previous = store.Read();
        var key = "LLAMA_FIXTURE_OVERRIDE_" + Guid.NewGuid().ToString("N");
        var inheritedKey = "LLAMA_FIXTURE_INHERITED_" + Guid.NewGuid().ToString("N");
        var emptyKey = "LLAMA_FIXTURE_EMPTY_" + Guid.NewGuid().ToString("N");
        var report = Path.Combine(root, "launch-environment.json");
        var port = Fixtures.FreePort();
        var config = new ManagerConfig { ModelPath = model };
        var manifest = new PluginManifest
        {
            Schema = 1, Id = "environment-fixture", DisplayName = "Environment fixture", GpuHeavy = false,
            Executable = new() { ["windows"] = ConfigService.GetLlamaServerPath(model) },
            Launch = new()
            {
                Arguments = ["-m", "{model}", "--port", "{port}", "--fake-env-report", report,
                    "--fake-env-keys", string.Join(",", key, inheritedKey, emptyKey)],
                Environment = new() { [key.ToLowerInvariant()] = "child-中文=✓", [emptyKey] = "" }
            },
            Parameters = [new() { Key = "port", Type = "Integer", Default = port.ToString() }],
            Ready = new() { Type = "http", TimeoutSeconds = 5 }
        };
        EngineSession? created = null;
        var plugin = new ProcessPluginBackend(manifest, root)
        {
            BeforePublishAsync = (s, _) => { created = s; return Task.CompletedTask; }
        }.Initialize();
        var quiet = new DelegateLogSink(_ => { });
        try
        {
            Environment.SetEnvironmentVariable(key, "parent");
            Environment.SetEnvironmentVariable(inheritedKey, "preserved");
            Environment.SetEnvironmentVariable(emptyKey, "not-empty");
            store.Save(config);
            await plugin.StartAsync(new EngineStartRequest { Config = config }, quiet, CancellationToken.None);
            using var result = JsonDocument.Parse(File.ReadAllText(report));
            Equal("child-中文=✓", result.RootElement.GetProperty(key).GetString());
            Equal("preserved", result.RootElement.GetProperty(inheritedKey).GetString());
            Check(string.IsNullOrEmpty(result.RootElement.GetProperty(emptyKey).GetString()), "empty override inherited parent value");
            Equal("parent", Environment.GetEnvironmentVariable(key));
            Equal("not-empty", Environment.GetEnvironmentVariable(emptyKey));
            // Invalid blocks must be rejected before logs are truncated or a child is spawned.
            var guard = Path.Combine(root, "environment-guard.log");
            File.WriteAllText(guard, "preserve");
            foreach (var invalid in new[] { new Dictionary<string, string> { ["bad=key"] = "x" },
                new Dictionary<string, string> { ["bad"] = "x\0injected=y" } })
                Throws<ArgumentException>(() => DetachedProcess.Start("missing-fixture.exe", "", guard, guard, root, invalid));
            Equal("preserve", File.ReadAllText(guard));
        }
        finally
        {
            if (created != null) await plugin.StopAsync(created, quiet);
            Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable(inheritedKey, null);
            Environment.SetEnvironmentVariable(emptyKey, null);
            store.Save(previous);
        }
    }
}
