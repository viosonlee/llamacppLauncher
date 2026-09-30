using System.Text.Json.Serialization;

namespace LlamaManager.Models;

public class ManagerConfig
{
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
