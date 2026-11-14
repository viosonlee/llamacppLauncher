using System.Diagnostics;
using System.IO;
using LlamaManager.Engines;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>
/// Installs Python 3.10+ on demand so a fresh machine needs no manual setup before the Strata
/// family can run. Two routes, in order: winget (fast, no browser, user scope, no admin prompt),
/// then the official python.org installer (silent, user scope, adds itself to PATH).
///
/// The discovery helpers are pure and take the roots they scan as parameters, so the regression
/// suite can exercise the candidate selection and argument shapes without touching this machine.
/// The command runner is a settable seam for the same reason.
/// </summary>
public static class PythonInstaller
{
    /// <summary>Minimum interpreter the Strata service runs on.</summary>
    public const string WingetPackageId = "Python.Python.3.12";

    /// <summary>Pinned fallback installer (user-scope, silent, no launcher prompt).</summary>
    public const string FallbackVersion = "3.12.10";
    public static string FallbackUrl => $"https://www.python.org/ftp/python/{FallbackVersion}/python-{FallbackVersion}-amd64.exe";

    public sealed record InstallResult(bool Ok, string? PythonPath, string Detail);

    /// <summary>Runs one command line and returns its exit code. Overridden by tests.</summary>
    public static Func<string, IReadOnlyList<string>, ILogSink, CancellationToken, Task<int>> ProcessRunner { get; set; } = DefaultRunner;

    /// <summary>Returns an already usable interpreter, or null. Overridden by tests.</summary>
    public static Func<string?> FindInstalledProbe { get; set; } = FindInstalled;

    /// <summary>Returns winget.exe, or null. Overridden by tests.</summary>
    public static Func<string?> FindWingetProbe { get; set; } = FindWinget;

    /// <summary>Scans the standard install roots. Overridden by tests.</summary>
    public static Func<string?> ProbeInstalledProbe { get; set; } = ProbeInstalledDirectories;

    /// <summary>Fetches the python.org installer and returns its local path. Overridden by tests.</summary>
    public static Func<string, ILogSink, CancellationToken, Task<string>> InstallerFetcher { get; set; } = DefaultFetchInstaller;

    /// <summary>Command-line arguments used to drive winget. Exposed so tests pin the shape.</summary>
    public static IReadOnlyList<string> WingetArguments { get; } =
    [
        "install", "--id", WingetPackageId, "--exact",
        "--scope", "user", "--silent",
        "--accept-package-agreements", "--accept-source-agreements",
    ];

    /// <summary>Command-line arguments used to drive the python.org installer silently.</summary>
    public static IReadOnlyList<string> InstallerArguments { get; } =
    [
        "/quiet", "InstallAllUsers=0", "PrependPath=1",
        "Include_test=0", "Include_launcher=1", "SimpleInstall=0",
    ];

    /// <summary>
    /// Directories to probe for an interpreter after an install. Pure: the caller supplies the
    /// roots, so this is usable on any machine and under test.
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories(string localAppData, string programFiles, string programFilesX86)
    {
        var dirs = new List<string>();
        // python.org / winget (user scope) drop their installs here.
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            dirs.Add(Path.Combine(localAppData, "Programs", "Python"));
            dirs.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps"));
        }
        if (!string.IsNullOrWhiteSpace(programFiles)) dirs.Add(programFiles);
        if (!string.IsNullOrWhiteSpace(programFilesX86)) dirs.Add(programFilesX86);
        return dirs;
    }

    /// <summary>
    /// Picks the exe of the newest <c>Python3NN</c> install under <paramref name="root"/>, or of
    /// <paramref name="root"/> itself when it is such a directory. Pure except for
    /// <paramref name="fileExists"/>, which tests substitute.
    /// </summary>
    public static string? SelectNewest(string root, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

        if (TryVersionOfName(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), out _))
        {
            var direct = Path.Combine(root, "python.exe");
            return fileExists(direct) ? direct : null;
        }

        string? best = null;
        var bestVersion = default(Version);
        foreach (var dir in SafeEnumerateDirectories(root))
        {
            if (!TryVersionOfName(Path.GetFileName(dir), out var version)) continue;
            if (best != null && version <= bestVersion) continue;
            var exe = Path.Combine(dir, "python.exe");
            if (!fileExists(exe)) continue;
            best = exe;
            bestVersion = version;
        }
        return best;
    }

    /// <summary>The interpreter already usable on this machine, if any.</summary>
    public static string? FindInstalled()
    {
        var direct = StrataBackend.ResolvePython(ConfigService.ReadConfig());
        if (direct != null) return direct;
        return ProbeInstalledDirectories();
    }

    /// <summary>Scans the standard install roots for an interpreter the PATH does not know about.</summary>
    public static string? ProbeInstalledDirectories()
    {
        foreach (var dir in CandidateDirectories(
            Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "",
            Environment.GetEnvironmentVariable("ProgramFiles") ?? "",
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? ""))
        {
            var found = SelectNewest(dir, File.Exists);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>winget.exe if it ships on this machine, else null.</summary>
    public static string? FindWinget()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, "winget.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Makes sure a usable Python exists, installing it when it does not. Returns the interpreter
    /// path on success. Never throws for an install failure: the caller decides how loud to be.
    /// </summary>
    public static async Task<InstallResult> EnsureAsync(ILogSink log, CancellationToken ct = default)
    {
        var existing = FindInstalledProbe();
        if (existing != null)
        {
            log.Write("已找到 Python：" + existing);
            return new InstallResult(true, existing, "已存在。");
        }

        var winget = FindWingetProbe();
        if (winget != null)
        {
            log.Write("未找到 Python，使用 winget 安装 " + WingetPackageId + "…");
            var code = await RunAsync(winget, WingetArguments, log, ct).ConfigureAwait(false);
            var afterWinget = ProbeInstalledProbe();
            if (code == 0 && afterWinget != null)
            {
                log.Write("winget 安装完成：" + afterWinget);
                return new InstallResult(true, afterWinget, "winget 安装完成。");
            }
            log.Write(afterWinget == null
                ? $"winget 未提供可用解释器（退出码 {code}），改用 python.org 安装包。"
                : "winget 安装完成。");
            if (afterWinget != null) return new InstallResult(true, afterWinget, "winget 安装完成。");
        }
        else log.Write("未找到 winget，改用 python.org 安装包。");

        return await InstallFromPythonOrgAsync(log, ct).ConfigureAwait(false);
    }

    private static Task<string> DefaultFetchInstaller(string destination, ILogSink log, CancellationToken ct)
        => EngineDownloadService.DownloadAsync(FallbackUrl, destination, null, log.Write, ct);

    private static async Task<InstallResult> InstallFromPythonOrgAsync(ILogSink log, CancellationToken ct)
    {
        var installer = Path.Combine(Path.GetTempPath(), $"python-{FallbackVersion}-amd64.exe");
        try
        {
            log.Write("下载 Python " + FallbackVersion + " 安装包…");
            await InstallerFetcher(installer, log, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Write("下载 Python 安装包失败：" + ex.Message);
            return new InstallResult(false, null, "下载失败：" + ex.Message);
        }

        try
        {
            log.Write("静默安装 Python（用户级，自动加入 PATH）…");
            var code = await RunAsync(installer, InstallerArguments, log, ct).ConfigureAwait(false);
            var installed = ProbeInstalledProbe();
            if (installed != null)
            {
                log.Write("Python 安装完成：" + installed);
                return new InstallResult(true, installed, "python.org 安装完成。");
            }
            log.Write($"Python 安装未生效（退出码 {code}）。请手动安装 Windows x64 Python 3.10+ 后重试。");
            return new InstallResult(false, null, $"退出码 {code}；请手动安装 Python 3.10+。");
        }
        finally
        {
            try { if (File.Exists(installer)) File.Delete(installer); } catch { }
        }
    }

    private static Task<int> RunAsync(string exe, IReadOnlyList<string> arguments, ILogSink log, CancellationToken ct)
        => ProcessRunner(exe, arguments, log, ct);

    private static async Task<int> DefaultRunner(string exe, IReadOnlyList<string> arguments, ILogSink log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var start = StrataService.CaptureStart(exe);
            foreach (var arg in arguments) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process == null) return -1;
            using var reads = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Both redirected pipes must be drained concurrently, including output without
            // newlines. Discard it instead of retaining unbounded installer output in memory.
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, reads.Token);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, reads.Token);
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch
            {
                reads.Cancel();
                // Disposing Process only closes our handle; it does not stop an installer.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Write("执行失败：" + ex.Message);
            return -1;
        }
    }

    /// <summary>True when <paramref name="name"/> looks like <c>Python3NN</c>; yields the version.</summary>
    public static bool TryVersionOfName(string name, out Version version)
    {
        version = new Version(0, 0);
        if (name.Length < 8 || !name.StartsWith("Python3", StringComparison.OrdinalIgnoreCase)) return false;
        var digits = name[7..];
        if (digits.Length != 2 && digits.Length != 3) return false;
        foreach (var ch in digits) if (!char.IsAsciiDigit(ch)) return false;
        var major = 3;
        var minor = int.Parse(digits[..2]);
        var patch = digits.Length == 3 ? int.Parse(digits[2..]) : 0;
        version = new Version(major, minor, patch);
        return true;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
