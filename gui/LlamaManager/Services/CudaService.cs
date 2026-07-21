using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace LlamaManager.Services;

public static class CudaService
{
    public static (bool Available, string Text) Detect()
    {
        var serverPath = ConfigService.GetLlamaServerPath();
        if (!File.Exists(serverPath))
            return (false, "未找到 llama-server.exe。");

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
                return (false, "无法启动 llama-server.exe。");

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var text = output.Trim();
            var available = Regex.IsMatch(text, @"CUDA\d+:");
            return (available, string.IsNullOrEmpty(text) ? "无 CUDA 设备信息。" : text);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
