using System.IO;
using System.Net.Http;
using LlamaManager.Engines;
using LlamaManager.Services;
using static Program;

/// <summary>
/// Python auto-install (the last manual step for a fresh machine). The install path is exercised
/// through the service's seams - discovery, probe and fetcher are all injected - so nothing here
/// touches the network or the real machine's Python.
/// </summary>
internal static class PythonRegression
{
    private sealed class Collector : ILogSink
    {
        public List<string> Lines { get; } = [];
        public void Write(string message) => Lines.Add(message);
        public bool Contains(string fragment) => Lines.Any(l => l.Contains(fragment, StringComparison.Ordinal));
    }

    public static void RunVersionParsing()
    {
        static bool Is(Version v, int major, int minor, int patch = 0)
            => v.Major == major && v.Minor == minor && v.Build == patch;

        Check(PythonInstaller.TryVersionOfName("Python312", out var v) && Is(v, 3, 12),
            "Python312 not parsed");
        Check(PythonInstaller.TryVersionOfName("python310", out var lower) && Is(lower, 3, 10),
            "lowercase python310 not parsed");
        Check(PythonInstaller.TryVersionOfName("Python3124", out var four) && Is(four, 3, 12, 4),
            "three-digit patch not parsed");
        Check(!PythonInstaller.TryVersionOfName("Python39", out _), "Python39 (well below 3.10) accepted");
        Check(!PythonInstaller.TryVersionOfName("Python", out _), "bare name accepted");
        Check(!PythonInstaller.TryVersionOfName("Python3xx", out _), "non-numeric accepted");
        Check(!PythonInstaller.TryVersionOfName("Python3", out _), "too short accepted");
    }

    public static void RunCandidateSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "pytest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Older, newer, an unsupported one, and a decoy that is not a Python install.
            foreach (var name in new[] { "Python310", "Python312", "Python39", "Miniconda3" })
                Directory.CreateDirectory(Path.Combine(root, name));
            foreach (var name in new[] { "Python310", "Python312", "Miniconda3" })
                File.WriteAllText(Path.Combine(root, name, "python.exe"), "");

            var newest = PythonInstaller.SelectNewest(root, File.Exists);
            Check(newest == Path.Combine(root, "Python312", "python.exe"),
                "SelectNewest did not pick the highest supported version: " + newest);

            // A directory without python.exe is skipped, so 3.10 wins when 3.12 is incomplete.
            File.Delete(Path.Combine(root, "Python312", "python.exe"));
            Check(PythonInstaller.SelectNewest(root, File.Exists) == Path.Combine(root, "Python310", "python.exe"),
                "incomplete install was not skipped");

            // The root itself being an install directory is honoured.
            Check(PythonInstaller.SelectNewest(Path.Combine(root, "Python310"), File.Exists)
                == Path.Combine(root, "Python310", "python.exe"), "direct install root not honoured");

            Check(PythonInstaller.SelectNewest(Path.Combine(root, "nope"), File.Exists) == null,
                "missing root returned a path");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    public static void RunArgumentShapes()
    {
        // winget must stay silent and non-interactive, and must be user-scoped (no admin prompt).
        var winget = PythonInstaller.WingetArguments;
        Check(winget.Contains("install") && winget.Contains("--id") && winget.Contains(PythonInstaller.WingetPackageId),
            "winget arguments missing the package");
        Check(winget.Contains("--scope") && winget.Contains("user"), "winget not user-scoped");
        Check(winget.Contains("--silent"), "winget not silent");
        Check(winget.Contains("--accept-package-agreements") && winget.Contains("--accept-source-agreements"),
            "winget would block on an agreement prompt");

        // The python.org installer must be silent, user-scoped and self-adding to PATH.
        var installer = PythonInstaller.InstallerArguments;
        Check(installer.Contains("/quiet"), "installer not quiet");
        Check(installer.Contains("InstallAllUsers=0"), "installer would need admin");
        Check(installer.Contains("PrependPath=1"), "installer would not be discoverable afterwards");
        Check(PythonInstaller.FallbackUrl.EndsWith("-amd64.exe", StringComparison.Ordinal)
            && PythonInstaller.FallbackUrl.Contains(PythonInstaller.FallbackVersion),
            "fallback URL is not the pinned 64-bit installer: " + PythonInstaller.FallbackUrl);
    }

    /// <summary>Every branch of EnsureAsync with the seams swapped, then restored.</summary>
    public static async Task RunEnsureAsync()
    {
        var saved = (
            PythonInstaller.FindInstalledProbe,
            PythonInstaller.FindWingetProbe,
            PythonInstaller.ProbeInstalledProbe,
            PythonInstaller.ProcessRunner,
            PythonInstaller.InstallerFetcher);
        try
        {
            // 1. Already present: never run anything.
            var calls = new List<string>();
            PythonInstaller.FindInstalledProbe = () => @"C:\py\python.exe";
            PythonInstaller.FindWingetProbe = () => "winget.exe";
            PythonInstaller.ProcessRunner = (exe, _, _, _) => { calls.Add(exe); return Task.FromResult(0); };
            var log = new Collector();
            var found = await PythonInstaller.EnsureAsync(log);
            Check(found.Ok && found.PythonPath == @"C:\py\python.exe", "existing Python not reused");
            Check(calls.Count == 0, "existing Python still triggered a command");

            // 2. winget succeeds and the probe finds the fresh install.
            calls.Clear();
            PythonInstaller.FindInstalledProbe = () => null;
            PythonInstaller.ProbeInstalledProbe = () => @"C:\user\Python312\python.exe";
            IReadOnlyList<string>? wingetArgs = null;
            PythonInstaller.ProcessRunner = (exe, args, _, _) =>
            {
                calls.Add(exe);
                wingetArgs = args;
                return Task.FromResult(0);
            };
            log = new Collector();
            var viaWinget = await PythonInstaller.EnsureAsync(log);
            Check(viaWinget.Ok && viaWinget.PythonPath == @"C:\user\Python312\python.exe",
                "winget path did not yield the interpreter");
            Check(calls.Count == 1 && calls[0] == "winget.exe", "winget was not the chosen route");
            Check(wingetArgs != null && wingetArgs.Contains(PythonInstaller.WingetPackageId),
                "winget invoked without the package id");

            // 3. No winget: fall back to the python.org installer.
            calls.Clear();
            PythonInstaller.FindWingetProbe = () => null;
            var fetched = 0;
            PythonInstaller.InstallerFetcher = (destination, _, _) =>
            {
                fetched++;
                File.WriteAllText(destination, "");
                return Task.FromResult(destination);
            };
            PythonInstaller.ProcessRunner = (exe, _, _, _) => { calls.Add(exe); return Task.FromResult(0); };
            log = new Collector();
            var viaOrg = await PythonInstaller.EnsureAsync(log);
            Check(viaOrg.Ok, "python.org fallback did not report success");
            Check(fetched == 1, "installer was not fetched exactly once");
            Check(calls.Count == 1 && calls[0].EndsWith("-amd64.exe", StringComparison.Ordinal),
                "python.org installer was not executed");

            // 4. Offline: the fetch failure is reported, not thrown.
            PythonInstaller.InstallerFetcher = (_, _, _) => throw new HttpRequestException("offline");
            log = new Collector();
            var offline = await PythonInstaller.EnsureAsync(log);
            Check(!offline.Ok && offline.Detail.Contains("下载失败"), "offline install did not degrade: " + offline.Detail);
            Check(log.Contains("下载 Python 安装包失败"), "offline install was not logged");

            // 5. Installer runs but installs nothing: reported as a manual step, not a crash.
            PythonInstaller.InstallerFetcher = (destination, _, _) =>
            {
                File.WriteAllText(destination, "");
                return Task.FromResult(destination);
            };
            PythonInstaller.ProbeInstalledProbe = () => null;
            PythonInstaller.ProcessRunner = (_, _, _, _) => Task.FromResult(1603);
            log = new Collector();
            var failed = await PythonInstaller.EnsureAsync(log);
            Check(!failed.Ok && failed.Detail.Contains("1603"), "failed installer not surfaced: " + failed.Detail);
            Check(log.Contains("请手动安装"), "manual fallback not suggested");
        }
        finally
        {
            PythonInstaller.FindInstalledProbe = saved.FindInstalledProbe;
            PythonInstaller.FindWingetProbe = saved.FindWingetProbe;
            PythonInstaller.ProbeInstalledProbe = saved.ProbeInstalledProbe;
            PythonInstaller.ProcessRunner = saved.ProcessRunner;
            PythonInstaller.InstallerFetcher = saved.InstallerFetcher;
        }
    }

    /// <summary>Reporting when Python is absent: the doctor must point at the automatic route.</summary>
    public static void RunDoctorActionMentionsInstall()
    {
        var finding = DoctorService.DescribePython(null, 0);
        Check(finding.Status == "missing", "absent Python not reported missing");
        Check(finding.Action != null && finding.Action.Contains("初始化环境"),
            "doctor action does not mention the automatic install: " + finding.Action);
    }
}
