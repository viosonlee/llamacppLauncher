namespace LlamaManager.Engines;

public enum ParamType { Integer, Decimal, Text, Boolean, Choice, FilePath, FolderPath, Multiline }

/// <summary>
/// One editable parameter, declared by a backend so the UI can generate controls.
/// Built-in backends may keep a hand-written panel and return an empty list.
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
    string[]? Choices = null,
    string? VisibleWhen = null);
