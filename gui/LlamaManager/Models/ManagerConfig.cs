using System.Text.Json.Serialization;

namespace LlamaManager.Models;

public class ManagerConfig
{
    /// <summary>
    /// Canonical session store, keyed by slot ("main", "embedding"). This is the only
    /// session structure written to disk. The pre-plugin keys (RunningServer /
    /// RunningEmbedding / LastPid / ...) are folded in by ConfigStore on read and are
    /// never written again (migrate-and-delete).
    /// </summary>
    [JsonPropertyName("Running")]
    public Dictionary<string, ServerSession> Running { get; set; } = new();

    /// <summary>The backend the launcher drives by default.</summary>
    [JsonPropertyName("ActiveBackendId")]
    public string ActiveBackendId { get; set; } = "llamacpp";

    /// <summary>Per-backend user settings (model, executable, port, free-form parameters).</summary>
    [JsonPropertyName("Engines")]
    public Dictionary<string, EngineSettings> Engines { get; set; } = new();

    /// <summary>The main (llama.cpp) session, if any. Computed; never serialized under this name.</summary>
    [JsonIgnore]
    public ServerSession? RunningServer => Running.GetValueOrDefault(SlotMain);

    /// <summary>The embedding session, if any. Computed; never serialized under this name.</summary>
    [JsonIgnore]
    public ServerSession? RunningEmbedding => Running.GetValueOrDefault(SlotEmbedding);

    public const string SlotMain = "main";
    public const string SlotEmbedding = "embedding";

    public ServerSession? GetRunning(string slot) => Running.GetValueOrDefault(slot);
    public void SetRunning(string slot, ServerSession? session)
    {
        if (session == null) Running.Remove(slot);
        else Running[slot] = session;
    }

    [JsonPropertyName("ModelPath")]
    public string ModelPath { get; set; } = string.Empty;

    [JsonPropertyName("ModelsDirectory")]
    public string ModelsDirectory { get; set; } = string.Empty;

    [JsonPropertyName("Args")]
    public string Args { get; set; } = string.Empty;

    [JsonPropertyName("Port")]
    public int Port { get; set; } = 8080;

    [JsonPropertyName("AutoStartEnabled")]
    public bool AutoStartEnabled { get; set; }

    [JsonPropertyName("AutoOpenBrowser")]
    public bool AutoOpenBrowser { get; set; } = false;

    /// <summary>Legacy PID-only recovery hint, retained only while no full session exists.
    /// Read for pre-plugin configs; never written once a canonical session exists.</summary>
    [JsonPropertyName("LastPid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LastPid { get; set; }

    [JsonPropertyName("LastStartedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastStartedAt { get; set; }

    /// <summary>Pre-plugin session key, folded into <see cref="Running"/> on read and never written again.</summary>
    [JsonPropertyName("RunningServer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerSession? LegacyRunningServer { get; set; }

    /// <summary>Pre-plugin embedding session key, folded into <see cref="Running"/> on read and never written again.</summary>
    [JsonPropertyName("RunningEmbedding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerSession? LegacyRunningEmbedding { get; set; }

    /// <summary>Folds legacy session keys into <see cref="Running"/> (migrate-and-delete). Idempotent.</summary>
    public void Normalize()
    {
        Window ??= new WindowSettings();
        if (!double.IsFinite(Window.Width) || Window.Width is < 640 or > 20000) Window.Width = 900;
        if (!double.IsFinite(Window.Height) || Window.Height is < 520 or > 20000) Window.Height = 720;
        if (Window.Left is { } left && !double.IsFinite(left)) Window.Left = null;
        if (Window.Top is { } top && !double.IsFinite(top)) Window.Top = null;

        if (LegacyRunningServer != null) { Running.TryAdd(SlotMain, LegacyRunningServer); LegacyRunningServer = null; }
        if (LegacyRunningEmbedding != null) { Running.TryAdd(SlotEmbedding, LegacyRunningEmbedding); LegacyRunningEmbedding = null; }
        // Once a full session exists, the PID-only recovery hints are redundant: drop them so
        // the next save no longer writes the pre-plugin keys.
        if (Running.ContainsKey(SlotMain)) { LastPid = 0; LastStartedAt = null; }
        if (Running.ContainsKey(SlotEmbedding)) { EmbeddingLastPid = 0; EmbeddingLastStartedAt = null; }
        foreach (var (_, settings) in Engines)
        {
            settings.ModelPresets ??= [];
            settings.Parameters ??= [];
            if (!string.IsNullOrWhiteSpace(settings.ModelPath) && settings.Parameters.Count > 0)
            {
                var norm = Services.PathIdentity.Normalize(settings.ModelPath);
                if (!settings.ModelPresets.ContainsKey(norm))
                {
                    settings.ModelPresets[norm] = new Dictionary<string, string>(settings.Parameters, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }

    [JsonPropertyName("CreatedAt")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("UpdatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("ModelPresets")]
    public Dictionary<string, ModelPreset> ModelPresets { get; set; } = [];

    [JsonPropertyName("EmbeddingModelPath")]
    public string EmbeddingModelPath { get; set; } = string.Empty;

    [JsonPropertyName("EmbeddingPort")]
    public int EmbeddingPort { get; set; } = 8081;

    [JsonPropertyName("EmbeddingLastPid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int EmbeddingLastPid { get; set; }

    [JsonPropertyName("EmbeddingLastStartedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EmbeddingLastStartedAt { get; set; }

    [JsonPropertyName("Strata")]
    public StrataConfig Strata { get; set; } = new();

    /// <summary>Window placement remembered between launches. Values are validated in Normalize().</summary>
    [JsonPropertyName("Window")]
    public WindowSettings Window { get; set; } = new();
}

public class WindowSettings
{
    [JsonPropertyName("Width")]
    public double Width { get; set; } = 900;

    [JsonPropertyName("Height")]
    public double Height { get; set; } = 720;

    [JsonPropertyName("Left")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Left { get; set; }

    [JsonPropertyName("Top")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Top { get; set; }

    [JsonPropertyName("Maximized")]
    public bool Maximized { get; set; }
}

/// <summary>Per-backend user settings. Model/executable/port are common; parameters are backend-defined.</summary>
public class EngineSettings
{
    [JsonPropertyName("ModelPath")]
    public string ModelPath { get; set; } = string.Empty;

    [JsonPropertyName("ExecutablePath")]
    public string ExecutablePath { get; set; } = string.Empty;

    [JsonPropertyName("Port")]
    public int Port { get; set; }

    [JsonPropertyName("Parameters")]
    public Dictionary<string, string> Parameters { get; set; } = new();

    [JsonPropertyName("ModelPresets")]
    public Dictionary<string, Dictionary<string, string>> ModelPresets { get; set; } = new();

    [JsonPropertyName("LastStats")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastStats { get; set; }
}

public class StrataConfig
{
    /// <summary>Strata 包根目录（包含 engine\strata.exe 的目录）。</summary>
    [JsonPropertyName("PackageRoot")]
    public string PackageRoot { get; set; } = string.Empty;

    /// <summary>Strata 源码根目录（包含 serve\server.py 的目录）；为空时回退到 PackageRoot。</summary>
    [JsonPropertyName("ServeRoot")]
    public string ServeRoot { get; set; } = string.Empty;

    /// <summary>服务监听地址；默认仅本机回环。</summary>
    [JsonPropertyName("Host")]
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>服务端口（HTTP API 端口）。</summary>
    [JsonPropertyName("ServePort")]
    public int ServePort { get; set; } = 8080;

    /// <summary>reasoning 预算（0 = 不限制）。</summary>
    [JsonPropertyName("ReasoningBudgetTokens")]
    public int ReasoningBudgetTokens { get; set; }

    /// <summary>Anthropic 请求未显式要求思考时的行为：model | on_request。</summary>
    [JsonPropertyName("AnthropicThinking")]
    public string AnthropicThinking { get; set; } = "model";

    /// <summary>pack 目录路径（可选；为空时自动探测 root\pack\iq-native 或 root 下的 pack-* 目录）。</summary>
    [JsonPropertyName("PackDir")]
    public string PackDir { get; set; } = string.Empty;

    public string ProfilePath { get; set; } = string.Empty;
    public int Spec { get; set; } = 2;
    public bool MtpEnabled { get; set; }
    public string MtpDir { get; set; } = string.Empty;
    /// <summary>APEX/Qwen MTP uses plain RMSNorm, rather than the donor HC path.</summary>
    public bool MtpPlain { get; set; } = true;
    public string PythonPath { get; set; } = string.Empty;
    public string PackCacheDirectory { get; set; } = string.Empty;
    /// <summary>Host RAM pinning cap, not GPU memory; 0 lets the engine decide.</summary>
    public int ArenaPinGib { get; set; }

    /// <summary>模型 .gguf 路径；为空时使用包内 models\ 下最大的 gguf。</summary>
    [JsonPropertyName("ModelPath")]
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>提示词模板（每次推理的输入）。</summary>
    [JsonPropertyName("Prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("ReserveMib")]
    public int ReserveMib { get; set; } = 1500;

    [JsonPropertyName("Context")]
    public int Context { get; set; } = 4096;

    [JsonPropertyName("MaxNew")]
    public int MaxNew { get; set; } = 256;

    [JsonPropertyName("Prefill")]
    public int Prefill { get; set; } = 2048;

    [JsonPropertyName("Kv")]
    public string Kv { get; set; } = "int8";

    [JsonPropertyName("Workers")]
    public int Workers { get; set; } = 0;

    [JsonPropertyName("Affinity")]
    public string Affinity { get; set; } = "auto";

    /// <summary>最近一次推理的统计（decode/prefill/hits/cpupool）。</summary>
    [JsonPropertyName("LastStats")]
    public string? LastStats { get; set; }

    /// <summary>当前/最近一次推理进程的 PID；0 表示没有进程在运行。</summary>
    [JsonPropertyName("LastPid")]
    public int LastPid { get; set; }

    /// <summary>该进程的创建时间（UTC ticks）：Stop 时防 PID 重用误杀。</summary>
    [JsonPropertyName("LastStartedUtcTicks")]
    public long LastStartedUtcTicks { get; set; }

    /// <summary>该进程的引擎路径：与 PID、创建时间共同构成进程身份。</summary>
    [JsonPropertyName("LastEnginePath")]
    public string LastEnginePath { get; set; } = string.Empty;
}

public class ModelPreset
{
    [JsonPropertyName("Args")]
    public string Args { get; set; } = string.Empty;

    [JsonPropertyName("Port")]
    public int Port { get; set; } = 8080;

    [JsonPropertyName("LastUsed")]
    public string LastUsed { get; set; } = string.Empty;

    /// <summary>文件签名（长度 + 头尾 hash），用于在路径变更后自动找回预设。</summary>
    [JsonPropertyName("Signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }
}
