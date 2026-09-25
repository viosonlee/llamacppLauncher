using System.IO;
using System.Management;

namespace LlamaManager.Services;

public static class PresetService
{
    public static string GeneratePresetArgs(string modelPath, int port = 8080)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"未找到模型文件：{modelPath}");

        var fileInfo = new FileInfo(modelPath);
        var size = fileInfo.Length;
        var threads = Math.Max(1, Environment.ProcessorCount <= 4 ? Environment.ProcessorCount : Environment.ProcessorCount - 2);
        if (threads > 8) threads = 8;

        var isMoE = Models.ModelInfo.DetectIsMoE(modelPath);

        if (isMoE)
            return GenerateMoEArgs(modelPath, port, threads, size);

        return GenerateStandardArgs(port, threads, size);
    }

    private static string GenerateMoEArgs(string modelPath, int port, int threads, long size)
    {
        var cuda = CudaService.Detect();
        string gpuArgs;
        if (cuda.Available)
        {
            var freeMb = cuda.FreeVramMb > 0 ? cuda.FreeVramMb : 5000;
            var modelMb = (int)(size / (1024 * 1024));

            // MoE 模型绝对不要使用 --fit on，因为 128 专家会触发多轮试探性全量显存分配，导致启动卡顿 30~60 秒！
            // 采用直接指定 -ngl 并加上 --fit off，5 秒内闪电秒启且推理速度提升 10 倍以上：
            if (modelMb <= freeMb - 400)
            {
                // 模型完全可以装入显存
                gpuArgs = "-ngl all --fit off";
            }
            else
            {
                // 模型略超显存（如 Ling-3.0 5.37GB 在 6G 显卡），留出 400MB 空间计算可用层数
                var usableMb = Math.Max(500, freeMb - 400);
                var estimatedTotalLayers = 24; // Ling-3.0 等常见微型 MoE 为 24 层
                var layersToOffload = Math.Clamp((int)Math.Round((double)usableMb / modelMb * estimatedTotalLayers), 1, 99);
                gpuArgs = $"-ngl {layersToOffload} --fit off";
            }
        }
        else
        {
            gpuArgs = "-ngl 0";
        }

        var parts = new List<string>
        {
            "--host 127.0.0.1",
            $"--port {port}",
            $"-t {threads}",
            $"-tb {threads}",
            "-c 4096",
            "-np 1",
            "-b 2048",
            "-ub 512",
            gpuArgs,
            "--cache-prompt",
            cuda.Available ? "--flash-attn on" : "",
            "--jinja",
            "--reasoning on",
            "--reasoning-format deepseek",
            "--reasoning-budget 2048"
        }.Where(s => !string.IsNullOrEmpty(s)).ToList();

        return string.Join(" ", parts);
    }

    private static string GenerateStandardArgs(int port, int threads, long size)
    {
        int ctx, batch, ubatch;

        long gb1 = 1024L * 1024 * 1024;
        long gb3_5 = (long)(3.5 * 1024 * 1024 * 1024);
        long gb5 = 5L * 1024 * 1024 * 1024;
        long gb6 = 6L * 1024 * 1024 * 1024;

        if (size <= gb1)
        {
            ctx = 40960; batch = 2048; ubatch = 512;
        }
        else if (size <= gb3_5)
        {
            ctx = 20480; batch = 2048; ubatch = 512;
        }
        else if (size <= gb5)
        {
            ctx = 10240; batch = 1024; ubatch = 512;
        }
        else if (size <= gb6)
        {
            ctx = 8192; batch = 1024; ubatch = 512;
        }
        else
        {
            ctx = 4096; batch = 512; ubatch = 256;
        }

        var cuda = CudaService.Detect();
        var gpu = cuda.Available
            ? (size <= gb5
                ? "-ngl all --fit off"
                : "-ngl auto --fit off")
            : "-ngl 0";

        var parts = new List<string>
        {
            "--host 127.0.0.1",
            $"--port {port}",
            $"-t {threads}",
            $"-tb {threads}",
            $"-c {ctx}",
            "-np 1",
            $"-b {batch}",
            $"-ub {ubatch}",
            gpu,
            "--cache-prompt",
            cuda.Available ? "--flash-attn on" : "",
            "--jinja",
            "--reasoning on",
            "--reasoning-format deepseek"
        }.Where(s => !string.IsNullOrEmpty(s)).ToList();

        return string.Join(" ", parts);
    }

    private static long GetFreePhysicalMemory()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                return Convert.ToInt64(obj["FreePhysicalMemory"]) * 1024;
            }
        }
        catch { }
        return 8L * 1024 * 1024 * 1024; // default 8GB
    }
}
