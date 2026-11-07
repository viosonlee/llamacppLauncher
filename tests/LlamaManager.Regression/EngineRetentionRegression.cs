using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LlamaManager.Engines;
using LlamaManager.Services;
using static LlamaManager.Engines.EngineVersionRetention;
using static Program;

/// <summary>
/// Small, guarded filesystem fixtures only. No downloads, GPU probes, actual engine roots or real
/// process probes. The parent runner registers these methods; this file deliberately does not edit it.
/// </summary>
internal static class EngineRetentionRegression
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static void RunKeepCurrentAndNewestPrevious(string root)
    {
        var f = new Fixture(root, "keep-current");
        // Neither creation order nor filesystem mtime is release ordering.
        var current = f.Owned("v0.19.10");
        var oldest = f.Owned("v0.19.1");
        var previous = f.Owned("v0.19.9");
        var middle = f.Owned("v0.19.2");
        var manual = f.Manual("v99.0.0-sm86");
        Directory.SetLastWriteTimeUtc(oldest.Path, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(previous.Path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = f.Run(current.Path);
        Exists(current.Path, previous.Path, manual);
        Missing(oldest.Path, middle.Path);
        Check(Has(result.Kept, current.Path, "current") && Has(result.Kept, previous.Path, "newest-previous"), "current/semantic newest previous were not selected");
        Check(Has(result.Skipped, manual, "not-launcher-owned"), "manual directory was not explicitly skipped");
        Equal(2, result.Pruned.Count(e => !e.Download));
        var newer = f.Owned("v0.19.11");
        result = f.Run(newer.Path);
        Exists(newer.Path, current.Path, manual);
        Missing(previous.Path);
        Equal(3, Directory.EnumerateDirectories(f.EngineRoot).Count()); // two owned + untouched manual
        Equal(0, f.Run(newer.Path).Pruned.Count); // idempotent, not append-only
        Check(!result.Cancelled, "normal retention reported cancellation");
        var pending = f.Owned("v99.0.0");
        f.Write(pending.Path, KvmemInstallation.PendingMarker, "unpublished installation");
        result = f.Run(newer.Path);
        Exists(newer.Path, current.Path, pending.Path);
        Check(Has(result.Blocked, pending.Path, "pending-installation"), "uncommitted owned package was pruned/countable as a rollback version");
    }

    public static void RunVersionOrderingAndMultiGpu(string root)
    {
        var cases = new[]
        {
            new[] { "v0.9.0", "v0.10.0", "v0.11.0" },
            new[] { "b99", "b100", "b101" },
            new[] { "poly.9", "poly.10", "poly.11" },
            new[] { "v1.2.3-rc.9", "v1.2.3-rc.10", "v1.2.3" },
            new[] { "v1.2.3", "v1.2.10", "v1.3.0" },
        };
        foreach (var versions in cases)
        {
            var f = new Fixture(root, "ordering");
            var old = f.Owned(versions[0]);
            var latest = f.Owned(versions[2]);
            var previous = f.Owned(versions[1]);
            f.Run(latest.Path);
            Exists(latest.Path, previous.Path);
            Missing(old.Path);
        }
        foreach (var (backend, architecture, variant) in new[]
        {
            ("llamacpp", "cuda13.4", "cuda-13.4"), ("polystrata", "x64", "cuda"), ("kvmem", "sm86", "nvidia-30"),
        })
        {
            var f = new Fixture(root, "catalog-alias", backend);
            var installed = Enumerable.Range(1, 3).Select(n =>
            {
                var version = backend == "llamacpp" ? "b" + n : "v" + n + ".0.0";
                var path = f.Manual(version + "-" + variant);
                Equal(0, WriteOwnedManifest(f.EngineRoot, f.Manifest(path, version, architecture, []), f.DownloadRoot).Count);
                return path;
            }).ToArray();
            f.Run(installed[2]);
            Exists(installed[2], installed[1]);
            Missing(installed[0]);
        }
        var cuda = new Fixture(root, "llama-cross-cuda-minor", "llamacpp");
        var cudaPackages = new[] { "12.4", "13.4", "13.5" }.Select((minor, index) =>
        {
            var version = "b" + (index + 1);
            var asset = cuda.Cache("llama-cuda-" + minor + ".zip", "verified release " + version, Path.Combine(cuda.DownloadRoot, version));
            var path = cuda.Manual(version + "-cuda-" + minor);
            Equal(0, WriteOwnedManifest(cuda.EngineRoot, cuda.Manifest(path, version, "cuda" + minor, [asset]), cuda.DownloadRoot).Count);
            return (Path: path, Asset: asset);
        }).ToArray();
        var cudaResult = cuda.Run(cudaPackages[2].Path);
        Exists(cudaPackages[2].Path, cudaPackages[1].Path);
        Missing(cudaPackages[0].Path, cudaPackages[0].Asset.Path, cudaPackages[0].Asset.Path + DownloadManifestSuffix);
        Equal(2, Directory.EnumerateDirectories(cuda.EngineRoot).Count());
        foreach (var package in cudaPackages.Skip(1)) Check(File.Exists(package.Asset.Path), "current/previous CUDA-minor cache was removed");
        Check(Has(cudaResult.Kept, cudaPackages[1].Path, "newest-previous"), "llama CUDA minors accumulated as separate GPU retention groups");
        var g = new Fixture(root, "multi-gpu");
        var a1 = g.Owned("v1.0.0", "sm86");
        var a2 = g.Owned("v2.0.0", "sm86");
        var a3 = g.Owned("v3.0.0", "sm86");
        var b1 = g.Owned("v8.0.0", "sm89");
        var b2 = g.Owned("v9.0.0", "sm89");
        var b3 = g.Owned("v10.0.0", "sm89");
        g.Run(a3.Path); // other GPU uses its own newest two, not this GPU's version threshold
        Exists(a2.Path, a3.Path, b2.Path, b3.Path);
        Missing(a1.Path, b1.Path);
        var sharedVersion = g.Owned("v3.0.0", "sm86", suffix: "repair");
        var result = g.Run(sharedVersion.Path);
        Exists(sharedVersion.Path, a3.Path);
        Missing(a2.Path); // one previous directory, even for same-version immutable repairs
        Check(Has(result.Kept, a3.Path, "newest-previous"), "same-version rollback sibling was not retained");
    }

    public static void RunProtectedRootsAndSessions(string root)
    {
        var f = new Fixture(root, "protections");
        var current = f.Owned("v10.0.0");
        var running = f.Owned("v9.0.0");
        var previous = f.Owned("v8.0.0");
        var explicitPackage = f.Owned("v7.0.0", retained: true);
        var preset = f.Owned("v6.0.0");
        var unknown = f.Owned("v5.0.0");
        var staleTicks = f.Owned("v4.0.0");
        var stalePath = f.Owned("v3.0.0");
        var absent = f.Owned("v2.0.0");
        var realOther = f.Manual("unrelated-process");
        var exe = Path.Combine(realOther, "engine.exe");
        var sessions = new[]
        {
            new RunningSessionReference(42, 420, Path.Combine(running.Path, "engine.exe")),
            new RunningSessionReference(43, 430, Path.Combine(unknown.Path, "engine.exe")),
            new RunningSessionReference(44, 440, Path.Combine(staleTicks.Path, "engine.exe")),
            new RunningSessionReference(45, 450, Path.Combine(stalePath.Path, "engine.exe")),
            new RunningSessionReference(46, 460, Path.Combine(absent.Path, "engine.exe")),
        };
        ProcessIdentity? Probe(int pid) => pid switch
        {
            42 => new(pid, 420, Path.Combine(running.Path, "engine.exe")),
            43 => throw new UnauthorizedAccessException("fixture cannot read process identity"),
            44 => new(pid, 441, exe), // PID reused, creation time differs; old recorded package is not live
            45 => new(pid, 450, exe), // creation time agrees but executable path differs
            _ => null,
        };
        var request = f.Request(current.Path) with
        {
            // A recoverable engine-level preset can name any descendant; it protects the whole package.
            ProtectedRoots = [Path.Combine(preset.Path, "serve", "..", "serve")],
            RunningSessions = sessions,
        };
        var result = Run(request, hooks: f.Hooks(probe: Probe));
        Exists(current.Path, running.Path, previous.Path, explicitPackage.Path, preset.Path, unknown.Path, realOther);
        Missing(staleTicks.Path, stalePath.Path, absent.Path);
        Check(Has(result.Kept, previous.Path, "newest-previous"), "extra running protection consumed the normal previous-version slot");
        foreach (var package in new[] { running, explicitPackage, preset, unknown })
            Check(Has(result.Blocked, package.Path), "protected package was not reported as blocked: " + package.Path);

        var python = new Fixture(root, "python-session");
        var pyCurrent = python.Owned("v4.0.0");
        var pyPrevious = python.Owned("v3.0.0");
        var pyWorker = python.Owned("v2.0.0");
        var pyOld = python.Owned("v1.0.0");
        var interpreter = Path.Combine(python.Manual("interpreter"), "engine.exe");
        var pythonRequest = python.Request(pyCurrent.Path) with
        {
            RunningSessions = [new(77, 770, interpreter) { ReferencedRoots = [Path.Combine(pyWorker.Path, "serve")] }],
        };
        result = Run(pythonRequest, hooks: python.Hooks(probe: id => new(id, 770, interpreter)));
        Exists(pyWorker.Path, pyCurrent.Path, pyPrevious.Path);
        Missing(pyOld.Path);
        Check(Has(result.Blocked, pyWorker.Path), "verified Python session did not protect its referenced worker root");
    }

    public static void RunOwnershipAndPathSafety(string root)
    {
        var f = new Fixture(root, "ownership");
        var current = f.Owned("v3.0.0");
        var previous = f.Owned("v2.0.0");
        var bad = new List<string>();
        var changes = new Func<OwnershipManifest, OwnershipManifest>[]
        {
            m => m with { SchemaVersion = 2 },
            m => m with { Owner = "somebody-else" },
            m => m with { BackendId = "polystrata" },
            m => m with { Architecture = "../sm86" },
            m => m with { Architecture = "sm89" }, // valid key, wrong directory identity
            m => m with { Version = "../v1.0.0" },
            m => m with { Version = "v0.0.1" }, // valid version, wrong directory identity
            m => m with { InstalledAtUtc = default },
            m => m with { Root = f.EngineRoot },
            m => m with { Root = m.Root + Path.DirectorySeparatorChar },
            m => m with { Root = Path.Combine(f.EngineRoot, "..", "outside") },
            m => m with { Root = Path.Combine(m.Root, "nested") },
            m => m with { Downloads = [new(f.DownloadRoot, Path.Combine(f.DownloadRoot, "..", "user.zip"), new string('a', 64), 1)] },
            m => m with { Downloads = [new(f.DownloadRoot, Path.Combine(f.DownloadRoot, "asset.zip"), "not-a-hash", 1)] },
        };
        foreach (var change in changes)
        {
            var package = f.Owned("v1.0.0", suffix: "invalid-" + bad.Count);
            f.Rewrite(package, change(package.Manifest));
            bad.Add(package.Path);
        }
        foreach (var key in new[] { "schemaVersion", "owner", "architecture", "downloads" })
        {
            var package = f.Owned("v1.0.0", suffix: "missing-" + key);
            var marker = JsonSerializer.SerializeToNode(package.Manifest, Json)!.AsObject();
            marker.Remove(key);
            f.Write(package.Path, ManifestFileName, marker.ToJsonString());
            bad.Add(package.Path); // constructor defaults cannot stand in for explicit ownership evidence
        }
        var malformed = f.Manual("v0.1.0-sm86-malformed");
        f.Write(malformed, ManifestFileName, "{broken");
        var missingFields = f.Manual("v0.1.0-sm86-missing-fields");
        f.Write(missingFields, ManifestFileName, "{}");
        var nestedParent = f.Manual("manual-container");
        var nested = Path.Combine(nestedParent, "v0.1.0-sm86");
        Directory.CreateDirectory(nested);
        f.Write(nested, ManifestFileName, JsonSerializer.Serialize(current.Manifest with { Root = nested, Version = "v0.1.0" }, Json));
        var outside = Path.Combine(f.Work, "engines", "kvmem-other", "v0.1.0-sm86");
        f.Write(outside, "user-model.gguf", "must never be pruned");
        f.Write(outside, ManifestFileName, JsonSerializer.Serialize(current.Manifest with { Root = outside }, Json));
        var result = f.Run(current.Path);
        Exists([current.Path, previous.Path, malformed, missingFields, nestedParent, nested, outside, .. bad]);
        Equal(0, result.Pruned.Count);
        foreach (var path in bad) Check(Has(result.Skipped, path, "invalid-ownership"), "invalid manifest was not rejected: " + path);
        Check(Has(result.Skipped, malformed, "invalid-ownership") && Has(result.Skipped, missingFields, "invalid-ownership"), "malformed/empty manifests were accepted");
        Throws<IOException>(() => WriteOwnedManifest(f.EngineRoot, current.Manifest, f.DownloadRoot));
        Throws<InvalidDataException>(() => WriteOwnedManifest(f.EngineRoot, current.Manifest with { Root = outside }, f.DownloadRoot));
        var invalidRequest = f.Request(current.Path) with { ProtectedRoots = ["relative-user-root"] };
        result = Run(invalidRequest, hooks: f.Hooks());
        Equal(0, result.Pruned.Count);
        Check(result.Blocked.Count > 0, "ambiguous protection path was not fail-closed");
    }

    public static void RunReparseAndUserDataSafety(string root)
    {
        foreach (var location in new[] { "ancestor", "directory", "manifest", "nested" })
        {
            var f = new Fixture(root, "reparse-" + location);
            var current = f.Owned("v3.0.0");
            var previous = f.Owned("v2.0.0");
            var old = f.Owned("v1.0.0");
            var nested = f.Write(old.Path, "lib/nested.dll", "fixture");
            var unsafePath = location switch
            {
                "ancestor" => f.Work,
                "directory" => old.Path,
                "manifest" => Path.Combine(old.Path, ManifestFileName),
                _ => nested,
            };
            var deleteCalls = 0;
            var hooks = f.Hooks(attributes: path => File.GetAttributes(path) |
                (PathIdentity.Equals(path, unsafePath) ? FileAttributes.ReparsePoint : 0),
                deleteDirectory: path => { deleteCalls++; f.DeleteDirectory(path); });
            var result = Run(f.Request(current.Path), hooks: hooks);
            Equal(0, deleteCalls);
            Exists(current.Path, previous.Path, old.Path);
            Check(result.Blocked.Count > 0, "injected reparse path was not blocked: " + location);
        }
        var race = new Fixture(root, "reparse-after-discovery");
        var active = race.Owned("v3.0.0");
        var rollback = race.Owned("v2.0.0");
        var obsolete = race.Owned("v1.0.0");
        var markerReads = 0;
        var raced = Run(race.Request(active.Path), hooks: race.Hooks(attributes: path =>
        {
            if (PathIdentity.Equals(path, Path.Combine(obsolete.Path, ManifestFileName)) && ++markerReads > 2)
                return File.GetAttributes(path) | FileAttributes.ReparsePoint;
            return File.GetAttributes(path);
        }));
        Exists(active.Path, rollback.Path, obsolete.Path);
        Check(Has(raced.Blocked, obsolete.Path), "reparse introduced after discovery was not rechecked before deleting");

        foreach (var data in new[] { "models/user.gguf", "user.ninfer", "weights.safetensors", "logs/session.log", "config.json", "llama-manager.config.json" })
        {
            var f = new Fixture(root, "user-data");
            var current = f.Owned("v3.0.0");
            var previous = f.Owned("v2.0.0");
            var old = f.Owned("v1.0.0");
            var user = f.Write(old.Path, data, "user-owned content");
            var result = f.Run(current.Path);
            Exists(current.Path, previous.Path, old.Path);
            Equal("user-owned content", File.ReadAllText(user));
            Check(Has(result.Blocked, old.Path, "cleanup-failed"), "model/config/log data inside an owned package was removed");
        }
        RunShippedLogSafety(root);
    }

    private static void RunShippedLogSafety(string root)
    {
        foreach (var mutation in new[] { "unchanged", "changed-hash", "changed-length", "new-log", "new-empty-log", "new-log-directory-file" })
        {
            var f = new Fixture(root, "shipped-logs-" + mutation);
            var current = f.Owned("v3.0.0");
            var previous = f.Owned("v2.0.0");
            var old = f.Owned("v1.0.0", shippedLogs:
            [
                ("bench/results/run/report.log", "original report"),
                ("docs/milestones/release.log", "original milestone"),
                ("logs/shipped.log", ""), // even empty release logs require explicit ownership
            ]);
            if (mutation == "changed-hash") f.Write(old.Path, "bench/results/run/report.log", "modified report"); // same byte length
            if (mutation == "changed-length") f.Write(old.Path, "docs/milestones/release.log", "original milestone\nnew runtime output");
            if (mutation == "new-log") f.Write(old.Path, "docs/milestones/new-user.log", "unregistered user log");
            if (mutation == "new-empty-log") f.Write(old.Path, "bench/results/run/empty-user.log", "");
            if (mutation == "new-log-directory-file") f.Write(old.Path, "logs/runtime.json", "unregistered runtime data");
            var result = f.Run(current.Path);
            Exists(current.Path, previous.Path);
            if (mutation == "unchanged")
            {
                Missing(old.Path);
                Check(Has(result.Pruned, old.Path), "unchanged explicitly shipped logs prevented real-package retention");
            }
            else
            {
                Exists(old.Path);
                Check(Has(result.Blocked, old.Path, "cleanup-failed"), "modified/unregistered log was deleted: " + mutation);
            }
        }
        var malformed = new Fixture(root, "shipped-log-metadata");
        var active = malformed.Owned("v3.0.0");
        var rollback = malformed.Owned("v2.0.0");
        var changes = new Func<OwnershipManifest, OwnershipManifest>[]
        {
            m => m with { ShippedLogs = [new("../user.log", new string('0', 64), 1)] },
            m => m with { ShippedLogs = [new("docs/../../user.log", new string('0', 64), 1)] },
            m => m with { ShippedLogs = [new(@"C:\user.log", new string('0', 64), 1)] },
            m => m with { ShippedLogs = [new("/user.log", new string('0', 64), 1)] },
            m => m with { ShippedLogs = [new("docs/release.log:stream.log", new string('0', 64), 1)] },
            m => m with { ShippedLogs = [new("docs//release.log", new string('0', 64), 1)] },
            m => m with { ShippedLogs = [new("models/user.gguf", new string('0', 64), 1)] },
            m => m with { ShippedLogs = [new("docs/release.log", "not-a-sha", 1)] },
            m => m with { ShippedLogs = [new("docs/release.log", new string('0', 64), -1)] },
            m => m with { ShippedLogs = [new("docs/release.log", new string('0', 64), 1), new(@"DOCS\RELEASE.LOG", new string('0', 64), 1)] },
        };
        for (var i = 0; i < changes.Length; i++)
        {
            var old = malformed.Owned("v1.0.0", suffix: "malformed-log-" + i);
            var invalid = changes[i](old.Manifest);
            Throws<InvalidDataException>(() => WriteOwnedManifest(malformed.EngineRoot, invalid, malformed.DownloadRoot));
            malformed.Rewrite(old, invalid);
        }
        var invalidResult = malformed.Run(active.Path);
        Exists(active.Path, rollback.Path);
        Equal(0, invalidResult.Pruned.Count);
        Equal(changes.Length, invalidResult.Skipped.Count(e => e.Reason.StartsWith("invalid-ownership", StringComparison.Ordinal)));

        var registration = new Fixture(root, "shipped-log-registration");
        var path = registration.Manual("v1.0.0-sm86");
        var file = registration.Write(path, "docs/original.log", "original release bytes");
        var length = new FileInfo(file).Length;
        var manifest = registration.Manifest(path, "v1.0.0", "sm86", []) with { ShippedLogs = [new("docs/original.log", new string('0', 64), length)] };
        Throws<IOException>(() => WriteOwnedManifest(registration.EngineRoot, manifest));
        Check(!File.Exists(Path.Combine(path, ManifestFileName)), "writer registered unverified shipped-log bytes");
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        manifest = manifest with { ShippedLogs = [new("docs/original.log", digest, length + 1)] };
        Throws<IOException>(() => WriteOwnedManifest(registration.EngineRoot, manifest));
        Check(!File.Exists(Path.Combine(path, ManifestFileName)), "writer registered a shipped log with mismatched length");
        var unrecordedPackage = registration.Manual("v2.0.0-sm86");
        var unrecordedLog = registration.Write(unrecordedPackage, "docs/user-before-registration.log", "never a release-owned log");
        Equal(0, WriteOwnedManifest(registration.EngineRoot, registration.Manifest(unrecordedPackage, "v2.0.0", "sm86", [])).Count);
        var registrationCurrent = registration.Owned("v4.0.0");
        var registrationPrevious = registration.Owned("v3.0.0");
        var notAdopted = registration.Run(registrationCurrent.Path);
        Exists(registrationCurrent.Path, registrationPrevious.Path, unrecordedPackage);
        Equal("never a release-owned log", File.ReadAllText(unrecordedLog));
        Check(Has(notAdopted.Blocked, unrecordedPackage), "writer silently discovered/adopted a preexisting user log");

        var linked = new Fixture(root, "shipped-log-reparse");
        var linkedCurrent = linked.Owned("v3.0.0");
        var linkedPrevious = linked.Owned("v2.0.0");
        var linkedOld = linked.Owned("v1.0.0", shippedLogs: [("docs/release.log", "verified release log")]);
        var linkedPath = Path.Combine(linkedOld.Path, "docs", "release.log");
        var unsafeResult = Run(linked.Request(linkedCurrent.Path), hooks: linked.Hooks(attributes: file => File.GetAttributes(file) |
            (PathIdentity.Equals(file, linkedPath) ? FileAttributes.ReparsePoint : 0)));
        Exists(linkedCurrent.Path, linkedPrevious.Path, linkedOld.Path);
        Check(Has(unsafeResult.Blocked, linkedOld.Path), "explicit shipped-log ownership bypassed the reparse guard");
    }

    public static void RunOwnedDownloadRetention(string root)
    {
        var f = new Fixture(root, "downloads");
        var a1 = f.Cache("v1.zip", "release one");
        var a2 = f.Cache("v2.zip", "release two");
        var a3 = f.Cache("v3.zip", "release three");
        var a4 = f.Cache("v4.zip", "release four");
        var old1 = f.Owned("v1.0.0", downloads: [a1]);
        var old2 = f.Owned("v2.0.0", downloads: [a2]);
        var previous = f.Owned("v3.0.0", downloads: [a3]);
        var current = f.Owned("v4.0.0", downloads: [a4]);
        var manual = f.Write(f.DownloadRoot, "kvmem-v0.0.1-windows-x64-nvidia-30-unified.zip", "matching name is not ownership");
        // The sidecar cannot authorize deleting a file that has subsequently been replaced by a user.
        File.WriteAllText(a1.Path, "user changed this cached file");
        var outside = f.Cache("outside.zip", "other backend cache", cacheRoot: Path.Combine(f.Work, "downloads", "other"));
        var badReceipt = Path.Combine(f.DownloadRoot, "outside.zip" + DownloadManifestSuffix);
        f.Write(f.DownloadRoot, Path.GetFileName(badReceipt), JsonSerializer.Serialize(old1.Manifest with { Downloads = [outside] }, Json));
        var result = f.Run(current.Path);
        Missing(old1.Path, old2.Path, a2.Path, a2.Path + DownloadManifestSuffix);
        Exists(previous.Path, current.Path);
        foreach (var path in new[] { a1.Path, a1.Path + DownloadManifestSuffix, a3.Path, a4.Path, manual, outside.Path, badReceipt })
            Check(File.Exists(path), "unowned/current/previous/mismatched asset was removed: " + path);
        Check(Has(result.Pruned, a2.Path, "old-owned-download") && Has(result.Blocked, a1.Path, "download-cleanup-failed"), "cache cleanup did not report success/hash mismatch distinctly");
        Check(Has(result.Skipped, badReceipt, "invalid/unowned-download-receipt"), "cross-root receipt was not rejected");
        Equal("matching name is not ownership", File.ReadAllText(manual));
        Equal("other backend cache", File.ReadAllText(outside.Path));

        var nested = new Fixture(root, "per-release-downloads");
        var releases = Enumerable.Range(1, 3).Select(n =>
        {
            var version = "v" + n + ".0.0";
            var asset = nested.Cache("engine.zip", "release " + n, Path.Combine(nested.DownloadRoot, version));
            return (Package: nested.Owned(version, downloads: [asset]), Asset: asset);
        }).ToArray();
        var unrecorded = nested.Write(releases[0].Asset.Root, "keep-user-file.txt", "never registered");
        nested.Run(releases[2].Package.Path);
        Missing(releases[0].Package.Path, releases[0].Asset.Path, releases[0].Asset.Path + DownloadManifestSuffix);
        Exists(releases[0].Asset.Root); // no ownership of the release cache directory itself
        foreach (var path in new[] { releases[1].Asset.Path, releases[2].Asset.Path, unrecorded })
            Check(File.Exists(path), "per-release cleanup removed an unrecorded/current/previous asset");

        var linked = new Fixture(root, "download-reparse");
        var linkedAsset = linked.Cache("owned.zip", "owned bytes");
        var linkedOld = linked.Owned("v1.0.0", downloads: [linkedAsset]);
        var linkedPrevious = linked.Owned("v2.0.0");
        var linkedCurrent = linked.Owned("v3.0.0");
        result = Run(linked.Request(linkedCurrent.Path), hooks: linked.Hooks(attributes: path => File.GetAttributes(path) |
            (PathIdentity.Equals(path, linkedAsset.Path) ? FileAttributes.ReparsePoint : 0)));
        Missing(linkedOld.Path);
        Exists(linkedPrevious.Path, linkedCurrent.Path);
        Check(File.Exists(linkedAsset.Path) && Has(result.Blocked, linkedAsset.Path), "injected download reparse path was followed/deleted");

        var registration = new Fixture(root, "registration-checksum");
        var mismatched = registration.Cache("unverified.zip", "original bytes");
        var package = registration.Manual("v1.0.0-sm86");
        var manifest = registration.Manifest(package, "v1.0.0", "sm86", [mismatched with { Sha256 = new string('0', 64) }]);
        var warnings = WriteOwnedManifest(registration.EngineRoot, manifest, registration.DownloadRoot);
        Equal(1, warnings.Count);
        Check(!File.Exists(mismatched.Path + DownloadManifestSuffix), "registration owned an asset before verifying its actual SHA");
        var wrongLength = registration.Cache("length.zip", "exact bytes");
        package = registration.Manual("v2.0.0-sm86");
        warnings = WriteOwnedManifest(registration.EngineRoot, registration.Manifest(package, "v2.0.0", "sm86", [wrongLength with { Length = wrongLength.Length + 1 }]), registration.DownloadRoot);
        Equal(1, warnings.Count);
        Check(!File.Exists(wrongLength.Path + DownloadManifestSuffix), "registration owned an asset with the wrong length");
    }

    public static void RunFailuresAndCancellation(string root)
    {
        var f = new Fixture(root, "failure");
        var old = f.Owned("v1.0.0");
        var otherOld = f.Owned("v2.0.0");
        var previous = f.Owned("v3.0.0");
        var current = f.Owned("v4.0.0");
        var activeMarker = File.ReadAllText(Path.Combine(current.Path, ManifestFileName));
        var warnings = new List<string>();
        var result = Run(f.Request(current.Path), warnings.Add, hooks: f.Hooks(deleteDirectory: path =>
        {
            if (PathIdentity.Equals(path, old.Path)) throw new IOException("fixture locked old package");
            f.DeleteDirectory(path);
        }));
        Exists(current.Path, previous.Path, old.Path);
        Missing(otherOld.Path);
        Equal(activeMarker, File.ReadAllText(Path.Combine(current.Path, ManifestFileName)));
        Check(Has(result.Blocked, old.Path, "cleanup-failed") && warnings.Any(s => s.Contains("fixture locked old package")), "cleanup failure was not returned/logged");

        var cacheFailure = new Fixture(root, "cache-failure-retry");
        var cache = cacheFailure.Cache("old.zip", "owned old archive");
        var cacheOld = cacheFailure.Owned("v1.0.0", downloads: [cache]);
        var cachePrevious = cacheFailure.Owned("v2.0.0");
        var cacheCurrent = cacheFailure.Owned("v3.0.0");
        result = Run(cacheFailure.Request(cacheCurrent.Path), hooks: cacheFailure.Hooks(deleteFile: path =>
        {
            if (PathIdentity.Equals(path, cache.Path)) throw new UnauthorizedAccessException("fixture locked cache");
            cacheFailure.DeleteFile(path);
        }));
        Missing(cacheOld.Path);
        Exists(cachePrevious.Path, cacheCurrent.Path);
        Check(File.Exists(cache.Path) && File.Exists(cache.Path + DownloadManifestSuffix), "failed cache cleanup lost its retry ownership receipt");
        Check(Has(result.Blocked, cache.Path, "download-cleanup-failed"), "failed cache cleanup was not returned");
        cacheFailure.Run(cacheCurrent.Path);
        Check(!File.Exists(cache.Path) && !File.Exists(cache.Path + DownloadManifestSuffix), "orphaned owned cache was not retried on the next call");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var filesystemCalls = 0;
        result = Run(f.Request(current.Path), ct: cancelled.Token, hooks: f.Hooks(attributes: path =>
        {
            filesystemCalls++;
            return File.GetAttributes(path);
        }));
        Equal(0, filesystemCalls);
        Check(result.Cancelled && result.Pruned.Count == 0, "pre-cancelled retention touched the filesystem");
        Equal(activeMarker, File.ReadAllText(Path.Combine(current.Path, ManifestFileName)));

        var during = new Fixture(root, "mid-cancel");
        var firstOld = during.Owned("v1.0.0");
        var secondOld = during.Owned("v2.0.0");
        var rollback = during.Owned("v3.0.0");
        var committed = during.Owned("v4.0.0");
        using var midway = new CancellationTokenSource();
        result = Run(during.Request(committed.Path), ct: midway.Token, hooks: during.Hooks(deleteDirectory: path =>
        {
            during.DeleteDirectory(path);
            midway.Cancel();
        }));
        Missing(firstOld.Path);
        Exists(secondOld.Path, rollback.Path, committed.Path);
        Check(result.Cancelled && result.Pruned.Count == 1, "mid-cleanup cancellation was thrown away or rolled back committed installation");
        result = Run(during.Request(committed.Path), _ => throw new IOException("fixture log sink failed"), hooks: during.Hooks());
        Missing(secondOld.Path);
        Exists(rollback.Path, committed.Path);
        Check(Has(result.Pruned, secondOld.Path), "a throwing log sink aborted maintenance/installation");
    }

    private static bool Has(IEnumerable<RetentionEntry> entries, string path, string? reason = null) =>
        entries.Any(e => PathIdentity.Equals(path, e.Path) && (reason == null || e.Reason.StartsWith(reason, StringComparison.Ordinal)));
    private static void Exists(params string[] paths)
    {
        foreach (var path in paths) Check(Directory.Exists(path), "protected/retained directory disappeared: " + path);
    }
    private static void Missing(params string[] paths)
    {
        foreach (var path in paths) Check(!Directory.Exists(path) && !File.Exists(path), "old owned path was not pruned: " + path);
    }
    private sealed record PackageFixture(string Path, OwnershipManifest Manifest);

    private sealed class Fixture
    {
        public string Work { get; }
        public string EngineRoot { get; }
        public string DownloadRoot { get; }
        private int _sequence;
        private readonly string _backend;

        public Fixture(string root, string label, string backend = "kvmem")
        {
            var full = Path.GetFullPath(root);
            var name = Path.GetFileName(full);
            Check(PathIdentity.Equals(full, ConfigService.GetManagerRoot()) && name.StartsWith("regression-", StringComparison.Ordinal) &&
                Guid.TryParseExact(name["regression-".Length..], "N", out _), "retention tests require the console runner's isolated root; never target a real installation");
            Work = Path.Combine(full, "engine-retention", label + "-" + Guid.NewGuid().ToString("N"));
            _backend = backend;
            EngineRoot = Path.Combine(Work, "engines", backend);
            DownloadRoot = Path.Combine(Work, "downloads", backend);
            Directory.CreateDirectory(EngineRoot);
            Directory.CreateDirectory(DownloadRoot);
        }

        public PackageFixture Owned(string version, string architecture = "sm86", bool retained = false,
            OwnedDownload[]? downloads = null, string? suffix = null, (string RelativePath, string Text)[]? shippedLogs = null)
        {
            var path = Manual(version + "-" + architecture + (suffix == null ? "" : "-" + suffix));
            var inventory = (shippedLogs ?? []).Select(log =>
            {
                var file = Write(path, log.RelativePath, log.Text);
                return new OwnedPackageFile(log.RelativePath, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(), new FileInfo(file).Length);
            }).ToArray();
            var manifest = Manifest(path, version, architecture, downloads ?? []) with { ExplicitlyRetained = retained, ShippedLogs = inventory };
            Equal(0, WriteOwnedManifest(EngineRoot, manifest, DownloadRoot).Count);
            return new(path, manifest);
        }

        public OwnershipManifest Manifest(string path, string version, string architecture, IReadOnlyList<OwnedDownload> downloads) => new()
        {
            BackendId = _backend, Version = version, Architecture = architecture, Root = Path.GetFullPath(path),
            InstalledAtUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(_sequence++), Downloads = downloads,
        };

        public string Manual(string name)
        {
            var path = Path.Combine(EngineRoot, name);
            Write(path, "engine.exe", "fixture engine, not executable");
            return path;
        }

        public OwnedDownload Cache(string name, string contents, string? cacheRoot = null)
        {
            var cache = cacheRoot ?? DownloadRoot;
            var path = Write(cache, name, contents);
            return new(Path.GetFullPath(cache), path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), new FileInfo(path).Length);
        }

        public string Write(string directory, string relative, string text)
        {
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            AssertInside(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        public void Rewrite(PackageFixture package, OwnershipManifest manifest) => Write(package.Path, ManifestFileName, JsonSerializer.Serialize(manifest, Json));
        public RetentionRequest Request(string current) => new(EngineRoot, _backend) { CurrentRoots = [current], DownloadRoot = DownloadRoot };
        public RetentionResult Run(string current) => EngineVersionRetention.Run(Request(current), hooks: Hooks());

        public RetentionHooks Hooks(Func<string, FileAttributes>? attributes = null, Action<string>? deleteDirectory = null,
            Action<string>? deleteFile = null, Func<int, ProcessIdentity?>? probe = null) => new()
        {
            GetAttributes = attributes ?? File.GetAttributes,
            DeleteDirectory = deleteDirectory ?? DeleteDirectory,
            DeleteFile = deleteFile ?? DeleteFile,
            ProbeProcess = probe ?? (_ => null), // Never query a real PID from fixture tests.
        };

        public void DeleteDirectory(string path) { AssertInside(path); Directory.Delete(path, recursive: true); }
        public void DeleteFile(string path) { AssertInside(path); File.Delete(path); }
        private void AssertInside(string path) => Check(Path.GetFullPath(path).StartsWith(Work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "fixture filesystem operation escaped its isolated work root");
    }
}
