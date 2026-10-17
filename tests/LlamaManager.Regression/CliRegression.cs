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
        Check(help.ToString().Contains("--auto-bootstrap") && help.ToString().Contains("--all"), "help missing --auto-bootstrap or --all");

        // A value-less flag must not be mistaken for a missing value: "--doctor --offline" used
        // to fail with "命令选项缺少值" because the loop demanded a value after every token.
        var doctor = new StringWriter();
        var doctorCode = await CommandLineService.RunAsync(["--doctor", "--offline"], doctor);
        Check(doctorCode is 0 or 1, "doctor returned an unexpected exit code: " + doctorCode);
        Check(doctor.ToString().Contains("python"), "doctor report does not mention python");

        // A value-bearing option with no value is still rejected, and named.
        try
        {
            await CommandLineService.RunAsync(["--status", "--model"], new StringWriter());
            throw new Exception("missing option value accepted");
        }
        catch (ArgumentException ex) { Check(ex.Message.Contains("缺少值"), "wrong error for a missing value: " + ex.Message); }

        // Unknown options are rejected and named.
        try
        {
            await CommandLineService.RunAsync(["--status", "--nonexistent-flag"], new StringWriter());
            throw new Exception("invalid flag accepted");
        }
        catch (ArgumentException ex) { Check(ex.Message.Contains("未知命令选项"), "wrong error for invalid flag: " + ex.Message); }

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
