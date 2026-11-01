using System.IO;
using LlamaManager.Models;

namespace LlamaManager.Services;

public static class ConfigService
{
    public sealed record PresetRebindSnapshot(
        string ModelPath,
        IReadOnlyList<KeyValuePair<string, ModelPreset>> Presets);

    public sealed record PresetMove(
        string FromKey,
        string ToKey,
        string ModelPath,
        ModelPreset Original,
        ModelPreset Rebound);

    public sealed record PresetSignatureUpgrade(string Key, string Signature);

    public sealed record PresetRebindPlan(
        IReadOnlyList<PresetMove> Moves,
        IReadOnlyList<PresetSignatureUpgrade> SignatureUpgrades);

    private static readonly string ManagerRoot;
    private static readonly string LlamaCppRoot;
    private static readonly string LogsDir;
    private static readonly string ConfigPath;
    private static readonly ConfigStore Store;
    public static string? RecoveryWarning => Store.RecoveryWarning;

    static ConfigService()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        ManagerRoot = Environment.GetEnvironmentVariable("LLAMA_MANAGER_ROOT") is { Length: > 0 } root
            ? Path.GetFullPath(root) : FindParentWithFile(exeDir, "llama-manager.ps1", maxDepth: 8) ?? exeDir;

        LlamaCppRoot = FindParentWithFile(ManagerRoot, "llama-server.exe", maxDepth: 4)
                    ?? FindParentWithFile(exeDir, "llama-server.exe", maxDepth: 4)
                    ?? ManagerRoot;

        LogsDir = Path.Combine(ManagerRoot, "logs");
        Directory.CreateDirectory(LogsDir);

        ConfigPath = Path.Combine(ManagerRoot, "llama-manager.config.json");
        Store = new ConfigStore(ConfigPath);
    }

    private static string? FindParentWithFile(string startDir, string targetFile, int maxDepth)
    {
        var dir = startDir;
        for (int i = 0; i < maxDepth; i++)
        {
            if (File.Exists(Path.Combine(dir, targetFile)))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir)
                break;
            dir = parent;
        }
        return null;
    }

    public static string GetManagerRoot() => ManagerRoot;
    public static string GetLlamaCppRoot() => LlamaCppRoot;
    public static string GetRoot() => LlamaCppRoot;
    public static string GetPrismServerPath() => Path.Combine(LlamaCppRoot, "engines", "prism", "llama-server.exe");
    public static bool HasPrismServer() => File.Exists(GetPrismServerPath());

    public static bool IsPrismModel(string? modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return false;
        if (GgufService.TryRead(modelPath) is { } metadata) return metadata.RequiresPrism;
        var name = Path.GetFileName(modelPath);
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"bonsai|ptq|ternary", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static string GetLlamaServerPath(string? modelPath = null)
    {
        if (IsPrismModel(modelPath))
        {
            var prismPath = GetPrismServerPath();
            if (File.Exists(prismPath))
                return prismPath;
        }
        return Path.Combine(LlamaCppRoot, "llama-server.exe");
    }
    public static string GetLogPath() => Path.Combine(LogsDir, "llama-manager.log");
    public static string GetMainStdoutPath() => Path.Combine(LogsDir, "llama-server-main.stdout.log");
    public static string GetMainStderrPath() => Path.Combine(LogsDir, "llama-server-main.stderr.log");
    public static string GetEmbeddingStdoutPath() => Path.Combine(LogsDir, "llama-server-embedding.stdout.log");
    public static string GetEmbeddingStderrPath() => Path.Combine(LogsDir, "llama-server-embedding.stderr.log");

    public static ManagerConfig ReadConfig() => Store.Read();

    // Settings transactions deliberately preserve the independently persisted running sessions.
    public static void SaveConfig(ManagerConfig config)
    {
        Store.Update(latest =>
        {
            latest.ModelPath = config.ModelPath;
            latest.ModelsDirectory = config.ModelsDirectory;
            latest.Args = config.Args;
            latest.Port = config.Port;
            latest.ModelPresets = config.ModelPresets;
            latest.AutoOpenBrowser = config.AutoOpenBrowser;
            latest.EmbeddingModelPath = config.EmbeddingModelPath;
            latest.EmbeddingPort = config.EmbeddingPort;
            latest.Window = config.Window ?? new WindowSettings();
            if (string.IsNullOrEmpty(latest.CreatedAt)) latest.CreatedAt = DateTime.Now.ToString("o");
            latest.UpdatedAt = DateTime.Now.ToString("o");
        });
    }

    public static void SaveSession(ManagerConfig config, ServerSession? session, bool embedding = false)
    {
        var slot = embedding ? ManagerConfig.SlotEmbedding : ManagerConfig.SlotMain;
        Store.Update(target => target.SetRunning(slot, session));
        config.SetRunning(slot, session);
    }

    /// <summary>Persists a session under an explicit slot (a backend id, "main", or "embedding").
    /// Plugin/Strata backends keep their own slot so they never masquerade as llama.cpp.</summary>
    public static void SaveSessionSlot(ManagerConfig config, string slot, ServerSession? session)
    {
        Store.Update(target => target.SetRunning(slot, session));
        config.SetRunning(slot, session);
    }

    public static bool MigrateSession(ManagerConfig config, ServerSession session)
    {
        var slot = session.Embedding ? ManagerConfig.SlotEmbedding : ManagerConfig.SlotMain;
        var migrated = false;
        Store.Update(latest =>
        {
            // Only migrate when the slot is empty and the recorded PID matches (never overwrite a live session).
            if (latest.GetRunning(slot) != null) return;
            var pid = session.Embedding ? latest.EmbeddingLastPid : latest.LastPid;
            if (pid != session.ProcessId) return;
            latest.SetRunning(slot, session);
            migrated = true;
        });
        if (migrated) config.SetRunning(slot, session);
        return migrated;
    }

    /// <summary>
    /// Replaces a dead recorded session with a live process that fully matches it
    /// (same engine, model, port and flags). Only the recorded session may be replaced;
    /// foreign processes are never adopted.
    /// </summary>
    public static bool AdoptSession(ManagerConfig config, ServerSession recorded, ServerSession live)
    {
        if (recorded.Embedding != live.Embedding) return false;
        var slot = live.Embedding ? ManagerConfig.SlotEmbedding : ManagerConfig.SlotMain;
        var adopted = false;
        Store.Update(latest =>
        {
            if (!Equals(latest.GetRunning(slot), recorded)) return;
            latest.SetRunning(slot, live);
            adopted = true;
        });
        if (adopted) config.SetRunning(slot, live);
        return adopted;
    }

    public static void SaveAutostartSetting(bool enabled) => Store.Update(latest => latest.AutoStartEnabled = enabled);

    /// <summary>Persists the default backend without touching sessions or other settings.</summary>
    public static void SaveActiveBackend(ManagerConfig config, string backendId)
    {
        Store.Update(latest => latest.ActiveBackendId = backendId);
        config.ActiveBackendId = backendId;
    }

    /// <summary>Persists one backend's settings without touching other backends or live sessions.</summary>
    public static void SaveEngineSettings(ManagerConfig config, string backendId, EngineSettings settings)
    {
        Store.Update(latest =>
        {
            latest.Engines[backendId] = settings;
            Touch(latest);
        });
        config.Engines[backendId] = settings;
    }

    public static void SaveStrata(ManagerConfig config)
    {
        Store.Update(latest =>
        {
            // Persist edits without writing a stale GUI copy over the live process.
            var s = config.Strata;
            var target = latest.Strata;
            target.PackageRoot = s.PackageRoot;
            target.ServeRoot = s.ServeRoot;
            target.Host = s.Host;
            target.ServePort = s.ServePort;
            target.ReasoningBudgetTokens = s.ReasoningBudgetTokens;
            target.AnthropicThinking = s.AnthropicThinking;
            target.PackDir = s.PackDir;
            target.ProfilePath = s.ProfilePath;
            target.ModelPath = s.ModelPath;
            target.Prompt = s.Prompt;
            target.ReserveMib = s.ReserveMib;
            target.Context = s.Context;
            target.MaxNew = s.MaxNew;
            target.Prefill = s.Prefill;
            target.Kv = s.Kv;
            target.Workers = s.Workers;
            target.Affinity = s.Affinity;
            target.Spec = s.Spec;
            target.MtpEnabled = s.MtpEnabled;
            target.MtpDir = s.MtpDir;
            target.MtpPlain = s.MtpPlain;
            target.PythonPath = s.PythonPath;
            target.PackCacheDirectory = s.PackCacheDirectory;
            target.ArenaPinGib = s.ArenaPinGib;
            Touch(latest);
        });
    }

    /// <summary>记录/清除 Strata 推理进程的身份（PID + 创建时间 + 引擎路径）；running=false 时清零。不触碰其他设置。</summary>
    public static void SaveStrataSession(ManagerConfig config, int pid, long startedUtcTicks, string? enginePath, bool running)
        => Store.Update(latest =>
        {
            latest.Strata.LastPid = running ? pid : 0;
            latest.Strata.LastStartedUtcTicks = running ? startedUtcTicks : 0;
            latest.Strata.LastEnginePath = running ? enginePath ?? "" : "";
        });

    public static void SaveStrataStats(string stats)
        => Store.Update(latest => { latest.Strata.LastStats = stats; });

    private static void Touch(ManagerConfig latest)
    {
        if (string.IsNullOrEmpty(latest.CreatedAt)) latest.CreatedAt = DateTime.Now.ToString("o");
        latest.UpdatedAt = DateTime.Now.ToString("o");
    }

    public static ModelPreset? GetModelPreset(ManagerConfig config, string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;
        var key = NormalizePath(modelPath);
        if (config.ModelPresets.TryGetValue(key, out var preset))
            return preset;

        // 路径未命中：尝试把孤儿预设（原文件已移动/改名）重绑定到该模型
        if (RebindOrphanPresets(config, [modelPath], save: false) > 0
            && config.ModelPresets.TryGetValue(key, out preset))
            return preset;

        return null;
    }

    public static void SaveModelPreset(ManagerConfig config, string modelPath, string args, int port)
    {
        if (string.IsNullOrEmpty(modelPath)) return;
        var key = NormalizePath(modelPath);
        config.ModelPresets[key] = new ModelPreset
        {
            Args = args,
            Port = port,
            LastUsed = DateTime.Now.ToString("o"),
            Signature = FileSignature.Compute(modelPath)
        };
    }

    /// <summary>
    /// 把 key 指向已不存在文件的预设（模型被移动/改名/删除）重新绑定到现有的模型文件。
    /// 匹配依据：文件签名（最强）→ 文件名 + 尾部目录段相似度。
    /// </summary>
    public static int RebindOrphanPresets(ManagerConfig config, IEnumerable<string> modelPaths, bool save)
        => ApplyOrphanPresetRebind(
            config,
            PlanOrphanPresetRebind(CreatePresetRebindSnapshot(config), modelPaths),
            save);

    /// <summary>
    /// Copy the small preset map on the dispatcher. The returned objects are detached clones, so
    /// expensive signature/scoring work can run on a worker without reading the live config.
    /// </summary>
    public static PresetRebindSnapshot CreatePresetRebindSnapshot(ManagerConfig config)
        => new(
            config.ModelPath,
            (config.ModelPresets ?? [])
                .Select(kv => new KeyValuePair<string, ModelPreset>(kv.Key, ClonePreset(kv.Value)))
                .ToList());

    /// <summary>
    /// Pure, potentially slow part of orphan rebinding. It never mutates <paramref name="snapshot"/>.
    /// </summary>
    public static PresetRebindPlan PlanOrphanPresetRebind(
        PresetRebindSnapshot snapshot,
        IEnumerable<string> modelPaths)
    {
        if (snapshot.Presets.Count == 0) return new PresetRebindPlan([], []);

        var models = modelPaths
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (models.Count == 0) return new PresetRebindPlan([], []);

        // Work on detached copies. This also models the old sequential removal of moved orphans.
        var working = snapshot.Presets
            .Where(kv => kv.Value != null)
            .ToDictionary(kv => kv.Key, kv => ClonePreset(kv.Value), StringComparer.Ordinal);
        var orphans = working
            .Where(kv => string.IsNullOrEmpty(kv.Key) || !File.Exists(kv.Key))
            .ToList();
        if (orphans.Count == 0) return new PresetRebindPlan([], []);

        var needSignature = orphans.Any(kv => !string.IsNullOrEmpty(kv.Value.Signature));
        var signatures = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (needSignature)
            foreach (var m in models)
                signatures[m] = FileSignature.Compute(m);

        var moves = new List<PresetMove>();
        foreach (var model in models)
        {
            var targetKey = NormalizePath(model);
            if (working.ContainsKey(targetKey)) continue;

            var sig = needSignature && signatures.TryGetValue(model, out var s) ? s : null;
            string? bestKey = null;
            ModelPreset? bestPreset = null;
            var bestScore = 0;

            foreach (var kv in orphans)
            {
                if (!working.ContainsKey(kv.Key)) continue; // 已被其他模型占用（已迁走）
                if (string.IsNullOrEmpty(kv.Value.Signature) &&
                    models.Count(m => string.Equals(Path.GetFileName(m), Path.GetFileName(kv.Key), StringComparison.OrdinalIgnoreCase)) != 1)
                    continue;
                var score = ScoreOrphan(kv.Key, kv.Value, model, sig);
                if (score > bestScore
                    || (score == bestScore && score > 0
                        && string.CompareOrdinal(kv.Value.LastUsed, bestPreset?.LastUsed) > 0))
                {
                    bestScore = score;
                    bestKey = kv.Key;
                    bestPreset = kv.Value;
                }
            }

            if (bestKey == null || bestPreset == null) continue;

            var rebound = ClonePreset(bestPreset);
            rebound.Signature = FileSignature.Compute(model) ?? rebound.Signature;
            working.Remove(bestKey);
            working[targetKey] = rebound;
            moves.Add(new PresetMove(bestKey, targetKey, model, ClonePreset(bestPreset), rebound));
        }

        // 给还没有签名的预设补签名（仅首次，后续直接命中缓存）
        var upgrades = new List<PresetSignatureUpgrade>();
        foreach (var model in models)
        {
            if (!working.TryGetValue(NormalizePath(model), out var p)
                || !string.IsNullOrEmpty(p.Signature))
                continue;
            var s = FileSignature.Compute(model);
            if (s != null) upgrades.Add(new PresetSignatureUpgrade(NormalizePath(model), s));
        }

        return new PresetRebindPlan(moves, upgrades);
    }

    /// <summary>
    /// Applies a precomputed plan. With <paramref name="save"/> this is one serialized config
    /// transaction and merges with presets added while the scan was running; callers should invoke
    /// it on the dispatcher because it also updates the in-memory config object.
    /// </summary>
    public static int ApplyOrphanPresetRebind(
        ManagerConfig config,
        PresetRebindPlan plan,
        bool save)
    {
        if (plan.Moves.Count == 0 && plan.SignatureUpgrades.Count == 0) return 0;

        (int Moved, bool Changed) applied;
        if (save)
        {
            applied = default;
            var saved = Store.Update(latest => applied = ApplyPlan(latest, plan));
            // Preset rebinding touches only this slice; never replace unrelated GUI settings with a
            // stale copy while merging the transaction result back into the in-memory config.
            config.ModelPresets = saved.ModelPresets;
            config.ModelPath = saved.ModelPath;
        }
        else
        {
            applied = ApplyPlan(config, plan);
        }

        return applied.Moved;
    }

    private static (int Moved, bool Changed) ApplyPlan(ManagerConfig target, PresetRebindPlan plan)
    {
        target.ModelPresets ??= [];
        var moved = 0;
        foreach (var move in plan.Moves)
        {
            // The target may have received a user preset after planning; that newer value wins.
            if (target.ModelPresets.ContainsKey(move.ToKey)) continue;
            if (!target.ModelPresets.TryGetValue(move.FromKey, out var current)
                || !PresetEquals(current, move.Original))
                continue;

            target.ModelPresets.Remove(move.FromKey);
            target.ModelPresets[move.ToKey] = ClonePreset(move.Rebound);
            if (PathIdentity.Equals(target.ModelPath, move.FromKey)) target.ModelPath = move.ModelPath;
            moved++;
            LogService.Write($"检测到模型文件变更，已恢复预设: {Path.GetFileName(move.ModelPath)}");
        }

        var upgraded = 0;
        foreach (var upgrade in plan.SignatureUpgrades)
        {
            if (!target.ModelPresets.TryGetValue(upgrade.Key, out var preset)
                || !string.IsNullOrEmpty(preset.Signature))
                continue;
            preset.Signature = upgrade.Signature;
            upgraded++;
        }

        if (moved > 0 || upgraded > 0) Touch(target);
        return (moved, moved > 0 || upgraded > 0);
    }

    private static ModelPreset ClonePreset(ModelPreset preset) => new()
    {
        Args = preset.Args,
        Port = preset.Port,
        LastUsed = preset.LastUsed,
        Signature = preset.Signature
    };

    private static bool PresetEquals(ModelPreset a, ModelPreset b)
        => a.Port == b.Port
           && string.Equals(a.Args, b.Args, StringComparison.Ordinal)
           && string.Equals(a.LastUsed, b.LastUsed, StringComparison.Ordinal)
           && string.Equals(a.Signature, b.Signature, StringComparison.Ordinal);

    private static int ScoreOrphan(string orphanKey, ModelPreset preset, string modelPath, string? signature)
    {
        if (!string.IsNullOrEmpty(preset.Signature) && signature != null &&
            !string.Equals(preset.Signature, signature, StringComparison.Ordinal)) return -1;
        var nameScore = 0;
        if (!string.IsNullOrEmpty(preset.Signature)
            && signature != null
            && string.Equals(preset.Signature, signature, StringComparison.Ordinal))
        {
            nameScore = 1000; // 内容签名完全一致：绝对可信
        }
        else
        {
            var orphanName = Path.GetFileName(orphanKey);
            if (string.IsNullOrEmpty(orphanName)
                || !string.Equals(orphanName, Path.GetFileName(modelPath), StringComparison.OrdinalIgnoreCase))
                return -1;
            nameScore = 1;
        }

        return nameScore + TrailingSegmentScore(orphanKey, modelPath);
    }

    /// <summary>从尾部开始比较目录段（不含文件名）的相同个数。</summary>
    private static int TrailingSegmentScore(string a, string b)
    {
        var sa = a.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var sb = b.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        int i = sa.Length - 2, j = sb.Length - 2, score = 0;
        while (i >= 0 && j >= 0)
        {
            if (!string.Equals(sa[i], sb[j], StringComparison.OrdinalIgnoreCase)) break;
            score++;
            i--;
            j--;
        }
        return score;
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).ToLowerInvariant();
}
