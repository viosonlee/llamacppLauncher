namespace LlamaManager.Engines;

public enum ParamType { Integer, Decimal, Text, Boolean, Choice, FilePath, FolderPath, Multiline }

/// <summary>
/// One selectable value of a <see cref="ParamType.Choice"/> parameter. <see cref="Value"/> is
/// what gets persisted and passed to the engine; <see cref="Label"/> is what the user reads, so
/// a choice can read like "q8_0 (8-bit 量化)" while still storing "q8_0".
/// </summary>
public sealed record ParamChoice(
    string Value,
    string Label,
    string? Note = null,
    bool Recommended = false)
{
    /// <summary>A bare choice whose label is the value itself.</summary>
    public static ParamChoice Of(string value) => new(value, value);
}

/// <summary>
/// One editable parameter, declared by a backend so the UI can generate controls.
/// Every engine (including the built-in ones and manifest plugins) describes its own
/// parameters here, so the panel never has to hard-code engine-specific knowledge.
/// </summary>
public sealed record ParameterSpec(
    string Key,
    string Label,
    string Group,
    ParamType Type,
    string? Default = null,
    string? ToolTip = null,
    double? Min = null,
    double? Max = null,
    ParamChoice[]? Choices = null,
    string? VisibleWhen = null,
    string? Help = null,
    string? Unit = null,
    bool Editable = false,
    bool Advanced = false,
    string? RangeHint = null,
    int? MultipleOf = null)
{
    /// <summary>The tooltip text, falling back to the longer help body when no short tip exists.</summary>
    public string? EffectiveToolTip => ToolTip ?? FirstLine(Help);

    /// <summary>The body shown by the "?" button: the help text, falling back to the tooltip.</summary>
    public string? EffectiveHelp => Help ?? ToolTip;

    /// <summary>The hint shown next to the control: an explicit range hint, else a Min–Max range.</summary>
    public string? EffectiveRangeHint => RangeHint ?? (Min != null && Max != null ? $"{Min}–{Max}" : null);

    /// <summary>True when the backend supplied a choice list, i.e. the control should be a combo box.</summary>
    public bool HasChoices => Choices is { Length: > 0 };

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var index = text.IndexOf('\n');
        return index < 0 ? text : text[..index];
    }
}
