using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>
/// Cyber on Strata 推理的独立生命周期。与 ServerService（llama-server HTTP 生命周期）完全分开：
/// Strata 是一次性的文本进/文本出进程，无 API 端口；两者共享显存，必须互斥运行。
/// </summary>
public static class StrataService
{
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    public sealed record StrataResult(string Answer, string Stats, int PromptTokens, string LogPath);
    public sealed record EnginePaths(string PackageRoot, string StrataExe, string TokenizeExe, string VocabTsv, string PackDir, string ProfileBin, string ModelsDir, string LastRunLog);

    public static bool IsRunning(ManagerConfig config)
    {
        var session = ConfigService.ReadConfig().Strata;
        using var process = GetLiveProcess(session);
        return process != null;
    }

    private static Process? GetLiveProcess(StrataConfig config)
    {
        if (config.LastPid <= 0) return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(config.LastPid);
            if (!process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == config.LastStartedUtcTicks &&
                PathIdentity.Equals(process.MainModule?.FileName, config.LastEnginePath))
                return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process?.Dispose();
        return null;
    }

    /// <summary>引擎是否带原生 qwen35moe 家族路径（上游 0.1.44+ 的 `--families`）。旧 cyber-on-strata 引擎没有该旗标。
    /// 必须同时排空 stdout 与 stderr 并在超时后结束子进程，否则日志填满管道会死锁调用线程。</summary>
    public static bool EngineSupportsQwen35(string strataExe)
    {
        Process? process = null;
        try
        {
            var start = CaptureStart(strataExe);
            start.ArgumentList.Add("--families");
            process = Process.Start(start);
            if (process == null) return false;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) return false;
            var text = stdout.GetAwaiter().GetResult();
            return process.ExitCode == 0 && text.Contains("qwen35moe", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        finally
        {
            try { if (process is { HasExited: false }) process.Kill(true); } catch { }
            process?.Dispose();
        }
    }

    /// <summary>q36 分词表的稳定缓存路径（按归一化模型路径的 SHA-256 前 16 位）。</summary>
    public static string Q36VocabPath(string model)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(PathIdentity.Normalize(model))))[..16];
        return Path.Combine(ConfigService.GetManagerRoot(), "data", "strata-q36", hash, "vocab_bytes.tsv");
    }

    /// <summary>q36 原生路径只需要 engine\strata.exe 与分词器，不需要 pack/profile。</summary>
    public static EnginePaths ResolveQ36Paths(ManagerConfig config)
    {
        var root = config.Strata.PackageRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("未找到 Strata 包目录，请先在 Strata 面板中选择。");
        root = Path.GetFullPath(root);
        var engineDir = Path.Combine(root, "engine");
        var strata = Path.Combine(engineDir, "strata.exe");
        var tokenize = Path.Combine(engineDir, "tokenize", "llama-tokenize.exe");
        if (!File.Exists(strata)) throw new FileNotFoundException("未找到 strata.exe。", strata);
        if (!File.Exists(tokenize)) throw new FileNotFoundException("未找到 llama-tokenize.exe。", tokenize);
        return new EnginePaths(root, strata, tokenize, Q36VocabPath(config.Strata.ModelPath), "", "", Path.Combine(root, "models"), Path.Combine(root, "last-run.log"));
    }

    public static EnginePaths ResolvePaths(ManagerConfig config)
    {
        var root = config.Strata.PackageRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("未找到 Strata 包目录，请先在 Strata 面板中选择。");
        root = Path.GetFullPath(root);
        var engineDir = Path.Combine(root, "engine");
        var strata = Path.Combine(engineDir, "strata.exe");
        var tokenize = Path.Combine(engineDir, "tokenize", "llama-tokenize.exe");
        var vocab = Path.Combine(engineDir, "vocab_bytes.tsv");
        var pack = ResolvePackDir(root, config.Strata.PackDir);
        var modelVocab = Path.Combine(pack, "vocab_bytes.tsv");
        if (File.Exists(modelVocab)) vocab = modelVocab;
        var profile = config.Strata.ProfilePath;
        if (string.IsNullOrWhiteSpace(profile))
        {
            var profiles = Directory.GetFiles(Path.Combine(root, "profile"), "*.bin");
            profile = profiles.Length == 1 ? profiles[0]
                : throw new InvalidOperationException("请选择与模型对应的 expert profile；不会从多个 profile 中任取一个。");
        }
        profile = Path.GetFullPath(profile);
        if (!File.Exists(profile)) throw new FileNotFoundException("未找到选择的 expert profile。", profile);
        if (!File.Exists(strata)) throw new FileNotFoundException("未找到 strata.exe。", strata);
        if (!File.Exists(tokenize)) throw new FileNotFoundException("未找到 llama-tokenize.exe。", tokenize);
        if (!File.Exists(vocab)) throw new FileNotFoundException("未找到 vocab_bytes.tsv。", vocab);
        if (!Directory.Exists(pack)) throw new DirectoryNotFoundException("未找到 pack 目录。请先用 iq_pack.py 生成 pack，或在设置中指定 pack 路径。");
        return new EnginePaths(root, strata, tokenize, vocab, pack, profile,
            Path.Combine(root, "models"), Path.Combine(root, "last-run.log"));
    }

    /// <summary>pack 目录解析优先级：用户指定 > root\pack\iq-native > root\pack > root 下的 pack-* 目录。</summary>
    private static string ResolvePackDir(string root, string? userPack)
    {
        if (!string.IsNullOrWhiteSpace(userPack))
        {
            if (!File.Exists(Path.Combine(userPack, "index.txt")) || !File.Exists(Path.Combine(userPack, "dense.bin")))
                throw new DirectoryNotFoundException("指定的 pack 不完整：需要 index.txt 和 dense.bin。");
            return Path.GetFullPath(userPack);
        }
        var nested = Path.Combine(root, "pack", "iq-native");
        if (Directory.Exists(nested)) return nested;
        var flat = Path.Combine(root, "pack");
        if (Directory.Exists(flat) && Directory.EnumerateFiles(flat, "*.bin").Any()) return flat;
        var candidates = Directory.EnumerateDirectories(root, "pack-*", SearchOption.TopDirectoryOnly)
            .Where(d => File.Exists(Path.Combine(d, "index.txt")) && File.Exists(Path.Combine(d, "dense.bin"))).ToArray();
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length > 1) throw new InvalidOperationException("检测到多个 pack，请明确选择与模型对应的目录。");
        return nested; // return the default path so the caller gets a meaningful error
    }

    /// <summary>未显式选择模型时取包内 models\ 下最大的 gguf（与 run.ps1 的规则一致）。</summary>
    public static string ResolveModel(ManagerConfig config, EnginePaths paths) =>
        string.IsNullOrWhiteSpace(config.Strata.ModelPath)
            ? Directory.EnumerateFiles(paths.ModelsDir, "*.gguf", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.Length).FirstOrDefault()?.FullName
                ?? throw new FileNotFoundException("包内 models\\ 目录没有 .gguf 模型。")
            : File.Exists(config.Strata.ModelPath) ? Path.GetFullPath(config.Strata.ModelPath)
                : throw new FileNotFoundException("未找到指定模型。", config.Strata.ModelPath);

    /// <summary>UI 展示用：包目录未配置或损坏时返回 null 而不是抛出。</summary>
    public static EnginePaths? ResolvePathsSafe(ManagerConfig config)
    {
        try { return ResolvePaths(config); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>有界探测可执行包；pack 与 profile 允许在包外单独选择。</summary>
    public static string? DetectPackageRoot()
    {
        var seeds = new List<string> { ConfigService.GetManagerRoot(), ConfigService.GetLlamaCppRoot() };
        var parent = Path.GetDirectoryName(ConfigService.GetManagerRoot());
        if (parent != null) seeds.Add(parent);
        var queue = new Queue<(string Path, int Depth)>();
        foreach (var seed in seeds.Distinct(StringComparer.OrdinalIgnoreCase)) queue.Enqueue((seed, 0));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0 && seen.Count < 256)
        {
            var (root, depth) = queue.Dequeue();
            if (!seen.Add(root) || !Directory.Exists(root)) continue;
            try
            {
                if (File.Exists(Path.Combine(root, "engine", "strata.exe"))) return root;
                if (depth < 3)
                    foreach (var child in Directory.EnumerateDirectories(root).Take(256 - seen.Count))
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) queue.Enqueue((child, depth + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static long EstimateVramFreeMib()
    {
        try
        {
            var start = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var process = Process.Start(start);
            if (process == null) return 0;
            var text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(15000);
            var first = text.Split('\n').FirstOrDefault()?.Trim();
            return long.TryParse(first, out var mib) ? mib : 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return 0; }
    }

    /// <summary>启动一次推理并等待完成。同一时间只允许一个 Strata 进程；返回时更新统计与 PID 状态。
    /// PID 记录的是 strata.exe 子进程（不是 GUI 自己），并带创建时间与引擎路径校验。</summary>
    public static async Task<StrataResult> RunAsync(ManagerConfig config, Action<string>? log = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!await RunGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("已有一个 Strata 推理或分词任务在运行，请先停止。");
        try
        {
            // Hold the lease through tokenization, execution and cleanup, including across GUI instances.
            using var lease = new FileStream(Path.Combine(ConfigService.GetManagerRoot(), "logs", "strata.run.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var existing = GetLiveProcess(ConfigService.ReadConfig().Strata);
            if (existing != null) throw new InvalidOperationException("已有一个 Strata 推理在运行，请先停止。");
            var snapshot = JsonSerializer.Deserialize<ManagerConfig>(JsonSerializer.Serialize(config))!;
            return await RunCoreAsync(snapshot, log, ct).ConfigureAwait(false);
        }
        finally { RunGate.Release(); }
    }

    public static ProcessStartInfo CreateEngineStart(ManagerConfig config, EnginePaths paths, string model, string tokensFile)
    {
        var s = config.Strata;
        if (s.Spec is < 2 or > 8) throw new ArgumentException("Spec 必须为 2-8（native pack 的验证窗口）。");
        if (s.ReserveMib is < 0 or > 60000 || s.Context is < 512 or > 1048576 ||
            s.MaxNew is < 1 or > 65536 || s.Prefill is < 128 or > 65536 || s.Workers is < 0 or > 256)
            throw new ArgumentException("Strata 数值参数超出范围。");
        if (s.MaxNew >= s.Context) throw new ArgumentException("生成长度必须小于上下文容量。");
        if (s.ArenaPinGib is < 0 or > 128) throw new ArgumentException("锁页内存上限必须为 0-128 GiB，0 为自动。");
        if (!new[] { "int8", "fp16", "k8v4", "q4_0" }.Contains(s.Kv) ||
            !new[] { "all", "auto", "p-cores" }.Contains(s.Affinity))
            throw new ArgumentException("Strata KV 或线程亲和模式无效。");
        var start = CaptureStart(paths.StrataExe);
        string[] arguments = ["--pack", paths.PackDir, "--native", model, "--no-ple", "--tokens-file", tokensFile,
            "--expert-profile", paths.ProfileBin, "--expert-cache", "auto", "--vram-reserve-mib", s.ReserveMib.ToString(),
            "--prefill", s.Prefill.ToString(), "--max-context", s.Context.ToString(), "--max-new", s.MaxNew.ToString(),
            "--kv", s.Kv, "--kv-resident", "0", "--spec", s.Spec.ToString(), "--suffix-draft", "0",
            "--pool-workers", s.Workers.ToString(), "--pool-affinity", s.Affinity, "--stats"];
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.Environment.Remove("STRATA_ARENA_PIN_GIB");
        if (s.ArenaPinGib > 0) start.Environment["STRATA_ARENA_PIN_GIB"] = s.ArenaPinGib.ToString();
        // Do not inherit a caller's MTP norm override when the GUI explicitly chose another mode.
        start.Environment.Remove("STRATA_MTP_PLAIN");
        if (s.MtpEnabled)
        {
            if (string.IsNullOrWhiteSpace(s.MtpDir)) throw new ArgumentException("请先选择 MTP runtime 目录。");
            var mtpDir = Path.GetFullPath(s.MtpDir);
            foreach (var file in new[] { "dense.bin", "dense.txt", "experts.bin" })
                if (!File.Exists(Path.Combine(mtpDir, file)))
                    throw new FileNotFoundException("MTP 目录不完整：缺少 " + file);
            start.ArgumentList.Add("--mtp");
            start.ArgumentList.Add(mtpDir);
            if (s.MtpPlain) start.Environment["STRATA_MTP_PLAIN"] = "1";
        }
        return start;
    }

    /// <summary>
    /// q36 原生路径（上游 0.1.44+）：直接读 GGUF，不需要 pack / profile / iq_pack。
    /// 参数与 cyber-on-strata 引擎不同：--tokens IDS、--mtp N（GGUF 内置草稿块）、--expert-vram / --cpu-threads。
    /// </summary>
    public static ProcessStartInfo CreateQ36EngineStart(ManagerConfig config, EnginePaths paths, string model, string tokens)
    {
        var s = config.Strata;
        if (s.Context is < 256 or > 1048576 || s.MaxNew is < 1 or > 65536 || s.Workers is < 0 or > 256)
            throw new ArgumentException("Strata 数值参数超出范围。");
        if (s.MaxNew >= s.Context) throw new ArgumentException("生成长度必须小于上下文容量。");
        if (s.MtpEnabled && s.Spec is < 1 or > 7) throw new ArgumentException("MTP 草稿数必须为 1-7。");
        var start = CaptureStart(paths.StrataExe);
        foreach (var arg in new[] { "--native", model, "--tokens", tokens, "--max-context", s.Context.ToString(),
            "--max-new", s.MaxNew.ToString(), "--pool-workers", s.Workers.ToString() })
            start.ArgumentList.Add(arg);
        if (s.Prefill > 0) { start.ArgumentList.Add("--prefill"); start.ArgumentList.Add(s.Prefill.ToString()); }
        if (s.MtpEnabled) { start.ArgumentList.Add("--mtp"); start.ArgumentList.Add(s.Spec.ToString()); }
        return start;
    }

    private static async Task<StrataResult> RunCoreAsync(ManagerConfig config, Action<string>? log, CancellationToken ct)
    {
        var model0 = config.Strata.ModelPath;
        var q36 = !string.IsNullOrWhiteSpace(model0) && GgufService.GetArchitecture(model0) == "qwen35moe" &&
            EngineSupportsQwen35(Path.Combine(Path.GetFullPath(config.Strata.PackageRoot), "engine", "strata.exe"));
        var paths = q36 ? ResolveQ36Paths(config) : ResolvePaths(config);
        var model = ResolveModel(config, paths);
        var prompt = config.Strata.Prompt;
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("提示词为空。");
        var work = Path.Combine(Path.GetTempPath(), "strata-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var promptFile = Path.Combine(work, "prompt.txt");
        var tokensFile = Path.Combine(work, "tokens.txt");
        var tokOut = Path.Combine(work, "tok.out");
        var tokErr = Path.Combine(work, "tok.err");
        var stdoutPath = Path.Combine(ConfigService.GetManagerRoot(), "logs", "strata.stdout.log");
        var stderrPath = Path.Combine(ConfigService.GetManagerRoot(), "logs", "strata.stderr.log");
        File.WriteAllText(promptFile, prompt, new UTF8Encoding(false));

        try
        {
            log?.Invoke("[Strata 1/3] 分词中…");
            var tokenizeStart = CaptureStart(paths.TokenizeExe);
            foreach (var arg in new[] { "-m", model, "-f", promptFile, "--ids" }) tokenizeStart.ArgumentList.Add(arg);
            var tokenized = await RunChildAsync(tokenizeStart, tokOut, tokErr, ct).ConfigureAwait(false);
            if (tokenized.ExitCode != 0)
                throw new InvalidOperationException($"分词失败（{tokenized.ExitCode}）：{tokenized.Stderr}");
            var raw = tokenized.Stdout;
            var match = Regex.Match(raw, @"\[[^\[\]]*\]");
            if (!match.Success)
                throw new InvalidOperationException("tokenizer 没有输出 token id（模型文件可能不兼容）。");
            var ids = string.Join(' ', match.Value.Replace("[", "").Replace("]", "")
                .Split([',', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var promptTokens = ids.Split(' ').Length;
            File.WriteAllText(tokensFile, ids);

            if (promptTokens + config.Strata.MaxNew > config.Strata.Context)
                throw new ArgumentException("提示词与生成长度之和超过上下文容量。");
            var s = config.Strata;
            if (q36)
            {
                // q36 输出 token id，需要 vocab 表反分词；按模型缓存，首次导出。
                var vocab = await StrataPackService.EnsureVocabAsync(
                    new StrataPackService.Options(paths.PackageRoot, model, s.PythonPath, s.PackCacheDirectory),
                    model, log, ct).ConfigureAwait(false);
                paths = paths with { VocabTsv = vocab };
            }
            var engineStart = q36
                ? CreateQ36EngineStart(config, paths, model, ids)
                : CreateEngineStart(config, paths, model, tokensFile);
            log?.Invoke($"[Strata 2/3] 推理中（reserve {s.ReserveMib} MiB, context {s.Context}, max-new {s.MaxNew}）…");

            ChildResult result;
            try
            {
                result = await RunChildAsync(engineStart, stdoutPath, stderrPath, ct, process =>
                    ConfigService.SaveStrataSession(config, process.Id, process.StartTime.ToUniversalTime().Ticks,
                        paths.StrataExe, running: true)).ConfigureAwait(false);
            }
            finally
            {
                ConfigService.SaveStrataSession(config, 0, 0, null, running: false);
            }
            var lines = result.Stdout.Split('\n').Concat(result.Stderr.Split('\n')).ToArray();
            try { File.WriteAllLines(paths.LastRunLog, lines); } catch (IOException) { }
            var outputLine = lines.LastOrDefault(l => Regex.IsMatch(l, q36 ? @"^\s*gen\s*:" : @"^\s*output\s*:"));
            if (outputLine == null || result.ExitCode != 0)
            {
                var tail = string.Join("\n", lines.Where(l => l.Length > 0).TakeLast(10));
                throw new InvalidOperationException($"引擎提前退出（{result.ExitCode}）。\n" + tail);
            }
            var answer = Detokenize(paths.VocabTsv, outputLine, q36);
            var stats = FormatStats(lines, promptTokens);
            log?.Invoke($"[Strata 3/3] 完成：{stats}");
            ConfigService.SaveStrataStats(stats);
            return new StrataResult(answer, stats, promptTokens, paths.LastRunLog);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static ProcessStartInfo CaptureStart(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        var engineDir = Path.GetDirectoryName(exe);
        var managerRoot = ConfigService.GetManagerRoot();
        var llamaCppRoot = ConfigService.GetLlamaCppRoot();
        var extraDirs = new[] { engineDir, managerRoot, llamaCppRoot }
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var extraPath = string.Join(";", extraDirs);
        if (!string.IsNullOrEmpty(extraPath))
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = extraPath + ";" + currentPath;
        }
        return psi;
    }

    internal sealed record ChildResult(int ExitCode, string Stdout, string Stderr);

    internal static async Task<ChildResult> RunChildAsync(ProcessStartInfo start, string outPath, string errPath,
        CancellationToken ct, Action<Process>? started = null, Action<string>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动进程：" + start.FileName);
        async Task<string> ReadAsync(StreamReader reader)
        {
            var text = new StringBuilder();
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                text.AppendLine(line);
                progress?.Invoke(line);
            }
            return text.ToString();
        }
        var stdout = ReadAsync(process.StandardOutput);
        var stderr = ReadAsync(process.StandardError);
        try
        {
            started?.Invoke(process);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ChildResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await File.WriteAllTextAsync(outPath, await stdout.ConfigureAwait(false)).ConfigureAwait(false);
            await File.WriteAllTextAsync(errPath, await stderr.ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    public static string DetokenizeForTest(string vocabTsv, string outputLine, bool q36) => Detokenize(vocabTsv, outputLine, q36);

    private static string Detokenize(string vocabTsv, string outputLine, bool q36 = false)
    {
        var ids = Regex.Replace(outputLine, q36 ? @"^\s*gen\s*:\s*" : @"^\s*output\s*:\s*", "")
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.All(char.IsDigit)).Select(int.Parse);
        var map = new Dictionary<int, string>();
        foreach (var line in File.ReadLines(vocabTsv))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var tab = line.IndexOf('\t');
            if (tab < 1) continue;
            if (int.TryParse(line.AsSpan(0, tab), out var id)) map[id] = line[(tab + 1)..];
        }
        var bytes = new List<byte>();
        foreach (var id in ids)
        {
            if (!map.TryGetValue(id, out var hex)) continue;
            for (var i = 0; i + 1 < hex.Length; i += 2) bytes.Add(Convert.ToByte(hex.Substring(i, 2), 16));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string FormatStats(IEnumerable<string> lines, int promptTokens)
    {
        string decode = "?", prefill = "?", cache = "", hits = "", pool = "";
        foreach (var line in lines)
        {
            var m = Regex.Match(line, @"decode\s+\d+ tokens in [\d.]+ ms\s+->\s+([\d.]+) tok/s");
            if (m.Success) decode = m.Groups[1].Value;
            m = Regex.Match(line, @"prefill\s+\d+ tokens in [\d.]+ ms\s+->\s+([\d.]+) tok/s");
            if (m.Success) prefill = m.Groups[1].Value;
            m = Regex.Match(line, @"expert cache auto:\s*(.*)$");
            if (m.Success) cache = m.Groups[1].Value.Trim();
            m = Regex.Match(line, @"(R4 expert-cache hits .*)$");
            if (m.Success) hits = m.Groups[1].Value.Trim();
            m = Regex.Match(line, @"(the CPU expert pool .*)$");
            if (m.Success) pool = m.Groups[1].Value.Trim();
        }
        return $"decode {decode} tok/s · prefill {prefill} tok/s（{promptTokens} prompt tokens）" +
            (cache.Length > 0 ? $"\ncache: {cache}" : "") +
            (hits.Length > 0 ? $"\nhits: {hits}" : "") +
            (pool.Length > 0 ? $"\ncpupool: {pool}" : "");
    }

    /// <summary>终止正在运行的 Strata 进程并清空 PID 记录。
    /// PID、创建时间和引擎路径三者一致才认为是我们启动的进程（防 PID 重用误杀）。</summary>
    public static bool Stop(ManagerConfig config, Action<string>? log = null)
    {
        var session = ConfigService.ReadConfig().Strata;
        if (session.LastPid <= 0)
        {
            log?.Invoke("当前没有正在运行的 Strata 推理。");
            return false;
        }
        Process? process = null;
        try
        {
            process = Process.GetProcessById(session.LastPid);
            if (process.HasExited ||
                process.StartTime.ToUniversalTime().Ticks != session.LastStartedUtcTicks ||
                !PathIdentity.Equals(process.MainModule?.FileName, session.LastEnginePath))
            {
                // not the process we started (PID reuse or a stale record): just clear the record
                process.Dispose();
                process = null;
                ConfigService.SaveStrataSession(config, 0, 0, null, running: false);
                log?.Invoke("记录的 Strata 进程已不存在（PID 被复用或已退出），已清理记录。");
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ConfigService.SaveStrataSession(config, 0, 0, null, running: false);
            log?.Invoke("记录的 Strata 进程已退出，已清理记录。");
            process?.Dispose();
            return false;
        }
        using (process)
        {
            log?.Invoke($"正在停止 Strata 推理，PID {process.Id}。");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10000);
        }
        ConfigService.SaveStrataSession(config, 0, 0, null, running: false);
        return true;
    }
}
