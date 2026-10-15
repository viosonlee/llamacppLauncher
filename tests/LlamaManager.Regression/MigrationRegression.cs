using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class MigrationRegression
{
    /// <summary>
    /// Phase 2: a pre-plugin config (RunningServer/RunningEmbedding + PID hints) is folded
    /// into the canonical Running slots on read, and the legacy keys disappear on the next save.
    /// </summary>
    public static void RunMigrateAndDelete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llama-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "legacy.json");
            var ticks = DateTime.UtcNow.Ticks;
            var legacy = new JsonObject
            {
                ["ModelPath"] = "model.gguf",
                ["Args"] = "-ngl 0",
                ["Port"] = 8080,
                ["EmbeddingPort"] = 8081,
                ["LastPid"] = 4242,
                ["LastStartedAt"] = "2026-01-01T00:00:00Z",
                ["EmbeddingLastPid"] = 4343,
                ["EmbeddingLastStartedAt"] = "2026-01-01T00:00:00Z",
                ["RunningServer"] = new JsonObject
                {
                    ["ProcessId"] = 4242, ["StartTimeUtcTicks"] = ticks,
                    ["EnginePath"] = "llama-server.exe", ["ModelPath"] = "model.gguf",
                    ["Args"] = "-ngl 0", ["Port"] = 8080, ["Embedding"] = false
                },
                ["RunningEmbedding"] = new JsonObject
                {
                    ["ProcessId"] = 4343, ["StartTimeUtcTicks"] = ticks,
                    ["EnginePath"] = "llama-server.exe", ["ModelPath"] = "bge.gguf",
                    ["Args"] = "--embedding", ["Port"] = 8081, ["Embedding"] = true
                }
            };
            File.WriteAllText(path, legacy.ToJsonString());

            var store = new ConfigStore(path);
            var config = store.Read();
            Check(config.RunningServer != null && config.RunningServer.ProcessId == 4242, "main session not migrated");
            Check(config.RunningEmbedding != null && config.RunningEmbedding.ProcessId == 4343, "embedding session not migrated");

            // Any save rewrites the file; the legacy top-level keys must be gone and the canonical slots present.
            store.Update(c => c.UpdatedAt = "now");
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Check(!root.ContainsKey("RunningServer"), "legacy RunningServer key survived a save");
            Check(!root.ContainsKey("RunningEmbedding"), "legacy RunningEmbedding key survived a save");
            Check(!root.ContainsKey("LastPid"), "legacy LastPid hint survived a save");
            Check(!root.ContainsKey("LastStartedAt"), "legacy LastStartedAt hint survived a save");
            Check(!root.ContainsKey("EmbeddingLastPid"), "legacy EmbeddingLastPid hint survived a save");
            Check(root.ContainsKey("Running"), "canonical Running store missing");

            var reloaded = store.Read();
            Check(reloaded.RunningServer != null && reloaded.RunningServer.ProcessId == 4242, "session lost after rewrite");
            Check(reloaded.RunningEmbedding != null && reloaded.RunningEmbedding.ProcessId == 4343, "embedding lost after rewrite");

            // A second migration pass must be a no-op (idempotent).
            reloaded.Normalize();
            Check(reloaded.Running.Count == 2, "normalize duplicated slots");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>A config with no sessions must not synthesize any.</summary>
    public static void RunEmptyConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llama-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "empty.json");
            File.WriteAllText(path, "{}");
            var store = new ConfigStore(path);
            var config = store.Read();
            Check(config.Running.Count == 0, "empty config invented a session");
            Check(config.RunningServer == null && config.RunningEmbedding == null, "empty config has sessions");
            Check(config.ActiveBackendId == "llamacpp", "default backend not llamacpp");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
