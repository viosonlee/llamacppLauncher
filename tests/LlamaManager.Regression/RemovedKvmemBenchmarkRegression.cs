using System.IO;
using System.Text.Json;
using LlamaManager.Services;
using static Program;

internal static class RemovedKvmemBenchmarkRegression
{
    public static async Task RunAsync(string root)
    {
        var assembly = typeof(CommandLineService).Assembly;
        foreach (var type in new[] { "KvmemTuningService", "KvmemRecommendationStore", "KvmemRecommendationCommands", "KvmemTuningReport", "KvmemCandidate" })
            Check(assembly.GetType("LlamaManager.Services." + type) == null, "removed product type still exists: " + type);
        var before = JsonSerializer.Serialize(ConfigService.ReadConfig());
        foreach (var command in new[] { "--tune-kvmem", "--recommend-kvmem" })
        {
            try { await CommandLineService.RunAsync([command], new StringWriter()); throw new Exception("removed CLI command still accepted: " + command); }
            catch (ArgumentException) { }
        }
        using var help = new StringWriter(); await CommandLineService.RunAsync(["--help"], help);
        Check(!help.ToString().Contains("--tune-kvmem") && !help.ToString().Contains("--recommend-kvmem"), "removed CLI still advertised");
        Equal(before, JsonSerializer.Serialize(ConfigService.ReadConfig()));
        Check(!Directory.Exists(Path.Combine(root, "data", "kvmem-recommendations")), "removed commands wrote recommendation cache");
    }
}
