using System.IO;

namespace LlamaManager.Services;

public static class PresetService
{
    public static string GeneratePresetArgs(string modelPath, int port = 8080)
    {
        var file = new FileInfo(modelPath);
        if (!file.Exists) throw new FileNotFoundException("未找到模型文件。", modelPath);
        // Device memory is a point-in-time measurement; refresh only for an explicit new preset.
        CudaService.Invalidate();
        return Plan(GgufService.TryRead(modelPath), file.Length, CudaService.Detect(modelPath), port);
    }

    /// <summary>
    /// Conservative starting point. Fit, when supported, makes the final allocation decision.
    /// A MoE model never has to fit whole: its routed experts can stay on the CPU, so the plan
    /// offloads every layer and instead decides how many layers' experts must stay off the card.
    /// </summary>
    public static string Plan(GgufMetadata? metadata, long bytes, CudaInfo device, int port = 8080)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var threads = Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
        var context = metadata?.ContextLength > 0 ? Math.Min(4096, metadata.ContextLength) : 4096;
        var layers = metadata?.LayerCount ?? 0;
        var gpuLayers = 0;
        string? moeOffload = null;
        if (device.Available)
        {
            if (device.SupportsFit) gpuLayers = 999; // fit reduces layers/context as needed
            else if (layers > 0 && device.FreeVramMb > 0)
            {
                var modelMb = Math.Max(1, bytes / (1024d * 1024));
                var available = Math.Max(0, device.FreeVramMb - 768);
                if (metadata is { IsMoE: true, ExpertCount: > 0 } && available < modelMb)
                {
                    // Offload everything and park the first N layers' experts on the CPU. The card
                    // first pays for compute/display reserve and the KV cache; what is left holds
                    // the experts. Calibrated against a 13.3 GiB A3B file on a 6 GiB card, where
                    // the measured running boundary is ~26 layers and the fastest setting is ~30:
                    // this formula lands at 28 for that machine.
                    var headSize = metadata!.AttentionHeads > 0 ? metadata.EmbeddingLength / metadata.AttentionHeads : 128;
                    var kvHeads = metadata.KvHeads > 0 ? metadata.KvHeads : Math.Max(1, metadata.AttentionHeads);
                    var key = metadata.KeyLength > 0 ? metadata.KeyLength : headSize;
                    var value = metadata.ValueLength > 0 ? metadata.ValueLength : headSize;
                    var kvMb = (double)context * layers * kvHeads * (key + value) * 2 / (1024 * 1024);
                    var expertRoom = Math.Max(0, available - kvMb);
                    var expertMb = modelMb * 0.91;
                    var perLayer = expertMb / Math.Max(1, layers);
                    var onCpu = expertMb - expertRoom;
                    gpuLayers = 999;
                    moeOffload = onCpu <= 0 ? "0"
                        : Math.Clamp((int)Math.Ceiling(onCpu / Math.Max(1, perLayer)), 1, Math.Max(1, layers)).ToString();
                }
                else
                {
                    var headSize = metadata!.AttentionHeads > 0 ? metadata.EmbeddingLength / metadata.AttentionHeads : 128;
                    var kvHeads = metadata.KvHeads > 0 ? metadata.KvHeads : Math.Max(1, metadata.AttentionHeads);
                    var key = metadata.KeyLength > 0 ? metadata.KeyLength : headSize;
                    var value = metadata.ValueLength > 0 ? metadata.ValueLength : headSize;
                    // F16 KV + compute/display reserve. Hybrid architectures may need less; overestimate deliberately.
                    var kvMb = (double)context * layers * kvHeads * (key + value) * 2 / (1024 * 1024);
                    var withKv = Math.Max(0, available - kvMb);
                    gpuLayers = Math.Clamp((int)Math.Floor(withKv / modelMb * layers), 0, layers);
                    if (withKv >= modelMb * 1.1) gpuLayers = layers + 1;
                }
            }
        }
        var args = $"--host 127.0.0.1 --port {port} -t {threads} -tb {threads} -c {context} -np 1 -b 512 -ub 128 -ngl {gpuLayers}";
        if (moeOffload != null) args += $" --n-cpu-moe {moeOffload}";
        if (device.Available && device.SupportsFit) args += " --fit on";
        if (device.SupportsFlash) args += " -fa auto";
        return args + " --jinja";
    }
}
