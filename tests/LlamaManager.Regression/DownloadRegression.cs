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
        Check(EngineAcquisitionService.CanAcquire("polystrata"), "polystrata not acquirable");
        Check(!EngineAcquisitionService.CanAcquire("nope"), "unknown backend acquirable");
    }

    /// <summary>
    /// llama.cpp nightlies expose a build number that is comparable with release tags. An
    /// unreadable installed build must skip the update instead of triggering a full reinstall.
    /// </summary>
    public static async Task RunUpdateCheckAsync(string root, string model)
    {
        var oldFetcher = EngineAcquisitionService.ReleasesFetcher;
        var oldCapability = EngineAcquisitionService.DetectComputeCapability;
        var marker = Path.Combine(root, "no-build");
        var versionMarker = Path.Combine(root, "version-override.txt");
        var oldConfig = ConfigService.ReadConfig();
        try
        {
            var capabilityProbes = 0;
            EngineAcquisitionService.DetectComputeCapability = () =>
            {
                capabilityProbes++;
                return 8.6;
            };
            EngineAcquisitionService.ReleasesFetcher = (_, _) => Task.FromResult<JsonArray?>(JsonNode.Parse("""
            [
              {
                "tag_name": "b12021",
                "assets": []
              },
              {
                "tag_name": "b12020",
                "assets": [
                  { "name": "llama-b12020-bin-win-cuda-13.4-x64.zip",
                    "browser_download_url": "https://invalid.example/llama.zip" }
                ]
              }
            ]
            """)!.AsArray());

            var current = await EngineAcquisitionService.UpdateAsync(
                "llamacpp", new DelegateLogSink(_ => { }), CancellationToken.None);
            Equal(false, current.Updated);
            Equal("b12020", current.InstalledVersion);
            Equal("b12020", current.LatestVersion);
            Equal<string?>(null, current.SkippedReason);
            Equal(1, capabilityProbes);

            File.WriteAllText(marker, "");
            var releaseRequested = false;
            EngineAcquisitionService.ReleasesFetcher = (_, _) =>
            {
                releaseRequested = true;
                throw new IOException("unexpected release lookup");
            };
            var unreadable = await EngineAcquisitionService.UpdateAsync(
                "llamacpp", new DelegateLogSink(_ => { }), CancellationToken.None);
            Equal(false, unreadable.Updated);
            Check(!releaseRequested, "unreadable install triggered a network lookup");
            Check(unreadable.SkippedReason?.Contains("跳过自动更新") == true,
                "unreadable install did not explain why the update was skipped");

            // An outdated, live engine must never be replaced beneath a running process. The
            // direct acquisition path also refuses before any release lookup or download.
            File.WriteAllText(versionMarker, "version: 0.8.0-dev (build 12019, commit fixture)");
            var port = Fixtures.FreePort();
            var live = new ManagerConfig
            {
                ModelPath = model,
                Port = port,
                Args = $"--host 127.0.0.1 --port {port} -ngl 0",
            };
            ConfigService.SaveConfig(live);
            var backend = new LlamaCppBackend();
            await backend.StartAsync(new EngineStartRequest { Config = live },
                new DelegateLogSink(_ => { }), CancellationToken.None);

            var acquireLookups = 0;
            EngineAcquisitionService.ReleasesFetcher = (_, _) =>
            {
                acquireLookups++;
                throw new IOException("running install must not reach the network");
            };
            var refused = false;
            try
            {
                await EngineAcquisitionService.AcquireAsync(
                    "llamacpp", new DelegateLogSink(_ => { }), CancellationToken.None);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("正在运行"))
            {
                refused = true;
            }
            Check(refused && acquireLookups == 0, "direct acquisition touched a running engine");

            EngineAcquisitionService.ReleasesFetcher = (_, _) => Task.FromResult<JsonArray?>(JsonNode.Parse("""
            [
              {
                "tag_name": "b12020",
                "assets": [
                  { "name": "llama-b12020-bin-win-cuda-13.4-x64.zip",
                    "browser_download_url": "https://invalid.example/must-not-download.zip" }
                ]
              }
            ]
            """)!.AsArray());
            var runningUpdate = await EngineAcquisitionService.UpdateAsync(
                "llamacpp", new DelegateLogSink(_ => { }), CancellationToken.None);
            Equal(false, runningUpdate.Updated);
            Check(runningUpdate.SkippedReason?.Contains("正在运行") == true,
                "outdated running engine was not skipped");
            Check(runningUpdate.SkippedReason?.Contains("先停止服务") == true,
                "running-engine update skip lacks an actionable next step");
        }
        finally
        {
            await new LlamaCppBackend().StopAsync(
                new EngineSession { BackendId = "llamacpp" }, new DelegateLogSink(_ => { }));
            ConfigService.SaveConfig(oldConfig);
            EngineAcquisitionService.ReleasesFetcher = oldFetcher;
            EngineAcquisitionService.DetectComputeCapability = oldCapability;
            if (File.Exists(marker)) File.Delete(marker);
            if (File.Exists(versionMarker)) File.Delete(versionMarker);
        }
    }

    /// <summary>
    /// Exercises the complete plan installer against a local HTTP "release": engine and source
    /// archives, staged extraction, publishing, BUILD version and persisted package paths.
    /// </summary>
    public static async Task RunInstallIntegrationAsync(string root)
    {
        var work = Path.Combine(root, "install-integration");
        var engineDir = Path.Combine(work, "engines", "polystrata", "engine-poly");
        var sourceDir = Path.Combine(work, "engines", "polystrata");
        Directory.CreateDirectory(engineDir);
        File.WriteAllText(Path.Combine(engineDir, "keep.txt"), "keep");
        var engineZip = ZipBytes(
            ("strata-poly.exe", "new-engine"),
            ("BUILD.json", """{"version":"old"}"""));
        var sourceZip = ZipBytes(
            ("PolyStrata-v9.9-test/serve/server.py", "print('serve')"),
            ("PolyStrata-v9.9-test/requirements.txt", "numpy\n"));
        using var server = new LocalFilesServer(new Dictionary<string, byte[]>
        {
            ["/engine.zip"] = engineZip,
            ["/source.zip"] = sourceZip,
        });
        var plan = new EngineInstallPlan(
            "polystrata",
            [new ReleaseAsset("strata-poly-windows-x64-deadbeef.zip", server.UrlFor("/engine.zip"), null)],
            engineDir,
            new ReleaseAsset("v9.9-test.zip", server.UrlFor("/source.zip"), null),
            sourceDir);
        var oldSettings = ConfigService.ReadConfig().Engines.GetValueOrDefault("polystrata");
        try
        {
            var installed = await EngineAcquisitionService.InstallPlanAsync(
                plan, "v9.9-test", new DelegateLogSink(_ => { }), CancellationToken.None);
            Equal(engineDir, installed);
            Equal("new-engine", File.ReadAllText(Path.Combine(engineDir, "strata-poly.exe")));
            Equal("keep", File.ReadAllText(Path.Combine(engineDir, "keep.txt")));
            Check(File.ReadAllText(Path.Combine(engineDir, "BUILD.json")).Contains("9.9-test"),
                "installed BUILD.json version was not updated");
            Check(File.Exists(Path.Combine(sourceDir, "serve", "server.py")), "source archive was not flattened");
            Check(File.Exists(Path.Combine(sourceDir, "requirements.txt")), "requirements were not installed");
            var settings = ConfigService.ReadConfig().Engines.GetValueOrDefault("polystrata");
            Check(settings != null, "installed engine paths were not persisted");
            Equal(sourceDir, settings!.Parameters.GetValueOrDefault("packageRoot"));
            Equal(sourceDir, settings.Parameters.GetValueOrDefault("serveRoot"));
        }
        finally
        {
            ConfigService.SaveEngineSettings(
                ConfigService.ReadConfig(), "polystrata", oldSettings ?? new EngineSettings());
        }
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
        Check(!File.Exists(bad) && Directory.GetFiles(work, "bad.bin*.part").Length == 0,
            "bad download not cleaned up");

        // Two concurrent downloads of one asset must not share a temp file (DEF-11): both succeed,
        // the destination is complete, and neither attempt leaves a .part behind.
        var shared = Path.Combine(work, "shared.bin");
        await Task.WhenAll(
            EngineDownloadService.DownloadAsync(server.Url, shared, digest, _ => { }, CancellationToken.None),
            EngineDownloadService.DownloadAsync(server.Url, shared, digest, _ => { }, CancellationToken.None));
        Check(File.Exists(shared) && File.ReadAllText(shared) == payload, "concurrent downloads produced a bad file");
        Check(Directory.GetFiles(work, "shared.bin*.part").Length == 0,
            "concurrent downloads left a temp file behind");

        // Engine files publish transactionally: an existing target is replaced, a new file is
        // created, and an unrelated file survives. If a later target is locked, the earlier
        // replacement is rolled back instead of leaving a mixed installation.
        var transaction = Path.Combine(work, "transaction");
        var stage = Path.Combine(transaction, "stage");
        var destination = Path.Combine(transaction, "destination");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(stage, "a.txt"), "new-a");
        File.WriteAllText(Path.Combine(stage, "b.txt"), "new-b");
        File.WriteAllText(Path.Combine(destination, "a.txt"), "old-a");
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "keep");
        Equal(2, EngineDownloadService.PublishStagedDirectories(
            [(stage, destination)], Path.Combine(transaction, "backup")));
        Equal("new-a", File.ReadAllText(Path.Combine(destination, "a.txt")));
        Equal("new-b", File.ReadAllText(Path.Combine(destination, "b.txt")));
        Equal("keep", File.ReadAllText(Path.Combine(destination, "keep.txt")));

        var rollbackStage = Path.Combine(transaction, "rollback-stage");
        var rollbackDestination = Path.Combine(transaction, "rollback-destination");
        Directory.CreateDirectory(rollbackStage);
        Directory.CreateDirectory(rollbackDestination);
        File.WriteAllText(Path.Combine(rollbackStage, "a.txt"), "replacement-a");
        File.WriteAllText(Path.Combine(rollbackStage, "z.txt"), "replacement-z");
        File.WriteAllText(Path.Combine(rollbackDestination, "a.txt"), "original-a");
        File.WriteAllText(Path.Combine(rollbackDestination, "z.txt"), "original-z");
        var rollbackFailed = false;
        using (new FileStream(Path.Combine(rollbackDestination, "z.txt"),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                EngineDownloadService.PublishStagedDirectories(
                    [(rollbackStage, rollbackDestination)], Path.Combine(transaction, "rollback-backup"));
            }
            catch (IOException) { rollbackFailed = true; }
        }
        Check(rollbackFailed, "locked engine file did not fail the transaction");
        Equal("original-a", File.ReadAllText(Path.Combine(rollbackDestination, "a.txt")));
        Equal("original-z", File.ReadAllText(Path.Combine(rollbackDestination, "z.txt")));

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
                        var client = await _listener.AcceptTcpClientAsync();
                        // Serve each connection on its own task so concurrent downloads really overlap.
                        _ = Task.Run(async () =>
                        {
                            using (client)
                            {
                                var stream = client.GetStream();
                                await using (stream.ConfigureAwait(false))
                                {
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
                        });
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

    /// <summary>A tiny loopback HTTP server serving named byte payloads for installation tests.</summary>
    private sealed class LocalFilesServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly IReadOnlyDictionary<string, byte[]> _files;
        private readonly Task _serve;

        public LocalFilesServer(IReadOnlyDictionary<string, byte[]> files)
        {
            _files = files;
            var port = Fixtures.FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            _listener.Start();
            _serve = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(async () =>
                        {
                            using (client)
                            {
                                var stream = client.GetStream();
                                await using (stream.ConfigureAwait(false))
                                {
                                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                                    var request = await reader.ReadLineAsync() ?? "";
                                    for (string? line; !string.IsNullOrEmpty(line = await reader.ReadLineAsync());) { }
                                    var path = request.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";
                                    var found = _files.TryGetValue(path, out var bytes);
                                    var body = found ? bytes! : Encoding.UTF8.GetBytes("not found");
                                    var header = Encoding.ASCII.GetBytes(
                                        $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\n" +
                                        $"Content-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                                    await stream.WriteAsync(header);
                                    await stream.WriteAsync(body);
                                    await stream.FlushAsync();
                                }
                            }
                        });
                    }
                }
                catch { }
            });
        }

        public string BaseUrl { get; }
        public string UrlFor(string path) => BaseUrl + path;
        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _serve.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private static byte[] ZipBytes(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries) WriteEntry(zip, name, content);
        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
