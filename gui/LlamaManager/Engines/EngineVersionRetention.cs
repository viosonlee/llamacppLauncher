using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaManager.Engines;

/// <summary>
/// Prunes only installer-recorded, direct-child packages. The caller must serialize this with
/// installation and pass its latest current/recoverable preset roots and running-session records.
/// Run is post-commit best-effort maintenance, never an installation/configuration transaction.
/// </summary>
public static class EngineVersionRetention
{
    public const string ManifestFileName = ".llama-manager-owned.json";
    public const string DownloadManifestSuffix = ".llama-manager-owned.json";
    public const string Owner = "LlamaManager";
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public sealed record OwnedDownload(string Root, string Path, string Sha256, long Length);
    public sealed record OwnedPackageFile(string RelativePath, string Sha256, long Length);

    public sealed record OwnershipManifest
    {
        public int SchemaVersion { get; init; } = 1;
        public string Owner { get; init; } = EngineVersionRetention.Owner;
        public string BackendId { get; init; } = "";
        public string Version { get; init; } = "";
        /// <summary>Actual package variant, e.g. sm86, cuda13.4 or x64. GPU families stay separate; llama CUDA minors share retention.</summary>
        public string Architecture { get; init; } = "";
        public string Root { get; init; } = "";
        public DateTimeOffset InstalledAtUtc { get; init; }
        public bool ExplicitlyRetained { get; init; }
        public IReadOnlyList<OwnedDownload> Downloads { get; init; } = [];
        /// <summary>Explicit inventory from verified release extraction before first launch, never discovered/adopted here.</summary>
        public IReadOnlyList<OwnedPackageFile> ShippedLogs { get; init; } = [];
    }

    public sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);

    /// <summary>ReferencedRoots covers a bundled worker/serve root when the recorded process is Python.</summary>
    public sealed record RunningSessionReference(int ProcessId, long StartTimeUtcTicks, string ExecutablePath)
    {
        public IReadOnlyList<string> ReferencedRoots { get; init; } = [];
    }

    public sealed record RetentionRequest(string EngineRoot, string BackendId)
    {
        public IReadOnlyList<string> CurrentRoots { get; init; } = [];
        /// <summary>Include explicit packages, recoverable presets and any user data located inside a package.</summary>
        public IReadOnlyList<string> ProtectedRoots { get; init; } = [];
        public IReadOnlyList<RunningSessionReference> RunningSessions { get; init; } = [];
        public string? DownloadRoot { get; init; }
    }

    /// <summary>Per-call test seams; null process means definitely absent, exceptions mean unknown/protect.</summary>
    public sealed class RetentionHooks
    {
        public Func<string, FileAttributes> GetAttributes { get; init; } = File.GetAttributes;
        public Action<string> DeleteDirectory { get; init; } = path => Directory.Delete(path, recursive: true);
        public Action<string> DeleteFile { get; init; } = File.Delete;
        public Func<int, ProcessIdentity?> ProbeProcess { get; init; } = ReadProcessIdentity;
    }

    public sealed record RetentionEntry(string Path, string Reason, bool Download = false);

    public sealed class RetentionResult
    {
        public List<RetentionEntry> Kept { get; } = [];
        public List<RetentionEntry> Pruned { get; } = [];
        public List<RetentionEntry> Blocked { get; } = [];
        public List<RetentionEntry> Skipped { get; } = [];
        public bool Cancelled { get; internal set; }
    }

    private sealed record Package(string Root, OwnershipManifest Manifest, string Text);
    private sealed record CachedAsset(string ReceiptPath, OwnershipManifest Manifest, OwnedDownload Download, string Text);

    /// <summary>
    /// Only the installer calls this for a newly created final package, never to adopt a manual root.
    /// Existing markers are not overwritten. Cache receipts are created only after independent SHA/length
    /// verification inside the supplied DownloadRoot; failures are warnings, not ownership of that asset.
    /// ShippedLogs is supplied explicitly from verified release extraction; only those entries are verified,
    /// never automatically discovered or adopted. Its verification errors also fail before the marker/commit.
    /// The return value contains cache-registration warnings; package-marker errors still fail before commit.
    /// </summary>
    public static IReadOnlyList<RetentionEntry> WriteOwnedManifest(string engineRoot, OwnershipManifest manifest,
        string? downloadRoot = null, Action<string>? log = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = Canonical(engineRoot);
        ValidateManifest(manifest, root, manifest.BackendId);
        var hooks = new RetentionHooks();
        EnsureNoReparseAncestors(manifest.Root, hooks);
        if ((hooks.GetAttributes(manifest.Root) & FileAttributes.Directory) == 0)
            throw new IOException("Ownership root is not a directory.");
        foreach (var shipped in manifest.ShippedLogs)
            EnsureOriginalShippedLog(manifest.Root, shipped, hooks, ct);
        var marker = Path.Combine(manifest.Root, ManifestFileName);
        // CreateNew prevents an archive/manual marker from silently being taken over.
        using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, manifest, Json);
        var warnings = new List<RetentionEntry>();
        foreach (var asset in manifest.Downloads)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (downloadRoot == null) throw new IOException("No authorized download root supplied.");
                var authorized = Canonical(downloadRoot);
                ValidateDownload(asset, authorized, manifest.Version);
                EnsureRegularFile(asset.Path, hooks);
                if (new FileInfo(asset.Path).Length != asset.Length ||
                    !Paths.Equals(HashFile(asset.Path, ct), asset.Sha256))
                    throw new IOException("Download SHA-256/length does not match its ownership record.");
                var receiptPath = asset.Path + DownloadManifestSuffix;
                var receipt = manifest with { Downloads = [asset] };
                if (File.Exists(receiptPath))
                {
                    EnsureRegularFile(receiptPath, hooks);
                    var existing = ReadManifest(receiptPath).Manifest;
                    ValidateManifest(existing, root, manifest.BackendId);
                    if (existing.Downloads.Count != 1 || !SameDownload(existing.Downloads[0], asset) ||
                        !Paths.Equals(existing.Version, manifest.Version) || !Paths.Equals(existing.Architecture, manifest.Architecture))
                        throw new IOException("Existing download receipt has a different or unverifiable owner.");
                    // Same-version repair can share the already verified asset; kept manifests protect it.
                    continue;
                }
                using var stream = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(stream, receipt, Json);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var warning = new RetentionEntry(asset.Path, "download-registration-skipped: " + ex.Message, true);
                warnings.Add(warning);
                WriteLog(log, warning);
            }
        }
        return warnings;
    }

    /// <summary>
    /// Keeps current + newest unprotected previous package per backend/GPU group (or the newest two
    /// when that group has no configured current). llama CUDA minors share one group, not one per runtime.
    /// Extra protections do not consume the previous slot.
    /// Unknown ownership is skipped; unsafe/unreadable/protected paths and cleanup failures are blocked.
    /// Cancellation stops maintenance and is returned, never thrown back to a committed installer.
    /// </summary>
    public static RetentionResult Run(RetentionRequest request, Action<string>? log = null,
        CancellationToken ct = default, RetentionHooks? hooks = null)
    {
        var result = new RetentionResult();
        hooks ??= new RetentionHooks();
        try
        {
            ct.ThrowIfCancellationRequested();
            var engineRoot = Canonical(request.EngineRoot);
            if (!ValidKey(request.BackendId)) throw new IOException("Invalid backend id.");
            EnsureNoReparseAncestors(engineRoot, hooks);
            if ((hooks.GetAttributes(engineRoot) & FileAttributes.Directory) == 0)
                throw new IOException("Authorized engine root is not a directory.");
            var current = NormalizeReferences(request.CurrentRoots, hooks);
            var protections = NormalizeReferences(request.ProtectedRoots, hooks);
            AddRunningProtections(request.RunningSessions, protections, hooks, ct);
            var packages = ReadPackages(engineRoot, request.BackendId, result, log, hooks, ct);
            var remove = new List<Package>();
            foreach (var group in packages.GroupBy(p => RetentionGroup(p.Manifest), Paths))
            {
                var ordered = group.OrderByDescending(p => p, PackageOrder.Instance).ToArray();
                var anchors = ordered.Where(p => current.Any(r => Overlaps(p.Root, r))).ToArray();
                if (anchors.Length == 0) anchors = [ordered[0]];
                var previous = ordered.FirstOrDefault(p => !anchors.Contains(p) && !IsProtected(p, protections));
                foreach (var package in ordered)
                {
                    if (anchors.Contains(package)) Add(result.Kept, package.Root, "current", log);
                    else if (IsProtected(package, protections)) Add(result.Blocked, package.Root, "protected-root/session/explicit-package", log);
                    else if (package == previous) Add(result.Kept, package.Root, "newest-previous", log);
                    else remove.Add(package);
                }
            }
            // Oldest first: interrupted maintenance still leaves the best rollback candidate untouched.
            foreach (var package in remove.OrderBy(p => p, PackageOrder.Instance))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var latestProtections = new List<string>(protections);
                    AddRunningProtections(request.RunningSessions, latestProtections, hooks, ct);
                    if (IsProtected(package, latestProtections) || current.Any(r => Overlaps(package.Root, r)))
                        throw new IOException("Package became protected before cleanup.");
                    EnsureUnchangedPackage(package, engineRoot, request.BackendId, hooks);
                    EnsureSafeTree(package, hooks, ct);
                    // Revalidate the marker and ancestors after the full, non-following preflight.
                    EnsureUnchangedPackage(package, engineRoot, request.BackendId, hooks);
                    ct.ThrowIfCancellationRequested();
                    hooks.DeleteDirectory(package.Root);
                    Add(result.Pruned, package.Root, "old-owned-package", log);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Add(result.Blocked, package.Root, "cleanup-failed: " + ex.Message, log); }
            }
            if (request.DownloadRoot != null)
                PruneDownloads(Canonical(request.DownloadRoot), engineRoot, request, packages, current, protections, result, log, hooks, ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            Add(result.Blocked, request.EngineRoot, "cancelled: committed installation is unchanged", log);
        }
        catch (Exception ex) { Add(result.Blocked, request.EngineRoot, "retention-unavailable: " + ex.Message, log); }
        return result;
    }

    private static List<Package> ReadPackages(string engineRoot, string backend, RetentionResult result,
        Action<string>? log, RetentionHooks hooks, CancellationToken ct)
    {
        var packages = new List<Package>();
        foreach (var directory in Directory.EnumerateDirectories(engineRoot, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var path = Canonical(directory);
            try
            {
                EnsureNoReparseAncestors(path, hooks);
                var marker = Path.Combine(path, ManifestFileName);
                if (!File.Exists(marker)) { Add(result.Skipped, path, "not-launcher-owned", log); continue; }
                EnsureRegularFile(marker, hooks);
                var data = ReadManifest(marker);
                ValidateManifest(data.Manifest, engineRoot, backend);
                if (!Paths.Equals(data.Manifest.Root, path)) throw new InvalidDataException("Manifest root does not match its directory.");
                if (File.Exists(Path.Combine(path, KvmemInstallation.PendingMarker)))
                {
                    Add(result.Blocked, path, "pending-installation", log);
                    continue;
                }
                packages.Add(new(path, data.Manifest, data.Text));
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException ex) { Add(result.Skipped, path, "invalid-ownership: " + ex.Message, log); }
            catch (JsonException ex) { Add(result.Skipped, path, "invalid-ownership: " + ex.Message, log); }
            catch (Exception ex) { Add(result.Blocked, path, "unreadable/unsafe-package: " + ex.Message, log); }
        }
        return packages;
    }

    private static void PruneDownloads(string downloadRoot, string engineRoot, RetentionRequest request,
        List<Package> packages, List<string> current, List<string> protections, RetentionResult result,
        Action<string>? log, RetentionHooks hooks, CancellationToken ct)
    {
        EnsureNoReparseAncestors(downloadRoot, hooks);
        if ((hooks.GetAttributes(downloadRoot) & FileAttributes.Directory) == 0)
            throw new IOException("Authorized download root is not a directory.");
        var receipts = new List<CachedAsset>();
        foreach (var receiptPath in FindDownloadReceipts(downloadRoot, result, log, hooks, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                EnsureRegularFile(receiptPath, hooks);
                var data = ReadManifest(receiptPath);
                ValidateManifest(data.Manifest, engineRoot, request.BackendId);
                if (data.Manifest.Downloads.Count != 1) throw new InvalidDataException("Download receipt must record exactly one asset.");
                var asset = data.Manifest.Downloads[0];
                ValidateDownload(asset, downloadRoot, data.Manifest.Version);
                if (!Paths.Equals(Canonical(receiptPath), asset.Path + DownloadManifestSuffix))
                    throw new InvalidDataException("Receipt path does not match its recorded asset.");
                receipts.Add(new(receiptPath, data.Manifest, asset, data.Text));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Add(result.Skipped, receiptPath, "invalid/unowned-download-receipt: " + ex.Message, log, true); }
        }
        var prunedRoots = result.Pruned.Where(e => !e.Download).Select(e => e.Path).ToHashSet(Paths);
        var remaining = packages.Where(p => !prunedRoots.Contains(p.Root)).ToArray();
        foreach (var group in receipts.GroupBy(r => RetentionGroup(r.Manifest), Paths))
        {
            var retainedVersions = remaining.Where(p => Paths.Equals(RetentionGroup(p.Manifest), group.Key))
                .Select(p => p.Manifest.Version).ToHashSet(Paths);
            // Receipts survive failed cleanup, so a subsequent call can retry an orphaned cache.
            // Without any installed owned package, keep the newest two recorded releases for recovery.
            if (retainedVersions.Count == 0)
                foreach (var version in group.Select(r => r.Manifest.Version).Distinct(Paths)
                    .OrderByDescending(v => v, VersionOrder.Instance).Take(2)) retainedVersions.Add(version);
            foreach (var cached in group)
            {
                ct.ThrowIfCancellationRequested();
                var asset = cached.Download;
                try
                {
                    var latestProtections = new List<string>(protections);
                    AddRunningProtections(request.RunningSessions, latestProtections, hooks, ct);
                    if (current.Any(r => Overlaps(cached.Manifest.Root, r) || Overlaps(asset.Path, r)))
                    {
                        Add(result.Kept, asset.Path, "current-download", log, true);
                        continue;
                    }
                    if (latestProtections.Any(r => Overlaps(cached.Manifest.Root, r) || Overlaps(asset.Path, r)) ||
                        cached.Manifest.ExplicitlyRetained)
                    {
                        Add(result.Blocked, asset.Path, "protected-download", log, true);
                        continue;
                    }
                    if (retainedVersions.Contains(cached.Manifest.Version) ||
                        remaining.Any(p => p.Manifest.Downloads.Any(d => SameDownload(d, asset))) ||
                        DirectoryStillExists(cached.Manifest.Root, hooks))
                    {
                        Add(result.Kept, asset.Path, "retained-package-download", log, true);
                        continue;
                    }
                    // All targets (including the sidecar) stay within the same explicit authorization.
                    ValidateDownload(asset, downloadRoot, cached.Manifest.Version);
                    EnsureRegularFile(cached.ReceiptPath, hooks);
                    if (!string.Equals(ReadManifest(cached.ReceiptPath).Text, cached.Text, StringComparison.Ordinal))
                        throw new IOException("Download ownership changed during cleanup.");
                    if (FileStillExists(asset.Path, hooks))
                    {
                        EnsureRegularFile(asset.Path, hooks);
                        if (new FileInfo(asset.Path).Length != asset.Length || !Paths.Equals(HashFile(asset.Path, ct), asset.Sha256))
                            throw new IOException("Download SHA-256/length changed; it may now be a user file.");
                        EnsureRegularFile(asset.Path, hooks);
                        EnsureRegularFile(cached.ReceiptPath, hooks);
                        if (!string.Equals(ReadManifest(cached.ReceiptPath).Text, cached.Text, StringComparison.Ordinal))
                            throw new IOException("Download ownership changed after hashing.");
                        ct.ThrowIfCancellationRequested();
                        hooks.DeleteFile(asset.Path);
                        Add(result.Pruned, asset.Path, "old-owned-download", log, true);
                    }
                    ct.ThrowIfCancellationRequested();
                    EnsureRegularFile(cached.ReceiptPath, hooks);
                    hooks.DeleteFile(cached.ReceiptPath);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Add(result.Blocked, asset.Path, "download-cleanup-failed: " + ex.Message, log, true); }
            }
        }
    }

    private static List<string> FindDownloadReceipts(string root, RetentionResult result, Action<string>? log,
        RetentionHooks hooks, CancellationToken ct)
    {
        var directories = new List<string> { root };
        // Flat KVMem cache and one-level per-release stock-engine caches. Never follow a junction
        // or recursively discover arbitrary models/user directories under the download root.
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            try { EnsureNoReparseAncestors(directory, hooks); directories.Add(directory); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Add(result.Blocked, directory, "unsafe-download-directory: " + ex.Message, log, true); }
        }
        var paths = new List<string>();
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            try { paths.AddRange(Directory.EnumerateFiles(directory, "*" + DownloadManifestSuffix, SearchOption.TopDirectoryOnly)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Add(result.Blocked, directory, "unreadable-download-directory: " + ex.Message, log, true); }
        }
        return paths;
    }

    private static (OwnershipManifest Manifest, string Text) ReadManifest(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Ownership manifest is too large.");
        var text = File.ReadAllText(path);
        using var document = JsonDocument.Parse(text);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object ||
            new[] { "schemaVersion", "owner", "backendId", "version", "architecture", "root", "installedAtUtc", "downloads" }
                .Any(key => !value.TryGetProperty(key, out _)) ||
            value.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new InvalidDataException("Ownership manifest is incomplete or has duplicate properties.");
        return (JsonSerializer.Deserialize<OwnershipManifest>(text, Json) ?? throw new InvalidDataException("Empty ownership manifest."), text);
    }

    private static void ValidateManifest(OwnershipManifest manifest, string engineRoot, string backend)
    {
        if (manifest.SchemaVersion != 1 || manifest.Owner != Owner || !ValidKey(manifest.BackendId) ||
            !Paths.Equals(manifest.BackendId, backend) || !Paths.Equals(Path.GetFileName(engineRoot), backend) || !ValidKey(manifest.Architecture) ||
            !ValidVersion(manifest.Version) || manifest.InstalledAtUtc == default || manifest.InstalledAtUtc.Offset != TimeSpan.Zero ||
            manifest.Downloads == null || manifest.Downloads.Count > 32 || manifest.ShippedLogs == null || manifest.ShippedLogs.Count > 1024)
            throw new InvalidDataException("Invalid ownership schema/backend/version/architecture/timestamp.");
        RequireCanonical(manifest.Root);
        if (!DirectChild(manifest.Root, engineRoot)) throw new InvalidDataException("Ownership root is not a direct child of the authorized engine root.");
        ValidateDirectoryIdentity(manifest);
        foreach (var asset in manifest.Downloads)
        {
            if (asset == null) throw new InvalidDataException("Null download record.");
            ValidateDownload(asset, Canonical(asset.Root));
        }
        var logPaths = new HashSet<string>(Paths);
        foreach (var shipped in manifest.ShippedLogs)
            if (!logPaths.Add(ShippedLogPath(manifest.Root, shipped)))
                throw new InvalidDataException("Duplicate shipped-log ownership path.");
    }

    private static void ValidateDirectoryIdentity(OwnershipManifest manifest)
    {
        var prefix = manifest.Version.TrimStart('v', 'V') + "-";
        var name = Path.GetFileName(manifest.Root).TrimStart('v', 'V');
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Directory version does not match its ownership manifest.");
        var variant = name[prefix.Length..];
        var aliases = new List<string> { manifest.Architecture };
        // Catalog layout aliases, not GPU probes: retain an exact version + architecture identity.
        if (Paths.Equals(manifest.BackendId, "kvmem") && manifest.Architecture.StartsWith("sm", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(manifest.Architecture[2..], out var sm) && EngineCatalog.KvmemGpuFamily(sm) is { } family)
            aliases.Add("nvidia-" + family);
        if (Paths.Equals(manifest.BackendId, "llamacpp") && manifest.Architecture.StartsWith("cuda", StringComparison.OrdinalIgnoreCase))
            aliases.Add("cuda-" + manifest.Architecture[4..].TrimStart('-'));
        if (Paths.Equals(manifest.BackendId, "polystrata") && Paths.Equals(manifest.Architecture, "x64"))
            aliases.Add("cuda");
        if (!aliases.Any(alias => Paths.Equals(variant, alias) || variant.StartsWith(alias + "-", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Directory architecture does not match its ownership manifest.");
    }

    private static void ValidateDownload(OwnedDownload asset, string authorizedRoot, string? version = null)
    {
        RequireCanonical(asset.Root);
        RequireCanonical(asset.Path);
        var sameRoot = Paths.Equals(asset.Root, authorizedRoot);
        if ((!sameRoot && !DirectChild(asset.Root, authorizedRoot)) || !DirectChild(asset.Path, asset.Root) ||
            (!sameRoot && version != null && !Paths.Equals(Path.GetFileName(asset.Root).TrimStart('v', 'V'), version.TrimStart('v', 'V'))) ||
            asset.Length <= 0 || asset.Sha256 == null || !Regex.IsMatch(asset.Sha256, "^[0-9a-fA-F]{64}$") ||
            IsKnownUserFile(asset.Path) || Paths.Equals(Path.GetFileName(asset.Path), "config.json") ||
            asset.Path.EndsWith(DownloadManifestSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Download root/path/SHA-256/length is outside its recorded authorization or identifies user data.");
    }

    private static void EnsureUnchangedPackage(Package package, string engineRoot, string backend, RetentionHooks hooks)
    {
        EnsureNoReparseAncestors(package.Root, hooks);
        if (!DirectChild(package.Root, engineRoot)) throw new IOException("Package escaped the authorized engine root.");
        var marker = Path.Combine(package.Root, ManifestFileName);
        EnsureRegularFile(marker, hooks);
        var latest = ReadManifest(marker);
        ValidateManifest(latest.Manifest, engineRoot, backend);
        if (!Paths.Equals(latest.Manifest.Root, package.Root) || !string.Equals(latest.Text, package.Text, StringComparison.Ordinal))
            throw new IOException("Ownership manifest changed during cleanup.");
    }

    private static string ShippedLogPath(string root, OwnedPackageFile shipped)
    {
        if (shipped == null || string.IsNullOrWhiteSpace(shipped.RelativePath) || shipped.RelativePath.Length > 1024 ||
            Path.IsPathRooted(shipped.RelativePath) || !shipped.RelativePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            shipped.Sha256 == null || !Regex.IsMatch(shipped.Sha256, "^[0-9a-fA-F]{64}$") || shipped.Length < 0)
            throw new InvalidDataException("Invalid shipped-log relative path/SHA-256/length.");
        var relative = shipped.RelativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (relative.Split(Path.DirectorySeparatorChar).Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.Contains(':') || segment.EndsWith('.') || segment.EndsWith(' ')))
            throw new InvalidDataException("Shipped-log path contains traversal/ADS/ambiguous segments.");
        var path = Canonical(Path.Combine(root, relative));
        if (!Within(path, root)) throw new InvalidDataException("Shipped-log path escaped its package root.");
        return path;
    }

    private static void EnsureOriginalShippedLog(string root, OwnedPackageFile shipped, RetentionHooks hooks, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = ShippedLogPath(root, shipped);
        EnsureRegularFile(path, hooks);
        if (new FileInfo(path).Length != shipped.Length || !Paths.Equals(HashFile(path, ct), shipped.Sha256))
            throw new IOException("Shipped release log changed; retain possible user/runtime data: " + path);
        EnsureRegularFile(path, hooks);
        if (new FileInfo(path).Length != shipped.Length) throw new IOException("Shipped release log changed after verification: " + path);
    }

    private static void EnsureSafeTree(Package package, RetentionHooks hooks, CancellationToken ct)
    {
        var root = package.Root;
        var shippedLogs = package.Manifest.ShippedLogs.ToDictionary(shipped => ShippedLogPath(root, shipped), Paths);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                var path = Canonical(entry);
                if (!Within(path, root)) throw new IOException("Package member escaped its root.");
                var attributes = hooks.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Package contains a symlink/junction: " + path);
                var relative = Path.GetRelativePath(root, path);
                var directoryEntry = (attributes & FileAttributes.Directory) != 0;
                var logFile = path.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
                if (logFile)
                {
                    if (directoryEntry || !shippedLogs.TryGetValue(path, out var shipped))
                        throw new IOException("Package contains an unrecorded user/runtime log: " + path);
                    EnsureOriginalShippedLog(root, shipped, hooks, ct);
                }
                var insideLogDirectory = Paths.Equals(relative, "logs") || Within(path, Path.Combine(root, "logs"));
                if (insideLogDirectory && (directoryEntry ? !shippedLogs.Keys.Any(log => Within(log, path)) : !logFile))
                    throw new IOException("Package contains unrecorded log-directory data: " + path);
                // Only hash-verified, explicitly shipped logs are exempt; model/config protection is unchanged.
                if ((IsKnownUserFile(path) && !logFile) || Paths.Equals(relative, "config.json") || Paths.Equals(relative, "models"))
                    throw new IOException("Package contains model/config/log/user data; supply protectedRoots or retain it: " + path);
                if (directoryEntry) pending.Push(path);
            }
        }
    }

    private static List<string> NormalizeReferences(IEnumerable<string> roots, RetentionHooks hooks)
    {
        var result = new List<string>();
        foreach (var value in roots)
        {
            var path = Canonical(value);
            EnsureNoReparseAncestors(path, hooks, allowMissing: true);
            result.Add(path);
        }
        return result;
    }

    private static void AddRunningProtections(IEnumerable<RunningSessionReference> sessions, List<string> protections,
        RetentionHooks hooks, CancellationToken ct)
    {
        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (session.ProcessId <= 0) continue;
            var unknown = false;
            ProcessIdentity? actual;
            try { actual = hooks.ProbeProcess(session.ProcessId); }
            catch (Exception) { actual = null; unknown = true; }
            var verified = actual != null && actual.ProcessId == session.ProcessId && session.StartTimeUtcTicks > 0 &&
                actual.StartTimeUtcTicks == session.StartTimeUtcTicks &&
                Paths.Equals(Canonical(actual.ExecutablePath), Canonical(session.ExecutablePath));
            if (verified || unknown)
                protections.AddRange(NormalizeReferences(new[] { session.ExecutablePath }.Concat(session.ReferencedRoots), hooks));
            // A reused PID must not protect its stale recorded path. Its observed executable, if any,
            // is nevertheless a real live file and must not be removed by this filesystem maintenance.
            if (actual != null) protections.AddRange(NormalizeReferences([actual.ExecutablePath], hooks));
        }
    }

    private static ProcessIdentity? ReadProcessIdentity(int processId)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return null; }
        using (process)
        {
            if (process.HasExited) return null;
            var executable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable)) throw new IOException("Cannot verify the recorded process executable.");
            return new(process.Id, process.StartTime.ToUniversalTime().Ticks, executable);
        }
    }

    private static void EnsureRegularFile(string path, RetentionHooks hooks)
    {
        EnsureNoReparseAncestors(path, hooks);
        if ((hooks.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("Expected a regular file: " + path);
    }

    private static void EnsureNoReparseAncestors(string path, RetentionHooks hooks, bool allowMissing = false)
    {
        for (string? current = Canonical(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((hooks.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Path contains a symlink/junction: " + current);
            }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
    }

    private static bool DirectoryStillExists(string path, RetentionHooks hooks)
    {
        try { return (hooks.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool FileStillExists(string path, RetentionHooks hooks)
    {
        try
        {
            if ((hooks.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("Recorded asset is now a directory.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new InvalidDataException("Path must be a normal absolute filesystem path.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full)!;
        if (Paths.Equals(full, root)) throw new InvalidDataException("A volume/share root cannot be authorized for retention.");
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            if (segment.Contains(':') || segment.EndsWith('.') || segment.EndsWith(' '))
                throw new InvalidDataException("Path contains an ADS or ambiguous trailing dot/space.");
        return full;
    }

    private static void RequireCanonical(string path)
    {
        var canonical = Canonical(path);
        if (!Paths.Equals(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar), canonical))
            throw new InvalidDataException("Ownership path is not canonical.");
    }

    // CUDA minor is a llama runtime version, not a GPU family: never accumulate two copies per minor.
    private static string RetentionGroup(OwnershipManifest manifest) =>
        Paths.Equals(manifest.BackendId, "llamacpp") && manifest.Architecture.StartsWith("cuda", StringComparison.OrdinalIgnoreCase)
            ? "win-x64-cuda" : manifest.Architecture;
    private static bool DirectChild(string path, string root) => Paths.Equals(Path.GetDirectoryName(path), root);
    private static bool Within(string path, string root) => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Overlaps(string a, string b) => Paths.Equals(a, b) || Within(a, b) || Within(b, a);
    private static bool IsProtected(Package package, List<string> roots) => package.Manifest.ExplicitlyRetained || roots.Any(r => Overlaps(package.Root, r));
    private static bool SameDownload(OwnedDownload a, OwnedDownload b) => Paths.Equals(a.Root, b.Root) && Paths.Equals(a.Path, b.Path) && Paths.Equals(a.Sha256, b.Sha256) && a.Length == b.Length;
    private static bool IsKnownUserFile(string path) =>
        new[] { ".gguf", ".ninfer", ".safetensors", ".log" }.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ||
        Paths.Equals(Path.GetFileName(path), "llama-manager.config.json") || Paths.Equals(Path.GetFileName(path), "llama-manager.config.json.bak");
    private static bool ValidKey(string value) => value != null && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$");
    private static bool ValidVersion(string value) => value != null && value.Length <= 128 && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9.+_-]*$") && value.Any(char.IsDigit);

    private static string HashFile(string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[128 * 1024];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Add(List<RetentionEntry> entries, string path, string reason, Action<string>? log, bool download = false)
    {
        var entry = new RetentionEntry(path, reason, download);
        entries.Add(entry);
        WriteLog(log, entry);
    }

    private static void WriteLog(Action<string>? log, RetentionEntry entry)
    {
        try { log?.Invoke($"Engine retention [{entry.Reason}]: {entry.Path}"); }
        catch (Exception) { /* A warning sink must not roll back an already committed installation. */ }
    }

    private sealed class PackageOrder : IComparer<Package>
    {
        public static readonly PackageOrder Instance = new();
        public int Compare(Package? x, Package? y)
        {
            if (x == null || y == null) return x == y ? 0 : x == null ? -1 : 1;
            var compared = VersionOrder.Instance.Compare(x.Manifest.Version, y.Manifest.Version);
            if (compared == 0) compared = x.Manifest.InstalledAtUtc.CompareTo(y.Manifest.InstalledAtUtc);
            return compared != 0 ? compared : Paths.Compare(x.Root, y.Root);
        }
    }

    /// <summary>SemVer precedence for numeric tags; natural numeric ordering for llama bNNN/poly.N tags.</summary>
    private sealed class VersionOrder : IComparer<string>
    {
        public static readonly VersionOrder Instance = new();
        public int Compare(string? x, string? y)
        {
            if (x == null || y == null) return x == y ? 0 : x == null ? -1 : 1;
            var a = Regex.Match(x, @"^[vV]?(\d+(?:\.\d+)*)(?:-([A-Za-z0-9.-]+))?(?:\+[A-Za-z0-9.-]+)?$");
            var b = Regex.Match(y, @"^[vV]?(\d+(?:\.\d+)*)(?:-([A-Za-z0-9.-]+))?(?:\+[A-Za-z0-9.-]+)?$");
            if (a.Success && b.Success)
            {
                var core = Natural(a.Groups[1].Value, b.Groups[1].Value);
                if (core != 0) return core;
                if (a.Groups[2].Success != b.Groups[2].Success) return a.Groups[2].Success ? -1 : 1;
                return Natural(a.Groups[2].Value, b.Groups[2].Value);
            }
            return Natural(x.TrimStart('v', 'V'), y.TrimStart('v', 'V'));
        }

        private static int Natural(string a, string b)
        {
            var left = Regex.Matches(a, @"\d+|\D+");
            var right = Regex.Matches(b, @"\d+|\D+");
            for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
            {
                var x = left[i].Value;
                var y = right[i].Value;
                int compared;
                if (char.IsDigit(x[0]) && char.IsDigit(y[0]))
                {
                    x = x.TrimStart('0');
                    y = y.TrimStart('0');
                    compared = x.Length.CompareTo(y.Length);
                    if (compared == 0) compared = string.CompareOrdinal(x, y);
                }
                else compared = StringComparer.OrdinalIgnoreCase.Compare(x, y);
                if (compared != 0) return compared;
            }
            return left.Count.CompareTo(right.Count);
        }
    }
}
