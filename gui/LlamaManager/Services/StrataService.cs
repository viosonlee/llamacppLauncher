using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>
/// Cyber on Strata 推理的独立生命周期。与 ServerService（llama-server HTTP 生命周期）完全分开：
/// Strata 是一次性的文本进/文本出进程，无 API 端口；两者共享显存，必须互斥运行。
/// </summary>
public static class StrataService
{
    public sealed record StrataResult(string Answer, string Stats, int PromptTokens, string LogPath);
    public sealed record EnginePaths(string PackageRoot, string StrataExe, string TokenizeExe, string VocabTsv, string PackDir, string ProfileBin, string ModelsDir, string LastRunLog);

    public static bool IsRunning(ManagerConfig config)
    {
        var session = ConfigService.ReadConfig().Strata;
        return session.LastPid > 0 && GetLiveProcess(session) != null;
    }

    private static Process? GetLiveProcess(StrataConfig config)
    {
        if (config.LastPid <= 0) return null;
        try
        {
            var process = Process.GetProcessById(config.LastPid);
            if (!process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == config.LastStartedUtcTicks &&
                PathIdentity.Equals(process.MainModule?.FileName, config.LastEnginePath))
                return process;
            process.Dispose();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return null;
    }

    public static EnginePaths ResolvePaths(ManagerConfig config)
    {
        var root = config.Strata.PackageRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("未找到 Strata 包目录，请先在 Strata 面板中选择。");
        var engineDir = Path.Combine(root, "engine");
        var strata = Path.Combine(engineDir, "strata.exe");
        var tokenize = Path.Combine(engineDir, "tokenize", "llama-tokenize.exe");
        var vocab = Path.Combine(engineDir, "vocab_bytes.tsv");
        var pack = ResolvePackDir(root, config.Strata.PackDir);
        var profile = Directory.GetFiles(Path.Combine(root, "profile"), "*.bin").FirstOrDefault()
            ?? throw new FileNotFoundException("profile 目录中没有 expert profile (*.bin)。");
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
        if (!string.IsNullOrWhiteSpace(userPack) && Directory.Exists(userPack))
            return userPack;
        var nested = Path.Combine(root, "pack", "iq-native");
        if (Directory.Exists(nested)) return nested;
        var flat = Path.Combine(root, "pack");
        if (Directory.Exists(flat) && Directory.EnumerateFiles(flat, "*.bin").Any()) return flat;
        foreach (var d in Directory.EnumerateDirectories(root, "pack-*", SearchOption.TopDirectoryOnly))
        {
            if (Directory.EnumerateFiles(d, "index.txt").Any() ||
                Directory.EnumerateFiles(d, "dense.bin").Any()) return d;
        }
        return nested; // return the default path so the caller gets a meaningful error
    }

    /// <summary>未显式选择模型时取包内 models\ 下最大的 gguf（与 run.ps1 的规则一致）。</summary>
    public static string ResolveModel(ManagerConfig config, EnginePaths paths) =>
        string.IsNullOrWhiteSpace(config.Strata.ModelPath) || !File.Exists(config.Strata.ModelPath)
            ? Directory.EnumerateFiles(paths.ModelsDir, "*.gguf", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.Length).FirstOrDefault()?.FullName
                ?? throw new FileNotFoundException("包内 models\\ 目录没有 .gguf 模型。")
            : config.Strata.ModelPath;

    /// <summary>UI 展示用：包目录未配置或损坏时返回 null 而不是抛出。</summary>
    public static EnginePaths? ResolvePathsSafe(ManagerConfig config)
    {
        try { return ResolvePaths(config); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or ArgumentException) { return null; }
    }

    /// <summary>包目录为空时自动探测：在 manager root 的兄弟/子目录里找 engine\strata.exe + pack 目录。</summary>
    public static string? DetectPackageRoot()
    {
        var seeds = new List<string> { ConfigService.GetManagerRoot(), ConfigService.GetLlamaCppRoot() };
        var parent = Path.GetDirectoryName(ConfigService.GetManagerRoot());
        if (parent != null) seeds.Add(parent);
        foreach (var seed in seeds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (seed == null || !Directory.Exists(seed)) continue;
            foreach (var strata in Directory.EnumerateFiles(seed, "strata.exe", SearchOption.AllDirectories))
            {
                try
                {
                    var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(strata)));
                    if (root == null) continue;
                    if (ResolvePackDir(root, null) is { } pack && Directory.Exists(pack) &&
                        Directory.GetFiles(Path.Combine(root, "profile"), "*.bin").Length > 0)
                        return root;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
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
            process.WaitForExit(5000);
            var first = text.Split('\n').FirstOrDefault()?.Trim();
            return long.TryParse(first, out var mib) ? mib : 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return 0; }
    }

    /// <summary>启动一次推理并等待完成。同一时间只允许一个 Strata 进程；返回时更新统计与 PID 状态。
    /// PID 记录的是 strata.exe 子进程（不是 GUI 自己），并带创建时间与引擎路径校验。</summary>
    public static async Task<StrataResult> RunAsync(ManagerConfig config, Action<string>? log = null, CancellationToken ct = default)
    {
        if (GetLiveProcess(ConfigService.ReadConfig().Strata) != null)
            throw new InvalidOperationException("已有一个 Strata 推理在运行，请先停止。");
        var paths = ResolvePaths(config);
        var model = ResolveModel(config, paths);
        var prompt = config.Strata.Prompt;
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("提示词为空。");
        var work = Path.Combine(Path.GetTempPath(), "strata-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var promptFile = Path.Combine(work, "prompt.txt");
        var tokensFile = Path.Combine(work, "tokens.txt");
        var tokOut = Path.Combine(work, "tok.out");
        var tokErr = Path.Combine(work, "tok.err");
        var engOut = Path.Combine(work, "eng.out");
        var engErr = Path.Combine(work, "eng.err");
        var stdoutPath = Path.Combine(ConfigService.GetManagerRoot(), "logs", "strata.stdout.log");
        var stderrPath = Path.Combine(ConfigService.GetManagerRoot(), "logs", "strata.stderr.log");
        File.WriteAllText(promptFile, prompt, new UTF8Encoding(false));

        try
        {
            log?.Invoke("[Strata 1/3] 分词中…");
            RunCapture(paths.TokenizeExe, $"-m \"{model}\" -f \"{promptFile}\" --ids", tokOut, tokErr);            var raw = File.Exists(tokOut) ? File.ReadAllText(tokOut) : "";
            var match = Regex.Match(raw, @"\[[^\[\]]*\]");
            if (!match.Success)
                throw new InvalidOperationException("tokenizer 没有输出 token id（模型文件可能不兼容）。");
            var ids = string.Join(' ', match.Value.Replace("[", "").Replace("]", "")
                .Split([',', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var promptTokens = ids.Split(' ').Length;
            File.WriteAllText(tokensFile, ids);

            var s = config.Strata;
            var argv = $"--pack \"{paths.PackDir}\" --native \"{model}\" --no-ple --tokens-file \"{tokensFile}\" " +
                $"--expert-profile \"{paths.ProfileBin}\" --expert-cache auto --vram-reserve-mib {s.ReserveMib} " +
                $"--prefill {s.Prefill} --max-context {s.Context} --max-new {s.MaxNew} --kv {s.Kv} " +
                $"--spec 2 --suffix-draft 0 --pool-workers {s.Workers} --pool-affinity {s.Affinity} --stats";
            log?.Invoke($"[Strata 2/3] 推理中（reserve {s.ReserveMib} MiB, context {s.Context}, max-new {s.MaxNew}）…");

            var engineStart = new ProcessStartInfo(paths.StrataExe, argv)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(paths.StrataExe)!
            };
            using var process = Process.Start(engineStart) ?? throw new InvalidOperationException("无法启动 strata.exe");
            // the real engine identity: the PID the Stop button kills, checked against creation time + path
            ConfigService.SaveStrataSession(config, process.Id, process.StartTime.ToUniversalTime().Ticks, paths.StrataExe, running: true);
            string stdout, stderr;
            try
            {
                var readOut = Task.Run(() => ReadPump(process, process.StandardOutput, engOut, stdoutPath), ct);
                var readErr = Task.Run(() => ReadPump(process, process.StandardError, engErr, stderrPath), ct);
                var exited = await Task.Run(() =>
                {
                    try { process.WaitForExit(); }
                    catch (Exception) when (ct.IsCancellationRequested) { }
                    return process.HasExited;
                }, ct).ConfigureAwait(false);
                if (!exited)
                {
                    try { process.Kill(entireProcessTree: true); } catch (System.ComponentModel.Win32Exception) { }
                    throw new OperationCanceledException("Strata 推理已取消。");
                }
                stdout = await readOut.ConfigureAwait(false);
                stderr = await readErr.ConfigureAwait(false);
            }
            finally
            {
                ConfigService.SaveStrataSession(config, 0, 0, null, running: false);
            }
            var lines = stdout.Split('\n').Concat(stderr.Split('\n'));
            try { File.WriteAllLines(paths.LastRunLog, lines); } catch (IOException) { }
            var outputLine = lines.LastOrDefault(l => Regex.IsMatch(l, @"^\s*output\s*:"));
            if (outputLine == null)
            {
                var tail = string.Join("\n", lines.Where(l => l.Length > 0).TakeLast(10));
                throw new InvalidOperationException($"引擎提前退出（{process.ExitCode}）。\n" + tail);
            }
            var answer = Detokenize(paths.VocabTsv, outputLine);
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

    /// <summary>短生命周期的分词子进程：同步等待，输出直接落盘。</summary>
    private static void RunCapture(string exe, string arguments, string outPath, string errPath)
    {
        var start = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动进程：" + exe);
        process.OutputDataReceived += (_, e) => { if (e.Data != null) File.AppendAllText(outPath, e.Data + "\n"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) File.AppendAllText(errPath, e.Data + "\n"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
    }

    private static string ReadPump(Process process, StreamReader reader, string tempPath, string logPath)
    {
        var buffer = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lock (buffer) buffer.AppendLine(line);
            try { File.AppendAllText(tempPath, line + "\n"); File.AppendAllText(logPath, line + "\n"); }
            catch (IOException) { }
        }
        process.WaitForExit();   // drain buffers after exit, as Process.BeginOutputReadLine would
        lock (buffer) return buffer.ToString();
    }

    private static string Detokenize(string vocabTsv, string outputLine)
    {
        var ids = outputLine.Replace("output:", "").Trim()
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
