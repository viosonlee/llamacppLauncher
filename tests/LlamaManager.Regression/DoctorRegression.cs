using System.IO;
using System.Text.Json.Nodes;
using LlamaManager.Engines;
using LlamaManager.Services;
using static Program;

internal static class DoctorRegression
{
    /// <summary>
    /// Version comparison across the three schemes in play. Numeric runs compare numerically
    /// ("b9" < "b11671"), letters compare lexically, and schemes that do not line up
    /// (different letter/digit pattern) are never reported as updates - a wrong "update
    /// available" nag is worse than a missed one.
    /// </summary>
    public static void RunVersionComparison()
    {
        // llama.cpp build numbers.
        Check(DoctorService.IsOutdated("b11641", "b11671"), "b11641 vs b11671 not outdated");
        Check(!DoctorService.IsOutdated("b11671", "b11671"), "same build reported outdated");
        Check(!DoctorService.IsOutdated("b11700", "b11671"), "newer local reported outdated");
        Check(DoctorService.IsOutdated("b9", "b11671"), "numeric runs must not compare lexically");

        // The fork / PolyStrata scheme, with and without the leading v.
        Check(DoctorService.IsOutdated("0.1.44.1-apex.1", "0.1.44.1-apex.2"), "apex patch bump");
        Check(DoctorService.IsOutdated("v0.1.44.1-poly.2", "0.1.44.1-poly.5"), "leading v normalised");
        Check(!DoctorService.IsOutdated("0.1.44.1-apex.2", "0.1.44.1-apex.1"), "downgrade reported as update");
        Check(!DoctorService.IsOutdated("0.1.44.1-poly.5", "0.1.44.1-poly.5"), "equal poly versions");

        // Mixed schemes are incomparable: never claim an update.
        Check(!DoctorService.IsOutdated("b11671", "0.1.44.1-apex.1"), "cross-scheme false update");
        Check(!DoctorService.IsOutdated("prism-b10743-adfffbe", "0.1.44.1-poly.5"), "prism vs poly false update");
        Check(!DoctorService.IsOutdated("", "b11671"), "empty local version");

        // Equal digits of different width compare numerically, not lexically ("2" < "10").
        Check(DoctorService.IsOutdated("1.2", "1.10"), "1.2 vs 1.10 must be numeric");
    }

    /// <summary>
    /// The doctor's local checks against a fixture root: Prism (never present here) must be
    /// reported missing with its install path, every registered engine must appear, and update
    /// detection works through the release seam. llama.cpp is not asserted here: its resolver
    /// walks up parent directories looking for a real install, so on a machine with one the
    /// finding is legitimately "ok" rather than "missing".
    /// </summary>
    public static async Task RunLocalChecksAsync(string root)
    {
        var savedNativeProbe = BuiltInInstallation.NativeProbe;
        BuiltInInstallation.NativeProbe = (start, timeout, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var local = Path.GetFullPath(start.FileName).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var name = Path.GetFileName(start.FileName);
            if (local && name.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new BuiltInInstallation.ProbeOutput(0, "version: 0.9.0-fixture (build 11671, commit fixture)"));
            if (local && name.Equals("strata-poly.exe", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new BuiltInInstallation.ProbeOutput(2, "usage: strata-poly --model FILE.gguf (--serve | --tokens ID) --max-context N"));
            if (local && name.StartsWith("fake-python", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new BuiltInInstallation.ProbeOutput(0, "PolyStrata imports OK"));
            return savedNativeProbe(start, timeout, ct);
        };
        try
        {
        var report = await DoctorService.RunAsync(checkUpdates: false, CancellationToken.None);
        // The harness pre-seeds engines/prism with a runtime copy, so Prism is present and
        // reported as "manual" (no public download source); its finding must name the path.
        var prism = report.Findings.Single(f => f.Engine == "prism");
        Check(prism.Status is "missing" or "manual", $"unexpected prism status {prism.Status}");
        Check(prism.Detail.Contains("engines"), "prism finding does not name its install path");
        Check(report.Findings.Any(f => f.Engine == "llamacpp"), "llamacpp finding missing");
        var python = report.Findings.Single(f => f.Engine == "python");
        Check(python.Status is "ok" or "missing", "python finding missing from report");
        Check(report.Findings.Any(f => f.Engine == "polystrata"), "polystrata finding missing");

        // A healthy llama.cpp with a fake engine binary flips the finding to ok.
        Fixtures.CopyRuntime(root);
        var healthy = await DoctorService.RunAsync(checkUpdates: false, CancellationToken.None);
        Equal("ok", healthy.Findings.Single(f => f.Engine == "llamacpp").Status);
        Check(!healthy.Findings.Any(f => f.Status == "outdated"), "offline check reported updates");

        // Update checking through the seam: BUILD.json version newer than the (faked) remote.
        // A healthy strata engine with a BUILD.json: seed the engine folder, a serve tree and a
        // fake python, then check update detection.
        var package = Path.Combine(root, "engines", "polystrata");
        var engineDir = Path.Combine(package, "engine-poly");
        Directory.CreateDirectory(engineDir);
        File.WriteAllText(Path.Combine(engineDir, "strata-poly.exe"), "# fixture\n");
        File.WriteAllText(Path.Combine(engineDir, "BUILD.json"),
            """{ "version": "0.1.44.1-poly.2", "archs": [75] }""");
        var source = Path.Combine(package, "serve");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "server.py"), "# fixture\n");
        var config = new LlamaManager.Models.ManagerConfig();
        config.Engines["polystrata"] = new LlamaManager.Models.EngineSettings
        {
            Parameters = new Dictionary<string, string> { ["packageRoot"] = package },
        };
        ConfigService.SaveEngineSettings(config, "polystrata", config.Engines["polystrata"]);
        var fakePython = Path.Combine(root, "fake-python.cmd");
        File.WriteAllText(fakePython, "@echo off\r\necho fake python 3.12\r\n");
        config.Engines["polystrata"].Parameters["pythonPath"] = fakePython;
        ConfigService.SaveEngineSettings(config, "polystrata", config.Engines["polystrata"]);

        var oldRemote = new ReleaseInfo("v0.1.44.1-poly.1", []);
        DoctorService.LatestRelease = (_, _) => Task.FromResult<ReleaseInfo?>(oldRemote);
        var current = await DoctorService.RunAsync(checkUpdates: true, CancellationToken.None);
        Check(!current.Findings.Where(f => f.Engine == "polystrata").Any(f => f.Status == "outdated"),
            "local newer than remote reported outdated");

        var newRemote = new ReleaseInfo("v0.1.44.1-poly.9", []);
        DoctorService.LatestRelease = (_, _) => Task.FromResult<ReleaseInfo?>(newRemote);
        var stale = await DoctorService.RunAsync(checkUpdates: true, CancellationToken.None);
        var finding = stale.Findings.FirstOrDefault(f => f.Engine == "polystrata" && f.Status == "outdated");
        if (finding == null)
            throw new Exception("no outdated finding; polystrata states: " +
                string.Join(" | ", stale.Findings.Where(f => f.Engine == "polystrata").Select(f => $"{f.Status}: {f.Detail}")));
        Check(finding.Detail.Contains("0.1.44.1-poly.2") && finding.Detail.Contains("0.1.44.1-poly.9"),
            "outdated detail does not name both versions");

        // A network failure degrades to a local-only report instead of crashing.
        DoctorService.LatestRelease = (_, _) => throw new IOException("network down");
        var offline = await DoctorService.RunAsync(checkUpdates: true, CancellationToken.None);
        Check(offline.Findings.Count > 0, "network failure produced no report");
        AssertInstallationPathPresetUpgrade(root);
        }
        finally { BuiltInInstallation.NativeProbe = savedNativeProbe; }
    }

    private static void AssertInstallationPathPresetUpgrade(string root)
    {
        Check(PathIdentity.Equals(root, ConfigService.GetManagerRoot()) && Path.GetFileName(root).StartsWith("regression-", StringComparison.Ordinal),
            "installation transaction fixture must use the isolated regression root");
        var config = ConfigService.ReadConfig();
        var original = config.Engines["polystrata"];
        var settings = System.Text.Json.JsonSerializer.Deserialize<LlamaManager.Models.EngineSettings>(System.Text.Json.JsonSerializer.Serialize(original))!;
        var oldRoot = Path.Combine(root, "engines", "polystrata");
        var newRoot = Path.Combine(oldRoot, "v0.1.45-poly.8-cuda-13.0");
        var manualRoot = Path.Combine(root, "manual model-specific engine");
        var sharedKey = PathIdentity.Normalize(Path.Combine(root, "models", "shared preset.gguf"));
        var manualKey = PathIdentity.Normalize(Path.Combine(root, "models", "manual preset.gguf"));
        settings.Parameters["packageRoot"] = oldRoot;
        settings.Parameters["serveRoot"] = oldRoot;
        settings.Parameters["pythonPath"] = "";
        settings.ModelPresets[sharedKey] = new() { ["packageRoot"] = oldRoot, ["serveRoot"] = oldRoot, ["pythonPath"] = "", ["context"] = "32768", ["kv"] = "q8_0" };
        settings.ModelPresets[manualKey] = new() { ["packageRoot"] = manualRoot, ["serveRoot"] = manualRoot, ["pythonPath"] = Path.Combine(manualRoot, "python.exe"), ["context"] = "65536" };
        ConfigService.SaveEngineSettings(config, "polystrata", settings);
        var before = ConfigService.ReadConfig();
        try
        {
            ConfigService.SaveEngineInstallation("polystrata", Path.Combine(newRoot, "engine-poly", "strata-poly.exe"),
                new Dictionary<string, string> { ["packageRoot"] = newRoot, ["serveRoot"] = newRoot, ["pythonPath"] = Path.Combine(newRoot, "runtime", "venv", "Scripts", "python.exe") },
                expectedSelection: before.Engines["polystrata"]);
            var after = ConfigService.ReadConfig();
            var shared = after.Engines["polystrata"].ModelPresets[sharedKey];
            Equal(newRoot, shared["packageRoot"]);
            Equal(newRoot, shared["serveRoot"]);
            Equal(Path.Combine(newRoot, "runtime", "venv", "Scripts", "python.exe"), shared["pythonPath"]);
            Equal("32768", shared["context"]);
            Equal("q8_0", shared["kv"]);
            var manual = after.Engines["polystrata"].ModelPresets[manualKey];
            Equal(manualRoot, manual["packageRoot"]);
            Equal(Path.Combine(manualRoot, "python.exe"), manual["pythonPath"]);
            Equal("65536", manual["context"]);
            Equal(before.ActiveBackendId, after.ActiveBackendId);
            Equal(System.Text.Json.JsonSerializer.Serialize(before.Running), System.Text.Json.JsonSerializer.Serialize(after.Running));
            Equal(System.Text.Json.JsonSerializer.Serialize(before.ModelPresets), System.Text.Json.JsonSerializer.Serialize(after.ModelPresets));
        }
        finally { ConfigService.SaveEngineSettings(ConfigService.ReadConfig(), "polystrata", original); }
    }
}
