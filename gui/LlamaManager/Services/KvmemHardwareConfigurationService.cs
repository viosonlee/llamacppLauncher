using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlamaManager.Engines;
using LlamaManager.Models;

namespace LlamaManager.Services;

public sealed record PreparedKvmemRecommendation(ManagerConfig Config, HardwareSnapshot Hardware,
    KvmemRecommendation Recommendation);

/// <summary>Read-only hardware/model inspection and exact current-model application. No benchmarks or recommendation cache.</summary>
public static class KvmemHardwareConfigurationService
{
    private static readonly HardwareProfileService Hardware = new();

    public static async Task<PreparedKvmemRecommendation> PrepareAsync(ManagerConfig config, CancellationToken ct,
        bool refresh = true)
    {
        var snapshot = JsonSerializer.Deserialize<ManagerConfig>(JsonSerializer.Serialize(config))!;
        var model = snapshot.Engines.GetValueOrDefault("kvmem")?.ModelPath;
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
            throw new FileNotFoundException("请先为 KVMem 选择一个已下载的 qwen35moe GGUF 模型。");
        var cache = snapshot.Engines["kvmem"].Parameters.GetValueOrDefault("kvmemDiskPath");
        if (string.IsNullOrWhiteSpace(cache)) cache = HardwareProfileService.DefaultCachePath(ConfigService.GetManagerRoot());
        cache = Path.GetFullPath(cache);
        var hardware = await Hardware.ProbeCachePathAsync(cache, ct, refresh).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var metadata = GgufService.ReadShards(model, ct);
            var size = GgufService.ShardPaths(model).Sum(path => new FileInfo(path).Length);
            var package = KvmemInstallation.ResolvePackageRoot(snapshot);
            var version = package == null ? "" : KvmemInstallation.ReadVersion(package) ?? "";
            var recommendation = KvmemRecommendationService.Recommend(hardware,
                new KvmemModelInfo(model, metadata.Architecture, size, ModelSignature(model)), version, cache);
            return new PreparedKvmemRecommendation(snapshot, hardware, recommendation);
        }, ct).ConfigureAwait(false);
    }

    public static string ModelSignature(string model) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("|", GgufService.ShardPaths(model).Select(path => PathIdentity.Normalize(path) + ":" +
            (FileSignature.Compute(path) ?? throw new IOException("无法确认模型文件签名，请等待模型下载/修改结束后重试。"))))))).ToLowerInvariant();

    public static void Apply(PreparedKvmemRecommendation prepared, IReadOnlyDictionary<string, string> parameters)
    {
        var expected = prepared.Recommendation.Parameters;
        if (!prepared.Recommendation.CanApply || parameters.Count != expected.Count ||
            !expected.All(pair => parameters.TryGetValue(pair.Key, out var value) && value == pair.Value))
            throw new InvalidOperationException("不是本次硬件读取生成的原始保守配置。");
        var model = prepared.Config.Engines["kvmem"].ModelPath;
        if (!File.Exists(model) || ModelSignature(model) != prepared.Recommendation.Scope.ModelFingerprint)
            throw new InvalidOperationException("模型文件已变化，请重新读取硬件与模型后配置。");
        ConfigService.SaveModelRecommendation("kvmem", model, parameters, prepared.Config.Engines["kvmem"]);
    }
}
