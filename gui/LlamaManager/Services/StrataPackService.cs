using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>Model preparation only: invokes the package's authoritative Python tools, never a GPU service.</summary>
public static class StrataPackService
{
    public sealed record Options(string PackageRoot, string ModelPath, string PythonPath = "", string CacheDirectory = "");
    public sealed record Result(string ModelPath, string PackDir, string ProfilePath, string LogPath, bool Reused);
    private sealed record PythonInfo(string Executable, string GgufRoot);
    private sealed record Source(string Path, long Length, long ModifiedUtcTicks, string Signature);
    private sealed record Manifest(int Version, string Identity, Source[] Sources);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string[] Tools = ["iq_pack.py", "make_profile.py", "gguf_reader.py", "strata_tokenizer.py", "_paths.py"];
    private const string BaseProbe = "import sys,json; assert sys.version_info >= (3,10), 'Python 3.10+ required'; print(json.dumps({'executable':sys.executable}))";
    private const string DependencyProbe = "import sys,os,json,pathlib; p=os.environ.get('STRATA_GGUF_PY'); sys.path.insert(0,p) if p else None; import numpy,regex,gguf; from gguf import quants,GGMLQuantizationType; print(json.dumps({'executable':sys.executable,'ggufRoot':str(pathlib.Path(gguf.__file__).resolve().parent.parent)}))";
    private const string ExportVocab = "import sys,pathlib; sys.path.insert(0,sys.argv[1]); from strata_tokenizer import Tokenizer; tk=Tokenizer.from_gguf(sys.argv[2]); f=open(sys.argv[3],'w',encoding='utf-8',newline='\\n'); f.writelines(str(i)+'\\t'+tk.token_bytes(i).hex()+'\\n' for i in range(len(tk.tokens))); f.close(); print('model vocab_bytes.tsv exported')";

    public static string DefaultCacheDirectory => Path.Combine(ConfigService.GetManagerRoot(), "data", "strata-packs");

    /// <summary>为 q36 原生路径导出 vocab_bytes.tsv（token id → 字节）；同一模型复用同一目录。</summary>
    public static async Task<string> EnsureVocabAsync(Options options, string model, Action<string>? progress = null, CancellationToken ct = default)
    {
        var vocab = StrataService.Q36VocabPath(model);
        var dir = Path.GetDirectoryName(vocab)!;
        var info = new FileInfo(vocab);
        var source = new FileInfo(model);
        if (info.Exists && info.Length > 0 && source.Exists && info.LastWriteTimeUtc >= source.LastWriteTimeUtc) return vocab;
        Directory.CreateDirectory(dir);
        var logPath = Path.Combine(ConfigService.GetManagerRoot(), "logs", "strata-q36-vocab-" + Guid.NewGuid().ToString("N") + ".log");
        var python = await EnsurePythonAsync(options, Path.Combine(Path.GetFullPath(options.PackageRoot), "tools"), DefaultCacheDirectory, logPath, progress ?? (_ => { }), ct).ConfigureAwait(false);
        await RunCheckedAsync(PythonStart(python.Executable, ["-u", "-c", ExportVocab,
            Path.Combine(Path.GetFullPath(options.PackageRoot), "tools"), model, vocab], python.GgufRoot),
            logPath, "vocab", progress ?? (_ => { }), ct).ConfigureAwait(false);
        if (!File.Exists(vocab)) throw new IOException("分词表导出失败：" + logPath);
        return vocab;
    }

    /// <summary>
    /// Prepare the tokenizer directory the Strata HTTP service reads (vocab.json / merges.txt /
    /// token_type.json / chat_template.jinja) from the GGUF, reusing it per model. Requires the
    /// Strata source tree (tools/strata_tokenizer.py) and Python 3.10+.
    /// Returns the directory that actually contains vocab.json: tool variants differ on whether
    /// they write into --out directly or into --out/tokenizer (the upstream tool nests), so the
    /// caller must point the runconfig at the real location rather than assuming one.
    /// </summary>
    public static async Task<string> EnsureTokenizerDirAsync(string serveRoot, string model, string tokenizerDir,
        string pythonPath = "", Action<string>? progress = null, CancellationToken ct = default)
    {
        string? FindMarker(string directory) =>
            File.Exists(Path.Combine(directory, "vocab.json")) ? directory
            : File.Exists(Path.Combine(directory, "tokenizer", "vocab.json")) ? Path.Combine(directory, "tokenizer")
            : null;

        var source = new FileInfo(model);
        var existing = Directory.Exists(tokenizerDir) ? FindMarker(tokenizerDir) : null;
        if (existing != null && source.Exists &&
            File.GetLastWriteTimeUtc(Path.Combine(existing, "vocab.json")) >= source.LastWriteTimeUtc)
            return existing;

        var tools = Path.Combine(Path.GetFullPath(serveRoot), "tools");
        var tool = Path.Combine(tools, "strata_tokenizer.py");
        if (!File.Exists(tool)) throw new FileNotFoundException("Strata 源码缺少 tools/strata_tokenizer.py，无法准备分词表。", tool);
        var logPath = Path.Combine(ConfigService.GetManagerRoot(), "logs", "strata-tokenizer-" + Guid.NewGuid().ToString("N") + ".log");
        var options = new Options(serveRoot, model, pythonPath, DefaultCacheDirectory);
        var python = await EnsurePythonAsync(options, tools, DefaultCacheDirectory, logPath, progress ?? (_ => { }), ct).ConfigureAwait(false);
        Directory.CreateDirectory(tokenizerDir);
        await RunCheckedAsync(PythonStart(python.Executable, ["-u", tool, "--gguf", model, "--out", tokenizerDir], python.GgufRoot),
            logPath, "tokenizer", progress ?? (_ => { }), ct).ConfigureAwait(false);
        var produced = FindMarker(tokenizerDir)
            ?? throw new IOException("分词表准备失败（工具已运行但未找到 vocab.json）：" + logPath);
        return produced;
    }

    public static async Task<Result> PrepareAsync(Options options, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("已有一个 pack 准备任务在运行。");
        try { return await PrepareCoreAsync(options, progress, ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private static async Task<Result> PrepareCoreAsync(Options options, Action<string>? progress, CancellationToken ct)
    {
        var (model, metadata, report, sources, tools, identity) = await Task.Run(() => Inspect(options, ct), ct).ConfigureAwait(false);
        var cache = Path.GetFullPath(string.IsNullOrWhiteSpace(options.CacheDirectory) ? DefaultCacheDirectory : options.CacheDirectory);
        Directory.CreateDirectory(cache);
        // Serializes shared environment installation and publication across launcher instances.
        using var lease = new FileStream(Path.Combine(cache, "prepare.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var stem = Regex.Replace(Path.GetFileNameWithoutExtension(model), @"[^\p{L}\p{N}._-]", "_");
        if (stem.Length > 48) stem = stem[..48];
        var target = Path.Combine(cache, stem + "-" + identity[..16]);
        var logPath = Path.Combine(ConfigService.GetManagerRoot(), "logs", "strata-pack-" + Guid.NewGuid().ToString("N") + ".log");
        var messages = new StringBuilder();
        var messageLock = new object();
        void Log(string line)
        {
            lock (messageLock) messages.AppendLine(line);
            progress?.Invoke(line);
        }
        string? staging = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            Log(StrataCompatibility.Describe(report));
            ct.ThrowIfCancellationRequested();
            if (IsComplete(target, identity, metadata.ExpertCount))
            {
                Log("已找到与当前 GGUF / 打包工具匹配的完整 pack，直接复用。");
                return new Result(model, target, Path.Combine(target, "expert-profile.bin"), logPath, true);
            }
            var python = await EnsurePythonAsync(options, tools, cache, logPath, Log, ct).ConfigureAwait(false);
            staging = Path.Combine(cache, ".building-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            Log("[1/3] 生成 native pack（保留原 GGUF，不复制整份专家权重）…");
            var pack = PythonStart(python.Executable, ["-u", Path.Combine(tools, "iq_pack.py"), "--gguf", model,
                "--out", staging, "--compat-bf16"], python.GgufRoot);
            await RunCheckedAsync(pack, logPath, "pack", Log, ct).ConfigureAwait(false);
            Log("[2/3] 生成初始 expert profile（按层均衡排列，非工作负载校准）…");
            await RunCheckedAsync(PythonStart(python.Executable, ["-u", Path.Combine(tools, "make_profile.py"), "--no-base",
                "--n-layer", "40", "--n-expert", metadata.ExpertCount.ToString(), "--out", Path.Combine(staging, "expert-profile.bin")],
                python.GgufRoot), logPath, "profile", Log, ct).ConfigureAwait(false);
            Log("[3/3] 导出此模型的 token 字节表并校验产物…");
            await RunCheckedAsync(PythonStart(python.Executable, ["-u", "-c", ExportVocab, tools, model,
                Path.Combine(staging, "vocab_bytes.tsv")], python.GgufRoot), logPath, "vocab", Log, ct).ConfigureAwait(false);
            var currentSources = await Task.Run(() => ReadSources(model), ct).ConfigureAwait(false);
            if (!sources.SequenceEqual(currentSources)) throw new IOException("生成期间模型发生变化，未发布 pack；请等模型下载/修改完成后重试。");
            await File.WriteAllTextAsync(Path.Combine(staging, "launcher-pack.json"),
                JsonSerializer.Serialize(new Manifest(1, identity, sources)), ct).ConfigureAwait(false);
            if (!IsComplete(staging, identity, metadata.ExpertCount)) throw new InvalidDataException("打包进程已退出，但产物不完整或 profile 布局不匹配。详见：" + logPath);
            ct.ThrowIfCancellationRequested();
            // Never overwrite an existing/manual pack, even if it has been damaged or interrupted.
            if (Directory.Exists(target)) target += "-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(staging, target);
            staging = null;
            Log("准备完成：" + target);
            return new Result(model, target, Path.Combine(target, "expert-profile.bin"), logPath, false);
        }
        finally
        {
            if (staging != null)
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("临时目录待清理：" + staging); }
            }
            try { await File.WriteAllTextAsync(logPath, messages.ToString()).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static (string Model, GgufMetadata Metadata, StrataCompatibility.Report Report, Source[] Sources, string Tools, string Identity)
        Inspect(Options options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ModelPath) || !File.Exists(options.ModelPath))
            throw new FileNotFoundException("请先选择已下载完成的 GGUF 模型。");
        var model = Path.GetFullPath(options.ModelPath);
        var md = GgufService.ReadShards(model, ct);
        var report = StrataCompatibility.Analyze(md);
        if (!report.IsPackable)
            throw new NotSupportedException(StrataCompatibility.Summary(report) + "\n\n" + StrataCompatibility.Describe(report));
        var tools = Path.GetFullPath(Path.Combine(options.PackageRoot, "tools"));
        foreach (var tool in Tools)
            if (!File.Exists(Path.Combine(tools, tool))) throw new FileNotFoundException("Strata 包缺少 tools/" + tool + "，请使用包含打包工具的完整运行包。");
        var sources = ReadSources(model);
        var toolHashes = Tools.Select(t => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(tools, t)))));
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sources) + string.Join(";", toolHashes))));
        return (model, md, report, sources, tools, identity);
    }

    private static Source[] ReadSources(string model)
    {
        return GgufService.ShardPaths(model).Select(path =>
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("模型分片缺失。", path);
            return new Source(PathIdentity.Normalize(path), info.Length, info.LastWriteTimeUtc.Ticks,
                FileSignature.Compute(path) ?? throw new IOException("无法计算模型签名：" + path));
        }).ToArray();
    }

    private static bool IsComplete(string directory, string identity, int experts)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(directory, "launcher-pack.json")));
            if (manifest?.Version != 1 || manifest.Identity != identity) return false;
            foreach (var name in new[] { "index.txt", "dense.bin", "native_experts.txt", "vocab_bytes.tsv" })
                if (new FileInfo(Path.Combine(directory, name)) is not { Exists: true, Length: > 0 }) return false;
            using var file = File.OpenRead(Path.Combine(directory, "expert-profile.bin"));
            using var reader = new BinaryReader(file);
            return Encoding.ASCII.GetString(reader.ReadBytes(4)) == "STRP" && reader.ReadUInt32() == 1 &&
                reader.ReadUInt32() == 40 && reader.ReadUInt32() == experts && file.Length == 24L + 40L * experts * 8;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    private static ProcessStartInfo PythonStart(string exe, IEnumerable<string> arguments, string? ggufRoot = null)
    {
        var start = StrataService.CaptureStart(exe);
        if (string.IsNullOrWhiteSpace(start.WorkingDirectory)) start.WorkingDirectory = ConfigService.GetManagerRoot();
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        if (!string.IsNullOrWhiteSpace(ggufRoot)) start.Environment["STRATA_GGUF_PY"] = ggufRoot;
        return start;
    }

    private static async Task<StrataService.ChildResult> RunCheckedAsync(ProcessStartInfo start, string log, string step,
        Action<string> progress, CancellationToken ct, int timeoutSeconds = 0)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds > 0) deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var result = await StrataService.RunChildAsync(start, log + "." + step + ".out", log + "." + step + ".err",
                deadline.Token, progress: progress).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new IOException($"{step} 失败（退出码 {result.ExitCode}）。\n" +
                string.Join('\n', (result.Stderr + "\n" + result.Stdout).Split('\n').TakeLast(12)) + "\n日志：" + log);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException(step + " 超时，已终止子进程。日志：" + log); }
    }

    private static async Task<PythonInfo> EnsurePythonAsync(Options options, string tools, string cache, string log,
        Action<string> progress, CancellationToken ct)
    {
        var venv = Path.Combine(cache, "python-env");
        var venvPython = Path.Combine(venv, "Scripts", "python.exe");
        var bundledGguf = Path.Combine(options.PackageRoot, "third_party", "llama.cpp", "gguf-py");
        var ggufRoot = Directory.Exists(Path.Combine(bundledGguf, "gguf")) ? bundledGguf : null;
        var candidates = string.IsNullOrWhiteSpace(options.PythonPath)
            ? new[] { venvPython, Path.Combine(options.PackageRoot, "runtime", "python", "python.exe"), "python", "py" }
            : new[] { options.PythonPath };
        string? python = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var prefix = candidate == "py" ? new[] { "-3" } : Array.Empty<string>();
                var result = await RunCheckedAsync(PythonStart(candidate, prefix.Concat(["-c", BaseProbe])), log, "python",
                    _ => { }, ct, 10).ConfigureAwait(false);
                using var info = JsonDocument.Parse(result.Stdout.Trim());
                python = info.RootElement.GetProperty("executable").GetString();
                if (!string.IsNullOrWhiteSpace(python)) break;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or System.ComponentModel.Win32Exception) { }
        }
        if (python == null) throw new FileNotFoundException("未找到 Python 3.10+。请安装 Windows x64 Python（勾选 PATH），或在面板中选择 python.exe。已有 pack 的推理不需要 Python。");
        async Task<PythonInfo> Dependencies(string executable)
        {
            var result = await RunCheckedAsync(PythonStart(executable, ["-c", DependencyProbe], ggufRoot), log, "dependencies",
                _ => { }, ct, 20).ConfigureAwait(false);
            using var info = JsonDocument.Parse(result.Stdout.Trim());
            return new PythonInfo(info.RootElement.GetProperty("executable").GetString()!, info.RootElement.GetProperty("ggufRoot").GetString()!);
        }
        try { return await Dependencies(python).ConfigureAwait(false); }
        catch (IOException) { progress("缺少打包依赖：在独立 python-env 中安装 numpy / regex / gguf（首次需要网络）…"); }
        if (!PathIdentity.Equals(python, venvPython))
            await RunCheckedAsync(PythonStart(python, ["-m", "venv", venv]), log, "venv", progress, ct, 120).ConfigureAwait(false);
        await RunCheckedAsync(PythonStart(venvPython, ["-m", "pip", "install", "--disable-pip-version-check", "numpy", "regex", "gguf"]),
            log, "pip", progress, ct, 600).ConfigureAwait(false);
        return await Dependencies(venvPython).ConfigureAwait(false);
    }
}
