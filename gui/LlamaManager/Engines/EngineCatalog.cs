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
}

/// <summary>
/// Maps a GitHub release to the files an engine install needs. Every rule is a pure function of
/// the release metadata, so the mapping can be checked against real release JSON in tests.
/// </summary>
public static class EngineCatalog
{
    public const string LlamaCppRepo = "ggml-org/llama.cpp";
    public const string PolyStrataRepo = "VecSzn/PolyStrata";

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
