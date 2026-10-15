using System.Text.Json.Serialization;

namespace LlamaManager.Engines;

/// <summary>
/// A backend declared by a manifest (<c>plugin.json</c>), so new engines can be added
/// without recompiling the launcher. The launcher only launches the process and probes it;
/// it never loads the plugin's code. Schema version is checked on load.
/// </summary>
public sealed class PluginManifest
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("gpuHeavy")]
    public bool GpuHeavy { get; set; } = true;

    /// <summary>Executable name per OS: { "windows": "strata-poly.exe", "linux": "strata-poly" }.</summary>
    [JsonPropertyName("executable")]
    public Dictionary<string, string> Executable { get; set; } = new();

    [JsonPropertyName("detect")]
    public DetectSpec? Detect { get; set; }

    [JsonPropertyName("launch")]
    public LaunchSpec Launch { get; set; } = new();

    [JsonPropertyName("ready")]
    public ReadySpec Ready { get; set; } = new();

    [JsonPropertyName("parameters")]
    public List<ParameterManifest> Parameters { get; set; } = new();

    [JsonPropertyName("logs")]
    public LogSpec? Logs { get; set; }

    [JsonPropertyName("compatibility")]
    public CompatibilitySpec? Compatibility { get; set; }

    public sealed class DetectSpec
    {
        [JsonPropertyName("versionArg")]
        public List<string> VersionArg { get; set; } = new();

        [JsonPropertyName("versionRegex")]
        public string VersionRegex { get; set; } = "";
    }

    public sealed class LaunchSpec
    {
        /// <summary>Working directory; {installDir} is replaced by the plugin's own folder.</summary>
        [JsonPropertyName("workingDirectory")]
        public string WorkingDirectory { get; set; } = "{installDir}";

        /// <summary>Argument template; {model}, {port}, {context}, and parameter keys are substituted.</summary>
        [JsonPropertyName("arguments")]
        public List<string> Arguments { get; set; } = new();

        [JsonPropertyName("environment")]
        public Dictionary<string, string> Environment { get; set; } = new();
    }

    public sealed class ReadySpec
    {
        /// <summary>"http" or "tcp".</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "http";

        [JsonPropertyName("path")]
        public string Path { get; set; } = "/health";

        [JsonPropertyName("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 180;
    }

    public sealed class ParameterManifest
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("group")]
        public string Group { get; set; } = "基本";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "Text";

        [JsonPropertyName("default")]
        public string? Default { get; set; }

        [JsonPropertyName("tooltip")]
        public string? ToolTip { get; set; }

        [JsonPropertyName("min")]
        public double? Min { get; set; }

        [JsonPropertyName("max")]
        public double? Max { get; set; }

        [JsonPropertyName("choices")]
        public List<string>? Choices { get; set; }

        public ParameterSpec ToParameterSpec() => new(
            Key, string.IsNullOrWhiteSpace(Label) ? Key : Label, Group,
            Enum.TryParse<ParamType>(Type, ignoreCase: true, out var parsed) ? parsed : ParamType.Text,
            Default, ToolTip, Min, Max, Choices?.ToArray(), null);
    }

    public sealed class LogSpec
    {
        [JsonPropertyName("perfRegex")]
        public Dictionary<string, string> PerfRegex { get; set; } = new();
    }

    public sealed class CompatibilitySpec
    {
        /// <summary>"none" | "strata-matrix" | "polystrata-matrix".</summary>
        [JsonPropertyName("driver")]
        public string Driver { get; set; } = "none";
    }

    /// <summary>Whether the manifest is structurally complete enough to register.</summary>
    public string? Validate()
    {
        if (Schema != 1) return $"schema 版本 {Schema} 不受支持（需要 1）。";
        if (string.IsNullOrWhiteSpace(Id)) return "缺少 id。";
        if (string.IsNullOrWhiteSpace(DisplayName)) return "缺少 displayName。";
        if (Executable.Count == 0) return "缺少 executable。";
        if (Launch.Arguments.Count == 0) return "缺少 launch.arguments。";
        return null;
    }

    /// <summary>The executable for the current OS, or null when the manifest has none for it.</summary>
    public string? ExecutableForCurrentOs() =>
        Executable.GetValueOrDefault(OperatingSystem.IsWindows() ? "windows" : "linux");
}
