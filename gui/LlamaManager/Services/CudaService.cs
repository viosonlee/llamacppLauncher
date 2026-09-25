using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace LlamaManager.Services;

public class CudaInfo
{
    public bool Available { get; set; }
    public string Text { get; set; } = string.Empty;
    public int TotalVramMb { get; set; }
    public int FreeVramMb { get; set; }

    public void Deconstruct(out bool available, out string text)
    {
        available = Available;
        text = Text;
    }
}

public static class CudaService
{
    public static CudaInfo Detect()
    {
        var serverPath = ConfigService.GetLlamaServerPath();
        if (!File.Exists(serverPath))
            return new CudaInfo { Available = false, Text = "未找到 llama-server.exe。" };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = serverPath,
                Arguments = "--list-devices",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return new CudaInfo { Available = false, Text = "无法启动 llama-server.exe。" };

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var text = output.Trim();
            var available = Regex.IsMatch(text, @"CUDA\d+:");

            int totalVram = 0, freeVram = 0;
            var match = Regex.Match(text, @"CUDA\d+:\s*(?<name>[^\(]+)\s*\((?<total>\d+)\s*MiB,\s*(?<free>\d+)\s*MiB\s+free\)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                _ = int.TryParse(match.Groups["total"].Value, out totalVram);
                _ = int.TryParse(match.Groups["free"].Value, out freeVram);
            }

            return new CudaInfo
            {
                Available = available,
                Text = string.IsNullOrEmpty(text) ? "无 CUDA 设备信息。" : text,
                TotalVramMb = totalVram,
                FreeVramMb = freeVram
            };
        }
        catch (Exception ex)
        {
            return new CudaInfo { Available = false, Text = ex.Message };
        }
    }
}
