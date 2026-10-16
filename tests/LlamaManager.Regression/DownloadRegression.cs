using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class DownloadRegression
{
    /// <summary>
    /// The release-to-install-plan mapping is a pure function of GitHub release JSON, so it can be
    /// checked against representative release shapes without a network.
    /// </summary>
    public static void RunCatalog()
    {
        // llama.cpp: a nightly with CUDA 13.4 + cudart; both extract beside llama-server.exe.
        var llama = JsonNode.Parse("""
        {
          "tag_name": "b11642",
          "assets": [
            { "name": "llama-b11642-bin-win-cuda-13.4-x64.zip",
              "browser_download_url": "https://x/llama-cuda13.zip",
              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            { "name": "cudart-llama-bin-win-cuda-13.4-x64.zip",
              "browser_download_url": "https://x/cudart-cuda13.zip" },
            { "name": "llama-b11642-bin-win-cuda-12.4-x64.zip",
              "browser_download_url": "https://x/llama-cuda12.zip" },
            { "name": "cudart-llama-bin-win-cuda-12.4-x64.zip",
              "browser_download_url": "https://x/cudart-cuda12.zip" },
            { "name": "llama-b11642-bin-win-cpu-x64.zip", "browser_download_url": "https://x/cpu.zip" }
          ]
        }
        """)!;
        var llamaRelease = EngineCatalog.ParseRelease(llama);
        Equal("b11642", llamaRelease.Tag);
        var llamaPlan = EngineCatalog.LlamaCppPlan(llamaRelease, @"C:\root");
        Check(llamaPlan != null, "llama.cpp plan not built");
        Equal(2, llamaPlan!.EngineArchives.Count);
        Check(llamaPlan.EngineArchives[0].Url.EndsWith("llama-cuda13.zip"), "wrong binary chosen");
        Check(llamaPlan.EngineArchives[1].Url.EndsWith("cudart-cuda13.zip"), "cudart not paired");
        Equal(@"C:\root", llamaPlan.EngineDir);
        Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            llamaPlan.EngineArchives[0].Sha256);

        // A release with only the 12.4 asset still serves a pre-Ampere GPU.
        var cuda12 = JsonNode.Parse("""
        { "tag_name": "b1", "assets": [
          { "name": "llama-b1-bin-win-cuda-12.4-x64.zip", "browser_download_url": "https://x/c12.zip" } ] }
        """)!;
        var c12Plan = EngineCatalog.LlamaCppPlan(EngineCatalog.ParseRelease(cuda12), @"C:\root", 7.5);
        Check(c12Plan != null && c12Plan.EngineArchives[0].Url.EndsWith("c12.zip"), "CUDA 12 fallback failed");

        // Pre-Ampere GPUs (compute capability < 8.0) must get the CUDA 12.4 build even when a
        // 13.4 asset exists: the 13.4 nightlies dropped Turing/Volta SASS (verified live on an
        // RTX 2060 - the 13.4 build fails with "PTX compiled with an unsupported toolchain").
        var c12Choice = EngineCatalog.LlamaCppPlan(llamaRelease, @"C:\root", 7.5);
        Check(c12Choice != null, "no plan for sm_75");
        Check(c12Choice!.EngineArchives[0].Name.Contains("cuda-12.4"), "12.4 asset not selected for sm_75");
        Check(c12Choice.EngineArchives.Any(a => a.Name.Contains("cudart-llama-bin-win-cuda-12.4")), "12.4 cudart not paired for sm_75");
        // Ampere and newer keep 13.4.
        var c13Choice = EngineCatalog.LlamaCppPlan(llamaRelease, @"C:\root", 8.6);
        Check(c13Choice!.EngineArchives[0].Name.Contains("cuda-13.4"), "Ampere GPU did not keep CUDA 13.4");
        // Unknown capability defaults to 13.4 (the common case for current cards).
        Check(EngineCatalog.LlamaCppPlan(llamaRelease, @"C:\root", 0)!.EngineArchives[0].Name.Contains("cuda-13.4"),
            "unknown capability did not default to 13.4");

        // A release with no Windows CUDA asset yields no plan (honest failure, not a wrong pick).
        var none = JsonNode.Parse("""{ "tag_name": "b1", "assets": [] }""")!;
        Check(EngineCatalog.LlamaCppPlan(EngineCatalog.ParseRelease(none), @"C:\root") == null, "empty release produced a plan");

        // Strata: patched fork engine zip + upstream-branch source archive.
        var strata = JsonNode.Parse("""
        { "tag_name": "v0.1.44.1-apex.1", "assets": [
          { "name": "strata-windows-x64.zip", "browser_download_url": "https://x/strata.zip",
            "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
          { "name": "strata-windows-x64-cuda12.zip", "browser_download_url": "https://x/strata12.zip" } ] }
        """)!;
        var strataPlan = EngineCatalog.StrataPlan(EngineCatalog.ParseRelease(strata), @"C:\root");
        Check(strataPlan != null, "strata plan not built");
        Equal(1, strataPlan!.EngineArchives.Count);
        Check(strataPlan.EngineArchives[0].Url.EndsWith("strata.zip"), "cuda12 zip wrongly preferred");
        Check(strataPlan.EngineDir.EndsWith(Path.Combine("engines", "strata-upstream", "engine")), "strata engine dir wrong");
        Check(strataPlan.SourceArchive != null
            && strataPlan.SourceArchive.Url.Contains("viosonlee/apex-on-strata/archive/refs/heads/upstream.zip"),
            "strata source archive must be the fork's upstream branch");

        // PolyStrata: hash-named asset + source archive.
        var poly = JsonNode.Parse("""
        { "tag_name": "v0.1.44.1-poly.2", "assets": [
          { "name": "strata-poly-windows-x64-0d1d889b3fc8977e.zip", "browser_download_url": "https://x/poly.zip" } ] }
        """)!;
        var polyPlan = EngineCatalog.PolyStrataPlan(EngineCatalog.ParseRelease(poly), @"C:\root");
        Check(polyPlan != null, "poly plan not built");
        Check(polyPlan!.EngineDir.EndsWith(Path.Combine("engines", "polystrata", "engine-poly")), "poly engine dir wrong");
        Check(polyPlan.SourceArchive != null, "poly source archive missing");

        // Digest parsing rejects junk and accepts GitHub's prefix.
        Equal("ab", EngineCatalog.ParseDigest("sha256:ab" + new string('0', 62))?[..2]);
        Check(EngineCatalog.ParseDigest("nope") == null, "junk digest accepted");
        Check(EngineCatalog.ParseDigest(null) == null, "null digest accepted");

        // Repo routing covers exactly the downloadable backends.
        Check(EngineAcquisitionService.CanAcquire("llamacpp"), "llamacpp not acquirable");
        Check(EngineAcquisitionService.CanAcquire("strata-upstream"), "strata not acquirable");
        Check(EngineAcquisitionService.CanAcquire("polystrata"), "polystrata not acquirable");
        Check(!EngineAcquisitionService.CanAcquire("nope"), "unknown backend acquirable");
    }

    /// <summary>
    /// When real GitHub release dumps are present (logs/*-rel.json), the plan mapping must still
    /// find a Windows binary. This guards against a release that renames its assets. The llama.cpp
    /// dump is a releases list (its binaries ship as prereleases); the others are single releases.
    /// </summary>
    public static void RunAgainstRealReleases(string root)
    {
        var cases = new (string File, string Backend, bool IsList)[]
        {
            ("llama-list.json", "llamacpp", true),
            ("strata-rel.json", "strata-upstream", false),
            ("poly-rel.json", "polystrata", false),
        };
        foreach (var (file, backend, isList) in cases)
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "logs", file);
            if (!File.Exists(path)) continue; // local verification aid; absent in CI
            EngineInstallPlan? plan = null;
            string tag = "";
            if (isList)
            {
                foreach (var node in JsonNode.Parse(File.ReadAllText(path))!.AsArray())
                {
                    if (node == null) continue;
                    var release = EngineCatalog.ParseRelease(node);
                    plan = EngineAcquisitionService.PlanFor(backend, release, root);
                    if (plan != null) { tag = release.Tag; break; }
                }
            }
            else
            {
                var release = EngineCatalog.ParseRelease(JsonNode.Parse(File.ReadAllText(path))!);
                plan = EngineAcquisitionService.PlanFor(backend, release, root);
                tag = release.Tag;
            }
            Check(plan != null, $"{backend}: no install plan for real release {tag} ({file})");
            Check(plan!.EngineArchives.Count > 0, $"{backend}: plan has no engine archive");
        }
    }

    /// <summary>
    /// The downloader's extract and checksum mechanics: zip-slip is refused, the wrapper folder of
    /// a GitHub source archive is stripped, and a bad SHA-256 deletes the partial file.
    /// </summary>
    public static async Task RunDownloadMechanicsAsync(string root)
    {
        var work = Path.Combine(root, "download-test");
        Directory.CreateDirectory(work);

        // A normal archive extracts with its structure preserved.
        var archive = Path.Combine(work, "plain.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "engine/strata.exe", "exe");
            WriteEntry(zip, "engine/strata_kq.dll", "dll");
        }
        var outDir = Path.Combine(work, "plain");
        Equal(2, EngineDownloadService.ExtractZip(archive, outDir));
        Check(File.Exists(Path.Combine(outDir, "engine", "strata.exe")), "engine not extracted");

        // A GitHub source archive is flattened: <repo>-<tag>/ wrapper stripped.
        var source = Path.Combine(work, "source.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "Strata-0.1.44.1/serve/server.py", "py");
            WriteEntry(zip, "Strata-0.1.44.1/requirements.txt", "req");
        }
        var sourceOut = Path.Combine(work, "source");
        Equal(2, EngineAcquisitionService.ExtractFlattened(source, sourceOut, new DelegateLogSink(_ => { })));
        Check(File.Exists(Path.Combine(sourceOut, "serve", "server.py")), "source not flattened");
        Check(!Directory.Exists(Path.Combine(sourceOut, "Strata-0.1.44.1")), "wrapper folder kept");

        // Zip-slip is refused.
        var evil = Path.Combine(work, "evil.zip");
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
            WriteEntry(zip, "../escaped.txt", "x");
        Throws<IOException>(() => EngineDownloadService.ExtractZip(evil, Path.Combine(work, "evil")));

        // Checksum: a wrong digest deletes the download; a right one keeps it.
        var payload = "hello-download-payload";
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        using var server = new LocalFileServer(payload);
        var target = Path.Combine(work, "copy.bin");
        await EngineDownloadService.DownloadAsync(server.Url, target, digest, _ => { }, CancellationToken.None);
        Check(File.Exists(target) && File.ReadAllText(target) == payload, "verified download wrong");
        var bad = Path.Combine(work, "bad.bin");
        try
        {
            await EngineDownloadService.DownloadAsync(server.Url, bad, new string('0', 64), _ => { }, CancellationToken.None);
            throw new Exception("bad checksum accepted");
        }
        catch (EngineDownloadService.ChecksumMismatchException) { }
        Check(!File.Exists(bad) && !File.Exists(bad + ".part"), "bad download not cleaned up");

        Check(EngineDownloadService.FormatBytes(2L << 30).Contains("GiB"), "size formatting wrong");
    }

    /// <summary>A one-shot loopback HTTP server that serves one payload, for download tests.</summary>
    private sealed class LocalFileServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly Task _serve;

        public LocalFileServer(string payload)
        {
            var port = Fixtures.FreePort();
            Url = $"http://127.0.0.1:{port}/payload.bin";
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            _listener.Start();
            _serve = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var client = await _listener.AcceptTcpClientAsync();
                        await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        // Read the request line and headers up to the blank line.
                        for (string? line; !string.IsNullOrEmpty(line = await reader.ReadLineAsync());) { }
                        var bytes = Encoding.UTF8.GetBytes(payload);
                        var header = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header);
                        await stream.WriteAsync(bytes);
                        await stream.FlushAsync();
                    }
                }
                catch { }
            });
        }

        public string Url { get; }
        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _serve.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
