using System.IO;
using System.Text.Json;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class KvmemHardwareConfigurationRegression
{
    public static void Run(string root)
    {
        Check(PathIdentity.Equals(root, ConfigService.GetManagerRoot()) && Path.GetFileName(root).StartsWith("regression-"), "isolated root required");
        var store = new ConfigStore(Path.Combine(root, "llama-manager.config.json"));
        var original = store.Read();
        var work = Path.Combine(root, "hardware-apply"); Directory.CreateDirectory(work);
        var model = Path.Combine(work, "model.gguf"); var other = Path.Combine(work, "other.gguf");
        File.WriteAllBytes(model, Fixtures.StrataGguf(layers: 1)); File.WriteAllBytes(other, Fixtures.StrataGguf(layers: 2));
        var hardware = new HardwareSnapshot
        {
            CpuName = "fixture CPU", PhysicalCores = 8, LogicalProcessors = 16,
            TotalRamBytes = 24L << 30, AvailableRamBytes = 20L << 30,
            Gpus = [new GpuHardwareInfo { Index = 0, Uuid = "GPU-fixture", Name = "sm86 GPU", TotalMemoryBytes = 6L << 30, ComputeCapability = 8.6 }],
            CacheDisk = new DiskHardwareInfo { CachePath = Path.Combine(work, "kv-cache"), FreeBytes = 100L << 30, TotalBytes = 500L << 30, MediaType = "SSD" }
        };
        var recommendation = KvmemRecommendationService.Recommend(hardware,
            new KvmemModelInfo(model, "qwen35moe", new FileInfo(model).Length, KvmemHardwareConfigurationService.ModelSignature(model)),
            "0.19.1", hardware.CacheDisk.CachePath);
        Check(recommendation.CanApply, "fixture should have a conservative starting point");
        var config = new ManagerConfig();
        config.Engines["kvmem"] = new EngineSettings { ModelPath = model, Parameters = new() { ["host"] = "127.0.0.1", ["threads"] = "3" },
            ModelPresets = new() { [PathIdentity.Normalize(other)] = new() { ["threads"] = "2", ["context"] = "65536" } } };
        config.Engines["polystrata"] = new EngineSettings { ModelPath = other, Parameters = new() { ["threads"] = "11" } };
        try
        {
            store.Save(config);
            var snapshot = ConfigService.ReadConfig();
            var prepared = new PreparedKvmemRecommendation(snapshot, hardware, recommendation);
            KvmemHardwareConfigurationService.Apply(prepared, recommendation.Parameters);
            var applied = ConfigService.ReadConfig();
            Equal("8", applied.Engines["kvmem"].Parameters["threads"]);
            Equal("2", applied.Engines["kvmem"].ModelPresets[PathIdentity.Normalize(other)]["threads"]);
            Equal("11", applied.Engines["polystrata"].Parameters["threads"]);
            Check(!Directory.Exists(Path.Combine(root, "data", "kvmem-recommendations")), "hardware apply wrote a recommendation cache");
            var before = JsonSerializer.Serialize(applied);
            Throws<IOException>(() => KvmemHardwareConfigurationService.Apply(prepared, recommendation.Parameters));
            var current = prepared with { Config = applied };
            var altered = new Dictionary<string, string>(recommendation.Parameters) { ["threads"] = "12" };
            Throws<InvalidOperationException>(() => KvmemHardwareConfigurationService.Apply(current, altered));
            var leaked = new Dictionary<string, string>(recommendation.Parameters) { ["packageRoot"] = "unrelated" };
            Throws<InvalidOperationException>(() => KvmemHardwareConfigurationService.Apply(current, leaked));
            File.WriteAllBytes(model, Fixtures.StrataGguf(layers: 2));
            Throws<InvalidOperationException>(() => KvmemHardwareConfigurationService.Apply(current, recommendation.Parameters));
            Equal(before, JsonSerializer.Serialize(ConfigService.ReadConfig()));
        }
        finally { store.Save(original); }
    }
}
