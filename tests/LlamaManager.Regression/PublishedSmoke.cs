using System.IO;
using LlamaManager.Models;
using LlamaManager.Services;

internal static class PublishedSmoke
{
    public static async Task<int> RunAsync(string published)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "logs", "package-smoke-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LLAMA_MANAGER_ROOT", root);
        Fixtures.CopyRuntime(root);
        Directory.CreateDirectory(Path.Combine(root, "dist"));
        var executable = Path.Combine(root, "dist", "LlamaManager.exe");
        File.Copy(Path.GetFullPath(published), executable);
        File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "llama-manager.ps1"), Path.Combine(root, "llama-manager.ps1"));
        var model = Path.Combine(root, "model with spaces.gguf");
        File.WriteAllBytes(model, Fixtures.Gguf(moe: false));
        var port = Fixtures.FreePort();
        ConfigService.SaveConfig(new ManagerConfig { ModelPath = model, Port = port, Args = "--port " + port });
        var succeeded = false;
        try
        {
            var help = await ProcessProbe.RunAsync(executable, "--help", TimeSpan.FromSeconds(20));
            Program.Check(help.Contains("--start"), "published CLI output is missing");
            await ProcessProbe.RunAsync(executable, "--service", TimeSpan.FromSeconds(20));
            var status = await ProcessProbe.RunAsync(executable, "--status", TimeSpan.FromSeconds(10));
            Program.Check(status.Contains("\"ready\":true"), "published service did not become ready");
            await ProcessProbe.RunAsync(executable, "--stop", TimeSpan.FromSeconds(10));
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var script = LauncherArguments.Quote(Path.Combine(root, "llama-manager.ps1"));
            await ProcessProbe.RunAsync(shell, $"-NoProfile -ExecutionPolicy Bypass -File {script} -Start -ModelPath {LauncherArguments.Quote(model)} -Port {port}", TimeSpan.FromSeconds(20));
            status = await ProcessProbe.RunAsync(shell, $"-NoProfile -ExecutionPolicy Bypass -File {script} -Status", TimeSpan.FromSeconds(10));
            Program.Check(status.Contains("\"ready\":true"), "PowerShell wrapper failed");
            await ProcessProbe.RunAsync(shell, $"-NoProfile -ExecutionPolicy Bypass -File {script} -Stop", TimeSpan.FromSeconds(10));
            Program.Check(!await ServerService.TestApiReadyAsync(port), "package smoke left server alive");
            Console.WriteLine("PASS published EXE help/service/status/stop and PowerShell wrapper with spaces");
            succeeded = true;
            return 0;
        }
        finally
        {
            await ServerService.StopAsync(ConfigService.ReadConfig());
            if (succeeded) Fixtures.Cleanup(root);
        }
    }
}
