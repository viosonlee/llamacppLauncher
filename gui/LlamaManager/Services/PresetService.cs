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
        var threads = Math.Min(8, Environment.ProcessorCount);
        if (threads < 1) threads = 1;

        var filename = Path.GetFileNameWithoutExtension(modelPath);
        var isMoE = filename.Contains("A3B", StringComparison.OrdinalIgnoreCase) ||
                    filename.Contains("moe", StringComparison.OrdinalIgnoreCase) ||
                    filename.Contains("mixture.of.experts", StringComparison.OrdinalIgnoreCase);

        if (isMoE)
            return GenerateMoEArgs(modelPath, port, threads, size);

        return GenerateStandardArgs(port, threads, size);
    }

    private static string GenerateMoEArgs(string modelPath, int port, int threads, long size)
    {
        var cuda = CudaService.Detect();
        var gpuArgs = cuda.Available
            ? "-ngl all --fit on --fit-target 512"
            : "-ngl 0";

        var freeRAM = GetFreePhysicalMemory();
        var useNoMmap = freeRAM - size > 2L * 1024 * 1024 * 1024;

        var parts = new List<string>
        {
            "--host 127.0.0.1",
            $"--port {port}",
            $"-t {threads}",
            $"-tb {threads}",
            "-c 8192",
            "-np 1",
            "-b 1024",
            "-ub 512",
            gpuArgs,
            "--cache-prompt",
            "--flash-attn on",
            "--cpu-moe",
            "--reasoning off"
        };

        if (useNoMmap)
        {
            parts.Add("--no-mmap");
            parts.Add("--mlock");
        }

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
            ? (size <= gb3_5
                ? "-ngl all --fit on --fit-target 1024"
                : "-ngl auto --fit on --fit-target 1024")
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
            "--reasoning off"
        };

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
