using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LlamaManager.Engines;
using LlamaManager.Services;

namespace LlamaManager;

public partial class MainWindow
{
    private readonly Dictionary<string, TextBlock> _parameterAdjustmentNotes = new(StringComparer.OrdinalIgnoreCase);

    // Never snap while typing: normalize only on commit (focus loss, Enter, or whole-panel save).
    private bool NormalizeParameterControl(ParameterSpec spec, Control control)
    {
        if (spec.MultipleOf == null || control is not TextBox box) return true;
        var result = ParameterNormalizer.Normalize(spec, box.Text);
        if (!result.IsValid) return false;
        if (!result.Changed) return true;
        var updating = _engineUpdate;
        _engineUpdate = true;
        try { box.Text = result.NormalizedValue ?? ""; }
        finally { _engineUpdate = updating; }
        if (_parameterAdjustmentNotes.TryGetValue(spec.Key, out var note))
        {
            note.Text = result.Message ?? "已调整为最近的合法值。";
            note.Visibility = Visibility.Visible;
        }
        LogService.Write($"[{SelectedEngineBackend()?.Descriptor.Id}] {spec.Label}：{result.Message}");
        return true;
    }

    private void CommitAlignedParameter(ParameterSpec spec, Control control)
    {
        if (_engineUpdate || !_engineParameterControls.TryGetValue(spec.Key, out var currentControl) || !ReferenceEquals(currentControl, control)) return;
        var backend = SelectedEngineBackend();
        if (backend == null) return;
        var context = BuildParameterContext(backend, _config.Engines.GetValueOrDefault(backend.Descriptor.Id));
        if (!ParameterVisibility.Evaluate(spec.VisibleWhen, context)) return;
        if (NormalizeParameterControl(spec, control)) OnParameterEdited(spec, control);
        else ValidateEngineParameters();
    }

    private bool NormalizePanelParameters(IEngineBackend backend)
    {
        var context = BuildParameterContext(backend, _config.Engines.GetValueOrDefault(backend.Descriptor.Id));
        var valid = true;
        foreach (var spec in backend.Parameters.Where(s => s.MultipleOf != null))
            if (ParameterVisibility.Evaluate(spec.VisibleWhen, context) && _engineParameterControls.TryGetValue(spec.Key, out var control))
                valid &= NormalizeParameterControl(spec, control);
        return valid;
    }
}
