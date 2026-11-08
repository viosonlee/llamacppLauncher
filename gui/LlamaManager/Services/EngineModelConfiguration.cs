using System.Globalization;
using System.IO;
using LlamaManager.Engines;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>
/// Model-local parameter domains for any schema-backed engine. Selection returns a deep copy;
/// neither operation saves configuration, scans models, reads tensors, or changes runtime identity.
/// </summary>
public static class EngineModelConfiguration
{
    private static readonly HashSet<string> InstallationKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "packageRoot", "serveRoot", "pythonPath", "bundlePython", "enginePath", "executablePath",
        "releaseVersion", "runtimeCuda"
    };

    /// <summary>
    /// Saves the previous model's current parameters and fully restores an existing target preset.
    /// A first selection inherits installation keys only, plus declared endpoint defaults so a
    /// legacy Strata endpoint cannot silently become the new model's endpoint. Pairing is a caller concern.
    /// </summary>
    public static EngineSettings SelectModel(EngineSettings settings, string model, IReadOnlyList<ParameterSpec> schema)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(schema);
        var copy = new EngineSettings
        {
            ModelPath = settings.ModelPath,
            ExecutablePath = settings.ExecutablePath,
            Port = settings.Port,
            LastStats = settings.LastStats,
            Parameters = CopyParameters(settings.Parameters),
            ModelPresets = CopyPresets(settings.ModelPresets)
        };
        var previousKey = ModelKey(copy.ModelPath);
        var selectedKey = ModelKey(model);
        if (previousKey != null) copy.ModelPresets[previousKey] = CopyParameters(copy.Parameters);
        copy.ModelPath = selectedKey == null ? string.Empty : Path.GetFullPath(model);
        // Re-selecting the same path must keep unsaved current edits, not load a stale preset.
        // With no model on either side, backend defaults also remain untouched.
        if (previousKey == selectedKey) return copy;

        copy.Parameters = selectedKey != null && copy.ModelPresets.TryGetValue(selectedKey, out var preset)
            ? CopyParameters(preset)
            : NewModelParameters(copy.Parameters, schema);
        // The top-level port is an endpoint mirror, not an installation key to inherit from another model.
        copy.Port = EndpointPort(copy.Parameters);
        if (selectedKey != null) copy.ModelPresets[selectedKey] = CopyParameters(copy.Parameters);
        return copy;
    }

    /// <summary>
    /// Mutates only the supplied copy and only for its current model. A mismatched selection returns
    /// false without changing anything. Empty model is allowed only with an empty current selection:
    /// it changes backend defaults without creating a model preset. The caller owns the save transaction.
    /// </summary>
    public static bool SetParameter(EngineSettings copy, string model, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        var modelKey = ModelKey(model);
        if (ModelKey(copy.ModelPath) != modelKey) return false;

        var parameters = CopyParameters(copy.Parameters);
        parameters[key] = value;
        if (modelKey != null)
        {
            // Detach dictionaries even if an older caller accidentally aliased current/other presets.
            var presets = CopyPresets(copy.ModelPresets);
            if (!presets.TryGetValue(modelKey, out var preset))
                presets[modelKey] = preset = CopyParameters(parameters);
            preset[key] = value;
            copy.ModelPresets = presets;
        }
        copy.Parameters = parameters;
        return true;
    }

    private static Dictionary<string, string> NewModelParameters(IReadOnlyDictionary<string, string> current,
        IReadOnlyList<ParameterSpec> schema)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in current)
            if (InstallationKeys.Contains(key)) parameters[key] = value;
        foreach (var spec in schema)
            if (IsEndpointKey(spec.Key) && spec.Default != null) parameters[spec.Key] = spec.Default;
        return parameters;
    }

    private static bool IsEndpointKey(string key) => key.Equals("host", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("servePort", StringComparison.OrdinalIgnoreCase) || key.Equals("port", StringComparison.OrdinalIgnoreCase);

    private static int EndpointPort(IReadOnlyDictionary<string, string> parameters)
    {
        foreach (var key in new[] { "servePort", "port" })
            if (parameters.TryGetValue(key, out var value) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
                return port;
        return 0;
    }

    private static string? ModelKey(string? model) => string.IsNullOrWhiteSpace(model) ? null : PathIdentity.Normalize(model);

    private static Dictionary<string, string> CopyParameters(IReadOnlyDictionary<string, string>? source)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source != null)
            foreach (var (key, value) in source) copy[key] = value;
        return copy;
    }

    private static Dictionary<string, Dictionary<string, string>> CopyPresets(
        IReadOnlyDictionary<string, Dictionary<string, string>>? source)
    {
        var copy = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (source != null)
            foreach (var (model, parameters) in source) copy[ModelKey(model) ?? model] = CopyParameters(parameters);
        return copy;
    }
}
