using System.Text.Json.Serialization;

namespace LlamaManager.Models;

public class ManagerConfig
{
    [JsonPropertyName("ModelPath")]
    public string ModelPath { get; set; } = string.Empty;

    [JsonPropertyName("Args")]
    public string Args { get; set; } = string.Empty;

    [JsonPropertyName("Port")]
    public int Port { get; set; } = 8080;

    [JsonPropertyName("AutoStartEnabled")]
    public bool AutoStartEnabled { get; set; }

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
}

public class ModelPreset
{
    [JsonPropertyName("Args")]
    public string Args { get; set; } = string.Empty;

    [JsonPropertyName("Port")]
    public int Port { get; set; } = 8080;

    [JsonPropertyName("LastUsed")]
    public string LastUsed { get; set; } = string.Empty;
}
