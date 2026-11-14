using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LlamaManager.Engines;

/// <summary>One downloadable file from a GitHub release, with an optional known SHA-256.</summary>
public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

/// <summary>A GitHub release: its tag (used to fetch the matching source archive) and its assets.</summary>
public sealed record ReleaseInfo(string Tag, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// The download layout for one engine: which archive holds the binary, where it extracts, and
/// (when the engine's HTTP service is a Python module) which source archive carries <c>serve/</c>.
/// Pure data so the release-to-plan mapping is testable without a network.
/// </summary>
public sealed record EngineInstallPlan(
    string BackendId,
    IReadOnlyList<ReleaseAsset> EngineArchives,
    string EngineDir,
    ReleaseAsset? SourceArchive,
    string? SourceDir)
{
    public IReadOnlyList<ReleaseAsset> AllArchives =>
        SourceArchive == null ? EngineArchives : [.. EngineArchives, SourceArchive];

    /// <summary>KVMem unified zip metadata; other engine plans keep their existing extraction behavior.</summary>
    public int? KvmemArchitecture { get; init; }
    public ReleaseAsset? ChecksumAsset { get; init; }

    /// <summary>Managed acquisition uses immutable direct-child version roots; legacy plans retain their public layout.</summary>
    public string? PackageRoot { get; init; }
    public string? CudaVersion { get; init; }
    public string? Architecture { get; init; }
}

/// <summary>
/// Maps a GitHub release to the files an engine install needs. Every rule is a pure function of
/// the release metadata, so the mapping can be checked against real release JSON in tests.
/// </summary>
public static class EngineCatalog
{
    public const string LlamaCppRepo = "ggml-org/llama.cpp";
    // Canonical remote for new queries/source archives; installed provenance is historical, not rewritten.
    public const string PolyStrataRepo = "simplexai-labs/PolyStrata";
    public const string KvmemRepo = "kvmem/kvmem";

    /// <summary>Engine directories live under the manager root, one folder per backend id.</summary>
    public static string EngineRoot(string managerRoot, string backendId) =>
        Path.Combine(managerRoot, "engines", backendId);

    /// <summary>Strips GitHub's <c>sha256:&lt;hex&gt;</c> digest prefix, or returns null.</summary>
    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var value = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : digest;
        return Regex.IsMatch(value, "^[0-9a-fA-F]{64}$") ? value.ToLowerInvariant() : null;
    }

    /// <summary>Builds a <see cref="ReleaseInfo"/> from a GitHub release JSON object.</summary>
    public static ReleaseInfo ParseRelease(JsonNode release)
    {
        var tag = release["tag_name"]?.GetValue<string>() ?? "";
        var assets = new List<ReleaseAsset>();
        if (release["assets"] is JsonArray array)
            foreach (var asset in array)
            {
                var name = asset?["name"]?.GetValue<string>();
                var url = asset?["browser_download_url"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                assets.Add(new ReleaseAsset(name, url, ParseDigest(asset?["digest"]?.GetValue<string>())));
            }
        return new ReleaseInfo(tag, assets);
    }

    /// <summary>The source archive GitHub generates for a tag (serve/, requirements.txt, tools/).</summary>
    public static ReleaseAsset SourceArchive(string repo, string tag) =>
        new($"{tag}.zip", $"https://github.com/{repo}/archive/refs/tags/{tag}.zip", null);

    /// <summary>First asset whose name matches, or null.</summary>
    public static ReleaseAsset? Find(IReadOnlyList<ReleaseAsset> assets, string pattern) =>
        assets.FirstOrDefault(a => Regex.IsMatch(a.Name, pattern, RegexOptions.IgnoreCase));

    /// <summary>
    /// llama.cpp: a nightly Windows build. The CUDA major follows the GPU: official CUDA 13.4
    /// builds stopped shipping Turing/Volta SASS, so older cards (compute capability &lt; 8.0)
    /// get the CUDA 12.4 build, which still carries their kernels; newer cards get 13.4.
    /// The cudart zip carries the CUDA DLLs the binary loads, and both extract to the same
    /// folder as <c>llama-server.exe</c>.
    /// </summary>
    public static EngineInstallPlan? LlamaCppPlan(ReleaseInfo release, string managerRoot, double computeCapability = 0)
    {
        var cuda = computeCapability > 0 && computeCapability < 8.0 ? "12.4" : "13.4";
        var binary = Find(release.Assets, $@"^llama-.*-bin-win-cuda-{Regex.Escape(cuda)}-x64\.zip$");
        if (binary == null) return null;
        var archives = new List<ReleaseAsset> { binary };
        var cudart = Find(release.Assets, $@"^cudart-llama-bin-win-cuda-{Regex.Escape(cuda)}-x64\.zip$");
        if (cudart != null) archives.Add(cudart);
        // llama-server.exe sits at the manager root so ConfigService finds it as the llama.cpp root.
        return new EngineInstallPlan("llamacpp", archives, managerRoot, null, null);
    }

    /// <summary>
    /// Actual llama.cpp installations require a binary + cudart from the very same nightly
    /// release and CUDA minor, in a private version root. Upstream may advance CUDA between
    /// nightlies; select a complete pair the detected driver supports, not a hard-coded minor.
    /// Unknown drivers use only the conservative CUDA 12.4 pair. Pre-Ampere never selects 13.x.
    /// The legacy LlamaCppPlan remains a compatibility mapping, not an installation target.
    /// </summary>
    public static EngineInstallPlan? ManagedLlamaCppPlan(ReleaseInfo release, string managerRoot,
        double computeCapability, double maximumCudaVersion)
    {
        if (!BuiltInInstallation.IsComparableVersion("llamacpp", release.Tag)) return null;
        var candidates = new List<(ReleaseAsset Binary, ReleaseAsset Runtime, string Cuda, Version Version)>();
        foreach (var binary in release.Assets)
        {
            var match = Regex.Match(binary.Name, @"^llama-(b\d{1,18})-bin-win-cuda-(\d{1,2}\.\d{1,2})-x64\.zip$", RegexOptions.IgnoreCase);
            if (!match.Success || !match.Groups[1].Value.Equals(release.Tag, StringComparison.OrdinalIgnoreCase) ||
                !IsLlamaReleaseAsset(binary, release.Tag)) continue;
            var cuda = match.Groups[2].Value;
            if (!Version.TryParse(cuda, out var version)) continue;
            if (computeCapability > 0 && computeCapability < 8.0 && cuda != "12.4") continue;
            var maximum = double.IsFinite(maximumCudaVersion) && maximumCudaVersion > 0 ? maximumCudaVersion : 12.4;
            if (!Version.TryParse(maximum.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), out var driver) || version > driver) continue;
            // CUDA 12.4 and CUDA 13.x are the upstream Windows families with supported mappings.
            if (cuda != "12.4" && version.Major != 13) continue;
            var runtime = release.Assets.FirstOrDefault(a => IsLlamaReleaseAsset(a, release.Tag) &&
                (a.Name.Equals($"cudart-llama-bin-win-cuda-{cuda}-x64.zip", StringComparison.OrdinalIgnoreCase) ||
                 a.Name.Equals($"cudart-llama-{release.Tag}-bin-win-cuda-{cuda}-x64.zip", StringComparison.OrdinalIgnoreCase)));
            if (runtime != null) candidates.Add((binary, runtime, cuda, version));
        }
        var chosen = candidates.OrderByDescending(c => c.Version).FirstOrDefault();
        if (chosen.Binary == null) return null;
        var root = Path.Combine(EngineRoot(managerRoot, "llamacpp"), release.Tag + "-cuda-" + chosen.Cuda);
        return new EngineInstallPlan("llamacpp", [chosen.Binary, chosen.Runtime], root, null, null)
        { PackageRoot = root, CudaVersion = chosen.Cuda, Architecture = "cuda" + chosen.Cuda };
    }

    // Untagged cudart names repeat across releases: URL provenance must agree with the selected
    // release too. Managed upstream acquisition never treats an arbitrary mirror/fork as stock.
    private static bool IsLlamaReleaseAsset(ReleaseAsset asset, string tag) =>
        Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        Uri.UnescapeDataString(uri.AbsolutePath).Equals($"/{LlamaCppRepo}/releases/download/{tag}/{asset.Name}", StringComparison.Ordinal);

    /// <summary>PolyStrata's source, native ZIP and runtime pins must be from one release; CUDA is read from its BUILD stamp.</summary>
    public static EngineInstallPlan? ManagedPolyStrataPlan(ReleaseInfo release, string managerRoot)
    {
        if (!BuiltInInstallation.IsComparableVersion("polystrata", release.Tag)) return null;
        var legacy = PolyStrataPlan(release, managerRoot);
        if (legacy == null) return null;
        var root = Path.Combine(EngineRoot(managerRoot, "polystrata"), release.Tag + "-cuda");
        return legacy with { EngineDir = Path.Combine(root, "engine-poly"), SourceDir = root, PackageRoot = root, Architecture = "x64" };
    }

    /// <summary>
    /// Only architectures with dedicated upstream unified assets are eligible. Ampere sm80 and
    /// Hopper sm90 are intentionally not aliases for an RTX package; unknown GPUs must not guess.
    /// </summary>
    public static int? KvmemArchitecture(double computeCapability)
    {
        if (!double.IsFinite(computeCapability) || computeCapability <= 0) return null;
        var scaled = computeCapability * 10;
        var architecture = (int)Math.Round(scaled);
        if (Math.Abs(scaled - architecture) > 0.001) return null;
        return KvmemGpuFamily(architecture) != null ? architecture : null;
    }

    public static string? KvmemGpuFamily(int architecture) => architecture switch
    {
        70 => "v100",
        75 => "20",
        86 => "30",
        89 => "40",
        120 => "50",
        _ => null,
    };

    /// <summary>
    /// KVMem: one full Windows unified bundle, in its own version/GPU directory. No secondary
    /// source or CUDA archive is needed; backends/, workers/, runtime/, provenance/ remain intact.
    /// </summary>
    public static EngineInstallPlan? KvmemPlan(ReleaseInfo release, string managerRoot, double computeCapability = 0)
    {
        var architecture = KvmemArchitecture(computeCapability);
        if (architecture == null || !Regex.IsMatch(release.Tag, @"^v?\d+\.\d+\.\d+(?:[-.][A-Za-z0-9]+)*$")) return null;
        var family = KvmemGpuFamily(architecture.Value)!;
        var tag = "v" + release.Tag.TrimStart('v');
        var name = $"kvmem-{tag}-windows-x64-nvidia-{family}-unified.zip";
        var asset = release.Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (asset == null) return null;
        var digest = ParseDigest(asset.Sha256);
        if (tag == KvmemInstallation.PinnedVersion && architecture == 86)
            digest = KvmemInstallation.PinnedRtx30Sha256;
        var checksum = release.Assets.FirstOrDefault(a => a.Name.Equals(name + ".sha256", StringComparison.OrdinalIgnoreCase));
        // Never install an unverified unified bundle, even if it is the only matching zip.
        if (digest == null && checksum == null) return null;
        return new EngineInstallPlan(KvmemInstallation.BackendId, [asset with { Sha256 = digest }],
            Path.Combine(EngineRoot(managerRoot, KvmemInstallation.BackendId), $"{tag}-nvidia-{family}"), null, null)
        {
            KvmemArchitecture = architecture,
            ChecksumAsset = checksum,
            PackageRoot = Path.Combine(EngineRoot(managerRoot, KvmemInstallation.BackendId), $"{tag}-nvidia-{family}"),
            Architecture = "sm" + architecture,
        };
    }

    /// <summary>Parses standard SHA256/SHA256SUMS text, accepting only the requested asset's digest.</summary>
    public static string? ParseChecksum(string text, string assetName)
    {
        string? result = null;
        foreach (var line in text.Split('\n'))
        {
            var match = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})(?:\s+\*?(.+))?$");
            if (!match.Success) continue;
            if (match.Groups[2].Success && !match.Groups[2].Value.Equals(assetName, StringComparison.OrdinalIgnoreCase)) continue;
            var digest = match.Groups[1].Value.ToLowerInvariant();
            if (result != null && result != digest) return null;
            result = digest;
        }
        return result;
    }

    /// <summary>
    /// PolyStrata: its engine zip carries <c>strata-poly.exe</c> (into <c>engine-poly/</c>) and
    /// the source archive supplies <c>serve/server.py</c> and the requirements the venv needs.
    /// </summary>
    public static EngineInstallPlan? PolyStrataPlan(ReleaseInfo release, string managerRoot)
    {
        var engine = Find(release.Assets, @"^strata-poly-windows-x64-[0-9a-f]+\.zip$");
        if (engine == null || string.IsNullOrWhiteSpace(release.Tag)) return null;
        var root = EngineRoot(managerRoot, "polystrata");
        return new EngineInstallPlan("polystrata", [engine],
            Path.Combine(root, "engine-poly"), SourceArchive(PolyStrataRepo, release.Tag), root);
    }
}
