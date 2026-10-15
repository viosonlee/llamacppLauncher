using System.IO;
using LlamaManager.Services;
using static Program;

internal static class CliRegression
{
    /// <summary>
    /// Phase 7: CLI commands resolve the backend through the registry and reject unknown ids.
    /// The bootstrap requirements reader prefers the engine's own pinned list.
    /// </summary>
    public static async Task RunAsync(string root)
    {
        // An unknown --backend must fail loudly, not silently fall back to llama.cpp.
        var output = new StringWriter();
        try
        {
            await CommandLineService.RunAsync(["--status", "--backend", "does-not-exist"], output);
            throw new Exception("unknown backend accepted");
        }
        catch (ArgumentException ex) { Check(ex.Message.Contains("未知引擎"), "wrong error for unknown backend: " + ex.Message); }

        // --help lists the new commands and exits 0.
        var help = new StringWriter();
        Equal(0, await CommandLineService.RunAsync(["--help"], help));
        Check(help.ToString().Contains("--bootstrap") && help.ToString().Contains("--backend"), "help missing new options");

        // Requirements: the engine's own pinned list wins.
        var serveRoot = Path.Combine(root, "reqs");
        Directory.CreateDirectory(serveRoot);
        File.WriteAllText(Path.Combine(serveRoot, "requirements.txt"), "# comment\nnumpy==2.5.3\njinja2==3.1.6\n");
        var reqs = BootstrapService.ReadRequirements(serveRoot);
        Equal(2, reqs.Count);
        Equal("numpy==2.5.3", reqs[0]);
        // No source tree: a built-in fallback is used.
        var fallback = BootstrapService.ReadRequirements(Path.Combine(root, "no-such-dir"));
        Check(fallback.Count > 0 && fallback.Contains("numpy"), "fallback requirements empty");
    }
}
