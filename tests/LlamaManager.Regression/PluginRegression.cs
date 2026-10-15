using System.IO;
using System.Text.Json;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class PluginRegression
{
    /// <summary>
    /// Phase 4: a manifest is parsed, validated and turned into a ProcessPluginBackend that
    /// builds a launch plan by substituting {model}/{port}/parameter keys. No plugin code is loaded.
    /// </summary>
    public static void RunManifest(string root)
    {
        var pluginDir = Path.Combine(root, "plugins", "demo");
        Directory.CreateDirectory(pluginDir);
        var manifest = new
        {
            schema = 1,
            id = "demo",
            displayName = "Demo Engine",
            description = "test",
            gpuHeavy = true,
            executable = new Dictionary<string, string> { ["windows"] = "demo.exe", ["linux"] = "demo" },
            launch = new
            {
                workingDirectory = "{installDir}",
                arguments = new[] { "--model", "{model}", "--port", "{port}", "--ctx", "{context}" },
                environment = new Dictionary<string, string> { ["DEMO"] = "1" },
            },
            ready = new { type = "http", path = "/health", timeoutSeconds = 5 },
            parameters = new object[]
            {
                new { key = "port", label = "端口", group = "基本", type = "Integer", @default = "9000", min = 1.0, max = 65535.0 },
                new { key = "context", label = "上下文", group = "基本", type = "Integer", @default = "4096" },
            },
            compatibility = new { driver = "polystrata-matrix" },
        };
        var path = Path.Combine(pluginDir, "plugin.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));

        var parsed = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path))!;
        Check(parsed.Validate() == null, "valid manifest rejected: " + parsed.Validate());
        var backend = new ProcessPluginBackend(parsed, pluginDir).Initialize();
        Equal("demo", backend.Descriptor.Id);
        Equal(2, backend.Parameters.Count);

        var model = Path.Combine(root, "m.gguf");
        File.WriteAllBytes(model, Fixtures.StrataGguf());
        var config = new ManagerConfig { ModelPath = model };
        config.Engines["demo"] = new EngineSettings { Parameters = new() { ["context"] = "8192" } };
        var plan = backend.BuildLaunch(new EngineStartRequest { Config = config });
        Check(plan.Arguments.Contains(model), "model not substituted");
        Check(plan.Arguments.Contains("9000"), "port default not substituted");
        Check(plan.Arguments.Contains("8192"), "parameter override not substituted");
        Equal(pluginDir, plan.WorkingDirectory);
        Equal("1", plan.Environment["DEMO"]);
        Equal(9000, backend.ResolvePort(new EngineStartRequest { Config = config }));

        // Invalid manifests must be rejected with a reason, never crash.
        var bad = new PluginManifest { Schema = 2, Id = "bad" };
        Check(bad.Validate() != null, "bad schema accepted");
        var noExe = new PluginManifest { Schema = 1, Id = "x", DisplayName = "x" };
        Check(noExe.Validate() != null, "manifest without executable accepted");
    }

    /// <summary>Discovery scans the plugins folder and rejects id collisions with built-ins.</summary>
    public static void RunDiscovery(string root)
    {
        var pluginDir = Path.Combine(root, "plugins", "collide");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            id = "llamacpp",
            displayName = "Collision",
            executable = new Dictionary<string, string> { ["windows"] = "x.exe" },
            launch = new { arguments = new[] { "--x" } },
            ready = new { type = "http", path = "/health" },
        }));
        var (loaded, rejected) = PluginLoader.Discover();
        Check(!loaded.Any(l => l.Backend.Descriptor.Id == "llamacpp"), "built-in id was overridden by a plugin");
        Check(rejected.Any(r => r.Reason.Contains("已被内置后端占用")), "id collision not reported");
    }
}
