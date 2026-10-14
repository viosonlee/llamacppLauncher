using System.Text.Json.Serialization;

namespace LlamaManager.Models;

public class ManagerConfig
{
    public ServerSession? RunningServer { get; set; }
    public ServerSession? RunningEmbedding { get; set; }
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

    [JsonPropertyName("LastPid")]
    public int LastPid { get; set; }

    [JsonPropertyName("LastStartedAt")]
    public string LastStartedAt { get; set; } = string.Empty;

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
    public int EmbeddingLastPid { get; set; }

    [JsonPropertyName("EmbeddingLastStartedAt")]
    public string EmbeddingLastStartedAt { get; set; } = string.Empty;

    [JsonPropertyName("Strata")]
    public StrataConfig Strata { get; set; } = new();
}

public class StrataConfig
{
    /// <summary>Strata 包根目录（包含 engine\strata.exe 的目录）。</summary>
    [JsonPropertyName("PackageRoot")]
    public string PackageRoot { get; set; } = string.Empty;

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
