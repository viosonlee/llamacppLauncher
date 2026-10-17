using System.Text.Json;
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

        /// <summary>Either a bare value ("model") or an object ("value", "label", "note", "recommended").</summary>
        [JsonPropertyName("choices")]
        public List<ChoiceManifest>? Choices { get; set; }

        /// <summary>Long explanation shown in the help popup; falls back to <see cref="ToolTip"/>.</summary>
        [JsonPropertyName("help")]
        public string? Help { get; set; }

        /// <summary>Unit suffix shown after the box, for example "token" or "MiB".</summary>
        [JsonPropertyName("unit")]
        public string? Unit { get; set; }

        /// <summary>True when the field is an editable free-text box even though it has choices.</summary>
        [JsonPropertyName("editable")]
        public bool Editable { get; set; }

        /// <summary>True to place the field with the advanced settings.</summary>
        [JsonPropertyName("advanced")]
        public bool Advanced { get; set; }

        /// <summary>Overrides the derived "min–max" hint.</summary>
        [JsonPropertyName("rangeHint")]
        public string? RangeHint { get; set; }

        /// <summary>A mini-expression that decides whether the field is shown (see ParameterVisibility).</summary>
        [JsonPropertyName("visibleWhen")]
        public string? VisibleWhen { get; set; }

        public ParameterSpec ToParameterSpec() => new(
            Key, string.IsNullOrWhiteSpace(Label) ? Key : Label, Group,
            Enum.TryParse<ParamType>(Type, ignoreCase: true, out var parsed) ? parsed : ParamType.Text,
            Default, ToolTip, Min, Max,
            Choices is { Count: > 0 } ? Choices.Select(c => c.ToParamChoice()).ToArray() : null,
            VisibleWhen, Help, Unit, Editable, Advanced, RangeHint);
    }

    /// <summary>
    /// One choice of a Choice parameter. A manifest may write it as a plain string, which keeps
    /// simple plugins simple, or as an object when the label, a note or a recommended mark helps.
    /// </summary>
    [JsonConverter(typeof(ChoiceManifestConverter))]
    public sealed class ChoiceManifest
    {
        [JsonPropertyName("value")]
        public string Value { get; set; } = "";

        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }

        [JsonPropertyName("recommended")]
        public bool Recommended { get; set; }

        public ParamChoice ToParamChoice() => new(
            Value, string.IsNullOrWhiteSpace(Label) ? Value : Label!, Note, Recommended);
    }

    /// <summary>Reads a choice written as a bare string or as an object; writes it as an object.</summary>
    public sealed class ChoiceManifestConverter : JsonConverter<ChoiceManifest>
    {
        public override ChoiceManifest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String) return new ChoiceManifest { Value = root.GetString() ?? "" };
            if (root.ValueKind != JsonValueKind.Object) return new ChoiceManifest();
            var choice = new ChoiceManifest();
            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                choice.Value = value.GetString() ?? "";
            if (root.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
                choice.Label = label.GetString();
            if (root.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String)
                choice.Note = note.GetString();
            if (root.TryGetProperty("recommended", out var recommended))
                choice.Recommended = recommended.ValueKind == JsonValueKind.True;
            return choice;
        }

        public override void Write(Utf8JsonWriter writer, ChoiceManifest value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("value", value.Value);
            if (value.Label != null) writer.WriteString("label", value.Label);
            if (value.Note != null) writer.WriteString("note", value.Note);
            if (value.Recommended) writer.WriteBoolean("recommended", true);
            writer.WriteEndObject();
        }
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
