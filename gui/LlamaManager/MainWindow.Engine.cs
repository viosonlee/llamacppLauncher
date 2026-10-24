using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager;

/// <summary>
/// The backend picker and the schema-driven parameter panel. The lifecycle buttons live in
/// the shared top bar (see MainWindow.Runtime.cs), so this file only selects the engine,
/// builds its parameter controls and shows/hides the engine-specific panels.
/// </summary>
public partial class MainWindow
{
    private bool _engineUpdate;
    private bool _doctorRunning;
    private readonly Dictionary<string, Control> _engineParameterControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ParameterSpec> _engineParameterSpecs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _engineParameterErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(ParameterSpec Spec, FrameworkElement Row)> _engineParameterRows = [];
    private readonly List<string> _registeredParameterNames = [];
    private string _engineParamFilter = "";

    private async Task ApplyEngineConfigAsync()
    {
        _engineUpdate = true;
        try
        {
            var all = BackendRegistry.All();
            EngineBackendBox.ItemsSource = all.Select(b => b.Descriptor.DisplayName).ToArray();
            var index = all.ToList().FindIndex(b => b.Descriptor.Id == _config.ActiveBackendId);
            EngineBackendBox.SelectedIndex = index >= 0 ? index : 0;
        }
        finally { _engineUpdate = false; }
        ApplyEnginePanelVisibility();
        RebuildEngineParameters();
        await RefreshEngineAsync();
    }

    private IEngineBackend? SelectedEngineBackend()
    {
        var index = EngineBackendBox.SelectedIndex;
        var all = BackendRegistry.All();
        return index >= 0 && index < all.Count ? all[index] : null;
    }

    private IServerBackend? SelectedServerBackend() => SelectedEngineBackend() as IServerBackend;

    /// <summary>The session slot a backend records under (llama.cpp keeps the "main" slot).</summary>
    private static string SlotFor(string backendId) => backendId == "llamacpp" ? ManagerConfig.SlotMain : backendId;

    private ServerSession? SelectedSession()
    {
        var backend = SelectedEngineBackend();
        return backend == null ? null : _config.GetRunning(SlotFor(backend.Descriptor.Id));
    }

    private bool IsLlamaCppSelected() => SelectedEngineBackend()?.Descriptor.Id == "llamacpp";

    private async void EngineBackendBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_engineUpdate) return;
        var backend = SelectedEngineBackend();
        if (backend != null)
        {
            _config.ActiveBackendId = backend.Descriptor.Id;
            ConfigService.SaveActiveBackend(_config, backend.Descriptor.Id);
        }
        ApplyEnginePanelVisibility();
        RebuildEngineParameters();
        // Reflect the newly selected engine's model in the model list.
        if (backend != null)
        {
            var selected = EngineModelFor(backend);
            _selecting = true;
            try { ModelListBox.SelectedItem = _models.FirstOrDefault(m => PathIdentity.Equals(m.FullName, selected)); }
            finally { _selecting = false; }
            UpdateSelectedModelText();
        }
        _statusVersion++;
        await RefreshEngineAsync();
        await RefreshStatusAsync();
    }

    private async void EngineRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        CudaService.Invalidate();
        await RefreshModelsAsync();
        await RefreshEngineAsync();
        await RefreshStatusAsync();
    }

    /// <summary>
    /// Every engine now renders through the schema panel, so only the truly engine-specific
    /// surfaces (raw args, embedding service) swap with the selection.
    /// </summary>
    private void ApplyEnginePanelVisibility()
    {
        var llamacpp = IsLlamaCppSelected();
        LlamaCppAdvancedPanel.Visibility = llamacpp ? Visibility.Visible : Visibility.Collapsed;
        EngineAdvancedPanel.Visibility = llamacpp ? Visibility.Collapsed : Visibility.Visible;
        EmbeddingPanel.Visibility = llamacpp ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Builds the schema-driven parameter panel: one row per <see cref="ParameterSpec"/>, grouped in
    /// declaration order, with <see cref="ParameterSpec.Advanced"/> fields folded into a "高级" area.
    /// Rows carry the default, an inline error line, and honour <see cref="ParameterSpec.VisibleWhen"/>
    /// so an engine never shows a knob it will ignore.
    /// </summary>
    private void RebuildEngineParameters()
    {
        _engineUpdate = true;
        try
        {
            _engineParameterControls.Clear();
            _engineParameterSpecs.Clear();
            _engineParameterErrors.Clear();
            _engineParameterRows.Clear();
            EngineParametersPanel.Children.Clear();
            // Detached controls keep their registered names; drop them so a stale lookup cannot
            // find a parameter belonging to the backend that was just deselected.
            foreach (var name in _registeredParameterNames)
            {
                try { UnregisterName(name); }
                catch (ArgumentException) { /* Already gone. */ }
            }
            _registeredParameterNames.Clear();
            var backend = SelectedEngineBackend();
            if (backend == null || backend.Parameters.Count == 0) return;
            var settings = SettingsFor(backend);
            var context = BuildParameterContext(backend, settings);

            var advanced = new StackPanel { Visibility = Visibility.Collapsed };
            var advancedToggle = new Button
            {
                Content = "高级参数 ▸",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 10, 0, 4),
                FontSize = 11,
            };
            advancedToggle.Click += (_, _) =>
            {
                var open = advanced.Visibility != Visibility.Visible;
                advanced.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                advancedToggle.Content = open ? "高级参数 ▾" : "高级参数 ▸";
            };

            foreach (var group in backend.Parameters.GroupBy(p => p.Group))
            {
                var basics = group.Where(p => !p.Advanced).ToList();
                var extras = group.Where(p => p.Advanced).ToList();
                if (basics.Count > 0)
                {
                    EngineParametersPanel.Children.Add(BuildGroupHeader(group.Key, basics, backend));
                    foreach (var spec in basics)
                    {
                        var element = BuildParameterRow(spec, backend, settings, context);
                        EngineParametersPanel.Children.Add(element);
                        RegisterParameter(spec, element);
                    }
                }
                if (extras.Count == 0) continue;
                advanced.Children.Add(BuildGroupHeader(group.Key, extras, backend));
                foreach (var spec in extras)
                {
                    var element = BuildParameterRow(spec, backend, settings, context);
                    advanced.Children.Add(element);
                    RegisterParameter(spec, element);
                }
            }

            if (advanced.Children.Count > 0)
            {
                EngineParametersPanel.Children.Add(advancedToggle);
                EngineParametersPanel.Children.Add(advanced);
            }
            ApplyParameterFilter();
            ValidateEngineParameters();
        }
        finally { _engineUpdate = false; }
    }

    /// <summary>
    /// The values the panel should display. llama.cpp keeps its settings in the raw argument
    /// string, so they are projected onto the schema; every other backend reads EngineSettings.
    /// </summary>
    private EngineSettings SettingsFor(IEngineBackend backend) =>
        backend is LlamaCppBackend
            // The raw box is the live source of truth while the window is up; _config.Args is only
            // written on save, so projecting from it would show stale values after a manual edit.
            ? new EngineSettings
            {
                ModelPath = _config.ModelPath,
                Parameters = LlamaCppParameters.ReadFromArgs(_loaded ? ArgsTextBox.Text : _config.Args),
            }
            : _config.Engines.GetValueOrDefault(backend.Descriptor.Id) ?? new EngineSettings();

    private void RegisterParameter(ParameterSpec spec, FrameworkElement element)
    {
        _engineParameterSpecs[spec.Key] = spec;
        _engineParameterRows.Add((spec, element));
    }

    /// <summary>A group title with the group's own "restore defaults" action.</summary>
    private FrameworkElement BuildGroupHeader(string title, IReadOnlyList<ParameterSpec> specs, IEngineBackend backend)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        var reset = new Button
        {
            Content = "恢复默认",
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 10,
            ToolTip = "把这一组的参数恢复为引擎声明的默认值",
        };
        DockPanel.SetDock(reset, Dock.Right);
        reset.Click += (_, _) =>
        {
            foreach (var spec in specs) RestoreDefault(spec);
            SaveEngineParameters(backend);
        };
        panel.Children.Add(reset);
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
        });
        return panel;
    }

    private string ResolveCurrentValue(ParameterSpec spec, IEngineBackend backend, EngineSettings? settings)
    {
        if (settings != null && settings.Parameters.TryGetValue(spec.Key, out var stored) && !string.IsNullOrWhiteSpace(stored))
            return stored;
        // Fields that predate the schema panel still live in the legacy StrataConfig block.
        // Showing that value keeps the panel honest about what the engine will receive.
        if (backend is StrataBackend strata)
        {
            var legacy = spec.Key switch
            {
                "threads" => strata.LegacyWorkers(_config),
                "reasoningBudget" => strata.LegacyReasoningBudget(_config),
                "anthropicThinking" => strata.LegacyAnthropicThinking(_config),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(legacy)) return legacy!;
        }
        return spec.Default ?? "";
    }

    /// <summary>Builds one parameter row: label + editor + "?" button, with an inline error line.</summary>
    private FrameworkElement BuildParameterRow(ParameterSpec spec, IEngineBackend backend,
        EngineSettings? settings, ParameterContext context)
    {
        var current = ResolveCurrentValue(spec, backend, settings);
        var host = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        var labelPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(new TextBlock
        {
            Text = spec.Unit == null ? spec.Label : $"{spec.Label}（{spec.Unit}）",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
            ToolTip = BuildParameterTip(spec),
        });
        if (!string.IsNullOrWhiteSpace(spec.Default))
        {
            labelPanel.Children.Add(new TextBlock
            {
                Text = $"默认 {spec.Default}",
                FontSize = 10,
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
            });
        }
        Grid.SetColumn(labelPanel, 0);
        row.Children.Add(labelPanel);

        Control control = spec.Type switch
        {
            ParamType.Boolean => BuildToggle(spec, current),
            ParamType.Choice => BuildChoice(spec, current),
            _ => new TextBox { Text = current, FontSize = 11 },
        };
        if (control is TextBox textBox)
        {
            textBox.ToolTip = spec.EffectiveToolTip;
            if (spec.Type is ParamType.Integer or ParamType.Decimal) FilterNumericInput(textBox, spec);
            textBox.LostFocus += (_, _) => ValidateEngineParameters();
            textBox.TextChanged += (_, _) => { if (!_engineUpdate) OnParameterEdited(spec, control); };
        }
        else if (control is ComboBox combo)
        {
            combo.ToolTip = spec.EffectiveToolTip;
            combo.SelectionChanged += (_, e) =>
            {
                if (_engineUpdate) return;
                // An editable ComboBox updates its Text after the selection event fires, so read
                // the picked item's machine value here instead of whatever Text still says.
                var picked = e.AddedItems.Count > 0 ? e.AddedItems[0] as ComboBoxItem : null;
                OnParameterEdited(spec, control, picked?.Tag is ParamChoice choice ? choice.Value : null);
            };
            // Typed free text commits on focus loss, matching the old hand-written combos.
            combo.LostFocus += (_, _) => { if (!_engineUpdate) OnParameterEdited(spec, control); };
        }
        else if (control is ToggleButton toggle)
        {
            toggle.ToolTip = spec.EffectiveToolTip;
            toggle.Checked += (_, _) => { if (!_engineUpdate) OnParameterEdited(spec, control); };
            toggle.Unchecked += (_, _) => { if (!_engineUpdate) OnParameterEdited(spec, control); };
        }

        if (spec.Type is ParamType.FilePath or ParamType.FolderPath && control is TextBox pathBox)
        {
            var pathRow = new DockPanel { MinWidth = 220 };
            var browse = new Button { Content = "浏览…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
            var folder = spec.Type == ParamType.FolderPath;
            browse.Click += (_, _) =>
            {
                if (folder)
                {
                    var dialog = new Microsoft.Win32.OpenFolderDialog { Title = spec.Label };
                    if (dialog.ShowDialog() == true) pathBox.Text = dialog.FolderName;
                }
                else
                {
                    var isGguf = spec.Key.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                                 spec.Key.Contains("mmproj", StringComparison.OrdinalIgnoreCase) ||
                                 spec.Key.Contains("draft", StringComparison.OrdinalIgnoreCase);
                    var filter = isGguf
                        ? "GGUF 模型文件 (*.gguf)|*.gguf|所有文件 (*.*)|*.*"
                        : "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                    var dialog = new Microsoft.Win32.OpenFileDialog { Title = spec.Label, Filter = filter };
                    if (dialog.ShowDialog() == true) pathBox.Text = dialog.FileName;
                }
            };
            DockPanel.SetDock(browse, Dock.Right);
            pathRow.Children.Add(browse);
            pathRow.Children.Add(pathBox);
            Grid.SetColumn(pathRow, 1);
            row.Children.Add(pathRow);
        }
        else
        {
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
        }

        var infoBtn = new Wpf.Ui.Controls.Button
        {
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.QuestionCircle24 },
            FontSize = 11,
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = spec.EffectiveToolTip ?? "点击查看详细说明与配置建议",
        };
        var capturedSpec = spec;
        var backendDisplayName = backend.Descriptor.DisplayName;
        infoBtn.Click += (_, _) => ShowParameterHelp($"{backendDisplayName} - {capturedSpec.Label}",
            capturedSpec.EffectiveHelp ?? GetEngineParamDetailedHelp(capturedSpec));
        Grid.SetColumn(infoBtn, 2);
        row.Children.Add(infoBtn);

        var error = new TextBlock
        {
            Text = "",
            FontSize = 10,
            Margin = new Thickness(150, 2, 30, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = (Brush)FindResource("SystemFillColorCriticalBrush"),
        };
        host.Children.Add(row);
        host.Children.Add(error);
        _engineParameterControls[spec.Key] = control;
        _engineParameterErrors[spec.Key] = error;
        // Dynamic controls are not in the XAML namescope; registering them keeps the panel
        // inspectable from the UI regression test without hard-coding control names again.
        var testName = "Param_" + spec.Key;
        try
        {
            if (FindName(testName) != null) UnregisterName(testName);
            RegisterName(testName, control);
            _registeredParameterNames.Add(testName);
        }
        catch (ArgumentException) { /* A duplicate name must not break the panel. */ }
        return host;
    }

    /// <summary>Restores one parameter to the backend-declared default.</summary>
    private void RestoreDefault(ParameterSpec spec)
    {
        if (!_engineParameterControls.TryGetValue(spec.Key, out var control)) return;
        var value = spec.Default ?? "";
        _engineUpdate = true;
        try
        {
            switch (control)
            {
                case TextBox box: box.Text = value; break;
                case ComboBox combo: SelectChoice(combo, value); break;
                case ToggleButton toggle: toggle.IsChecked = value is "1" or "true" or "True"; break;
            }
        }
        finally { _engineUpdate = false; }
        if (SelectedEngineBackend() is LlamaCppBackend) ApplyLlamaCppParameter(spec, control);
    }

    /// <summary>Re-evaluates visibility after an edit: a condition may depend on another field.</summary>
    private void OnParameterEdited(ParameterSpec spec, Control control, string? explicitValue = null)
    {
        if (SelectedEngineBackend() is LlamaCppBackend) ApplyLlamaCppParameter(spec, control, explicitValue);
        ValidateEngineParameters();
        ApplyParameterVisibility();
        ApplyParameterFilter();
    }

    /// <summary>
    /// Writes one edited value back into llama.cpp's raw argument string. The raw box stays the
    /// single source of truth, so hand-written flags survive and the advanced tab reflects edits.
    /// </summary>
    private void ApplyLlamaCppParameter(ParameterSpec spec, Control control, string? explicitValue = null)
    {
        var value = explicitValue ?? ReadControlValue(control, spec);
        var updated = LlamaCppParameters.Apply(ArgsTextBox.Text, spec.Key, value);
        if (updated == ArgsTextBox.Text) return;
        _parsingArgs = true;
        try { ArgsTextBox.Text = updated; }
        finally { _parsingArgs = false; }
        // No rebuild here: the edited control already shows the value, and rebuilding the panel
        // from inside its own selection event would tear down the control mid-event.
    }

    /// <summary>Hides parameters whose <see cref="ParameterSpec.VisibleWhen"/> is false, keeping their values.</summary>
    private void ApplyParameterVisibility()
    {
        var backend = SelectedEngineBackend();
        if (backend == null) return;
        var context = BuildParameterContext(backend, _config.Engines.GetValueOrDefault(backend.Descriptor.Id));
        foreach (var (spec, row) in _engineParameterRows)
            row.Tag = ParameterVisibility.Evaluate(spec.VisibleWhen, context);
    }

    /// <summary>Shows a row only when it is both visible per its condition and matched by the filter.</summary>
    private void ApplyParameterFilter()
    {
        var filter = _engineParamFilter.Trim();
        ApplyParameterVisibility();
        foreach (var (spec, row) in _engineParameterRows)
        {
            var visible = row.Tag is not false;
            if (visible && filter.Length > 0)
                visible = spec.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || spec.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || spec.Group.Contains(filter, StringComparison.OrdinalIgnoreCase);
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void EngineParamSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_engineUpdate) return;
        _engineParamFilter = EngineParamSearchBox.Text ?? "";
        ApplyParameterFilter();
    }

    private void EngineResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = SelectedEngineBackend();
        if (backend == null) return;
        foreach (var spec in backend.Parameters) RestoreDefault(spec);
        SaveEngineParameters(backend);
        ApplyParameterFilter();
    }

    private void EngineBenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = SelectedEngineBackend();
        if (backend == null || _serverStarting || _serverStopping) return;
        // The sweep starts a bare llama-server per variant; schema-only plugins have no such binary.
        if (backend is not LlamaCppBackend)
        {
            MessageBox.Show("参数实测对比目前只支持 llama.cpp 引擎（每组参数启动一个独立的 llama-server）。",
                "引擎不支持", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var model = EngineModelFor(backend);
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
        {
            MessageBox.Show("请先在「模型」页选择一个有效的 GGUF 模型。", "未选择模型", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // A starting plan only for MoE models: that is the parameter with real leverage
        // (the 6 GiB boundary and the fast setting differ), dense models rarely need a sweep.
        string? suggest = null;
        if (GgufService.TryRead(model) is { IsMoE: true } meta)
        {
            var common = $"-ngl 999 -c 4096 -t {Math.Clamp(Environment.ProcessorCount / 2, 1, 8)} -fa on --jinja";
            suggest = $"# 每行一组：标签|参数。实测器统一加 -m/--port/--host。\n" +
                      $"moe 26|{common} --n-cpu-moe 26\nmoe 30|{common} --n-cpu-moe 30\nmoe 34|{common} --n-cpu-moe 34";
        }
        new ParameterBenchmarkWindow(model, suggest) { Owner = this }.ShowDialog();
    }

    /// <summary>The tooltip shown on a parameter label: help, range and default.</summary>
    private static string? BuildParameterTip(ParameterSpec spec)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(spec.EffectiveToolTip)) parts.Add(spec.EffectiveToolTip!);
        if (spec.EffectiveRangeHint != null) parts.Add($"范围：{spec.EffectiveRangeHint}");
        if (!string.IsNullOrWhiteSpace(spec.Default)) parts.Add($"默认：{spec.Default}");
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    /// <summary>The value every visibility condition and validator sees for the current panel state.</summary>
    private ParameterContext BuildParameterContext(IEngineBackend backend, EngineSettings? settings)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in backend.Parameters)
        {
            values[spec.Key] = _engineParameterControls.TryGetValue(spec.Key, out var control)
                ? ReadControlValue(control, spec)
                : ResolveCurrentValue(spec, backend, settings);
        }
        GgufMetadata? metadata = null;
        var model = EngineModelFor(backend);
        if (!string.IsNullOrWhiteSpace(model) && File.Exists(model))
        {
            try { metadata = GgufService.TryReadShards(model); }
            catch (IOException) { metadata = null; }
        }
        return new ParameterContext(backend.Descriptor.Id, metadata, values);
    }

    /// <summary>Rejects non-numeric keystrokes in Integer/Decimal fields without blocking paste.</summary>
    private static void FilterNumericInput(TextBox box, ParameterSpec spec)
    {
        box.PreviewTextInput += (_, e) =>
        {
            foreach (var ch in e.Text)
            {
                var ok = char.IsDigit(ch) || ch == '-' || (spec.Type == ParamType.Decimal && ch is '.' or ',');
                if (!ok) { e.Handled = true; return; }
            }
        };
    }

    /// <summary>
    /// Validates every visible field and paints inline errors. Returns false when something is out of
    /// range so the caller can refuse to launch; it never rewrites the user's value.
    /// </summary>
    private bool ValidateEngineParameters()
    {
        var backend = SelectedEngineBackend();
        if (backend == null) return true;
        var ok = true;
        foreach (var spec in backend.Parameters)
        {
            if (!_engineParameterControls.TryGetValue(spec.Key, out var control)) continue;
            var error = _engineParameterErrors.GetValueOrDefault(spec.Key);
            var problem = ValidateValue(spec, ReadControlValue(control, spec));
            if (problem != null) ok = false;
            if (error != null)
            {
                error.Text = problem ?? "";
                error.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
            }
            if (problem != null) control.BorderBrush = (Brush)FindResource("SystemFillColorCriticalBrush");
            else control.ClearValue(Control.BorderBrushProperty);
        }
        return ok;
    }

    /// <summary>Returns a human-readable problem for one value, or null when it is acceptable.</summary>
    private static string? ValidateValue(ParameterSpec spec, string value)
    {
        // A Choice that carries a numeric range holds a number even though it also offers presets
        // (llama.cpp's -ngl/-b/-ub/-t), so it gets the same range check as a plain Integer.
        var numeric = spec.Type is ParamType.Integer or ParamType.Decimal
            || (spec.Type == ParamType.Choice && spec.Min != null);
        if (!numeric) return null;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        // -ngl also accepts "all" / "auto", which are not numbers at all.
        if (text is "all" or "auto") return null;
        var allowFraction = spec.Type == ParamType.Decimal;
        if (allowFraction)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                return "需要一个数字";
            if (spec.Min != null && real < spec.Min) return $"不能小于 {spec.Min}";
            if (spec.Max != null && real > spec.Max) return $"不能大于 {spec.Max}";
            return null;
        }
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return "需要一个整数";
        if (spec.Min != null && number < spec.Min) return $"不能小于 {spec.Min:0}";
        if (spec.Max != null && number > spec.Max) return $"不能大于 {spec.Max:0}";
        return null;
    }

    private static string GetEngineParamDetailedHelp(ParameterSpec spec)
    {
        return spec.Key switch
        {
            "packageRoot" =>
                "【引擎包目录 (packageRoot)】\n" +
                "• 作用：Strata 引擎二进制核心和配套服务脚本所在的根目录。\n" +
                "• 默认建议：留空即可（系统会自动匹配 engines/polystrata）。\n" +
                "• 包含文件：内含 engine-poly\\strata-poly.exe 与 serve\\server.py。",

            "serveRoot" =>
                "【服务源码根 (serveRoot)】\n" +
                "• 作用：提供 HTTP API（兼容 OpenAI / Anthropic 格式）的 Python 服务源码根目录。\n" +
                "• 默认建议：留空即可。留空时系统默认使用“引擎包目录”。",

            "pythonPath" =>
                "【Python 解释器路径 (pythonPath)】\n" +
                "• 作用：启动 API 服务端所需的 Python 可执行文件路径（要求 Python 3.10+）。\n" +
                "• 默认建议：留空即可。系统会自动使用通过“一键配置环境”准备就绪的专属虚拟环境（venv）。",

            "servePort" =>
                "【服务端口 (servePort)】\n" +
                "• 作用：Strata HTTP API 监听的端口。外部客户端（如 Claude Code、Codex、沉浸式翻译等）通过此端口连接模型。\n" +
                "• 默认建议：8091（已自动避开 llama.cpp 默认的 8080 端口）。\n" +
                "• 调优说明：若与其他软件冲突，可修改为 1024–65535 范围内的任意空闲端口。",

            "host" =>
                "【监听地址 (host)】\n" +
                "• 作用：指定 HTTP API 监听的网络 IP 地址。\n" +
                "• 选项建议：\n" +
                "  - 127.0.0.1（默认）：仅本机访问，安全性最高。\n" +
                "  - 0.0.0.0：允许局域网内其他电脑、手机通过本机内网 IP 调用此模型服务。",

            "context" =>
                "【上下文窗口 (context)】\n" +
                "• 作用：Strata 引擎单次能够记忆和处理的最大 Token 数量（--max-context）。\n" +
                "• 显存影响：上下文设得越大，KV 缓存占用的显存越多。\n" +
                "• 推荐配置：\n" +
                "  - 6GB 显存显卡：建议保持 2048 ~ 4096，防止爆显存 (OOM)\n" +
                "  - 8GB ~ 12GB 显存：推荐 4096 ~ 8192\n" +
                "  - 16GB+ 显存：可增大至 8192 ~ 16384",

            "vramReserveMib" =>
                "【显存预留 MiB (vramReserveMib)】\n" +
                "• 作用：指示 Strata 引擎在分配 GPU 显存时，强行预留指定 MiB 的安全显存（--vram-reserve-mib）。\n" +
                "• 为什么需要：防止模型把显存 100% 占满导致 Windows 桌面窗口黑屏卡死或显卡驱动崩溃重置。\n" +
                "• 推荐配置：\n" +
                "  - PolyStrata 默认 256 MiB，引擎会自动将扣除预留后的全部空闲显存尽可能多地用于缓存 MoE 专家。\n" +
                "  - 笔记本独显或单显卡用户：推荐 256 ~ 500 MiB 最佳。\n" +
                "  - 双显卡（核显负责屏幕显示，独显仅推理）：可设为 256 MiB 以腾出更多显存给模型。",

            "threads" =>
                "【CPU 线程数 (threads)】\n" +
                "• 作用：显存外的专家在 CPU 上计算时使用的线程数（--threads）。\n" +
                "• 推荐配置：默认填 0 即可（0 表示由引擎根据 CPU 可用核心数自动分配）。\n" +
                "• 调优：线程过多会和显卡搬运争抢内存带宽；可试 8 / 12 与自动值对比。",

            "reasoningBudget" =>
                "【思考预算 (reasoningBudget)】\n" +
                "• 作用：针对 DeepSeek-R1 / Qwen-QwQ 等具备思维链的大模型，限制其单次思考输出的最大 Token 数。\n" +
                "• 推荐配置：\n" +
                "  - 0（默认）：不设上限，由模型自主推理完毕后作答。\n" +
                "  - 正整数（如 1024、2048）：限制思考深度，适合追求更快响应速度的日常问答场景。",

            "anthropicThinking" =>
                "【Anthropic 协议思考行为 (anthropicThinking)】\n" +
                "• 作用：当上游应用（如 Claude Code 等支持 Anthropic 接口的应用）连接时，若请求头未显式指定 thinking 字段时的默认行为。\n" +
                "• 选项说明：\n" +
                "  - model（默认）：遵循模型自身设定。思考模型（如 R1/QwQ）默认自动展开思考推理。\n" +
                "  - on_request：仅当客户端请求中显式包含 thinking 参数时才思考，否则按普通模型直接输出回答。",

            _ => string.IsNullOrWhiteSpace(spec.ToolTip)
                ? $"【{spec.Label}】\n默认值：{spec.Default ?? "无"}\n参数类型：{spec.Type}"
                : $"【{spec.Label}】\n{spec.ToolTip}\n\n默认值：{spec.Default ?? "无"}\n参数类型：{spec.Type}"
        };
    }

    /// <summary>
    /// Builds the editor for a Choice parameter. The label (and any usage note) is what the user
    /// reads; the box's SelectedItem is the <see cref="ParamChoice"/> itself, so the engine always
    /// receives the machine value in <see cref="ParamChoice.Value"/> even when the label differs.
    /// </summary>
    private static ComboBox BuildChoice(ParameterSpec spec, string current)
    {
        var combo = new ComboBox { FontSize = 11 };
        foreach (var choice in spec.Choices ?? [])
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = choice.Recommended ? $"{choice.Label}（推荐）" : choice.Label,
                ToolTip = choice.Note,
                Tag = choice,
            });
        }
        var selected = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is ParamChoice choice && choice.Value == current)
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        combo.SelectedItem = selected;
        combo.IsEditable = spec.Editable;
        return combo;
    }

    /// <summary>The machine value of a Choice box, or <paramref name="fallback"/> when nothing is selected.</summary>
    private static string ChoiceValue(ComboBox combo, string fallback)
    {
        if (combo.IsEditable)
        {
            // An editable Choice also accepts free text (e.g. -ngl 35), so map a known entry back
            // to its machine value and otherwise keep whatever the user typed. The displayed
            // Content may carry a "（推荐）" suffix, so it is matched as well as the bare label.
            var text = (combo.Text ?? "").Trim();
            var match = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase)
                || (item.Tag is ParamChoice choice
                    && (string.Equals(choice.Label, text, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(choice.Value, text, StringComparison.OrdinalIgnoreCase))));
            if (match?.Tag is ParamChoice matched) return matched.Value;
            return string.IsNullOrEmpty(text) ? fallback : text;
        }
        return combo.SelectedItem is ComboBoxItem { Tag: ParamChoice choice } ? choice.Value : fallback;
    }

    /// <summary>Selects the item whose <see cref="ParamChoice.Value"/> matches; falls back to the first.</summary>
    private static void SelectChoice(ComboBox combo, string value)
    {
        var items = combo.Items.OfType<ComboBoxItem>().ToList();
        combo.SelectedItem = items.FirstOrDefault(item => item.Tag is ParamChoice choice && choice.Value == value)
            ?? items.FirstOrDefault();
    }

    /// <summary>Builds the on/off control a Boolean parameter uses; it writes "1" / "0".</summary>
    private static Wpf.Ui.Controls.ToggleSwitch BuildToggle(ParameterSpec spec, string current) =>
        new() { IsChecked = current is "1" or "true" or "True", FontSize = 11 };

    private async Task RefreshEngineAsync()
    {
        var backend = SelectedEngineBackend();
        if (backend == null) return;
        EngineStatusText.Text = "正在探测…";
        try
        {
            var status = await backend.ProbeAsync(_lifetime.Token);
            EngineStatusText.Text = status.Available
                ? $"{backend.Descriptor.DisplayName} 可用：{status.ExecutablePath}{(status.Version != null ? $"（{status.Version}）" : "")}"
                : $"不可用：{status.Problem}";
            var model = EngineModelFor(backend);
            if (!string.IsNullOrWhiteSpace(model) && File.Exists(model))
            {
                var report = await backend.InspectAsync(model, _lifetime.Token);
                EngineCompatText.Text = report.Summary + (report.Estimate != null ? "\n" + report.Estimate.Note : "");
                EngineCompatText.ToolTip = report.Blockers.Count > 0 ? string.Join("\n", report.Blockers) : null;
            }
            else EngineCompatText.Text = "在「模型」页选择模型后，这里显示该引擎的兼容性。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EngineStatusText.Text = "探测失败：" + ex.Message; }
    }

    private string EngineModelFor(IEngineBackend backend) => backend.Descriptor.Id == "llamacpp"
        ? _config.ModelPath
        : _config.Engines.GetValueOrDefault(backend.Descriptor.Id)?.ModelPath ?? "";

    /// <summary>Reads the engine-facing value out of whichever control a spec produced.</summary>
    private static string ReadControlValue(Control control, ParameterSpec spec) => control switch
    {
        TextBox textBox => textBox.Text,
        ComboBox combo => ChoiceValue(combo, spec.Default ?? ""),
        ToggleButton toggle => toggle.IsChecked == true ? "1" : "0",
        _ => spec.Default ?? "",
    };

    /// <summary>
    /// Persists the schema panel's values before a start. Returns false — without saving anything —
    /// when a field is out of range, so an invalid value can never reach the engine's command line.
    /// </summary>
    private bool SaveEngineParameters(IEngineBackend backend)
    {
        if (_engineUpdate || backend.Parameters.Count == 0) return true;
        if (!ValidateEngineParameters())
        {
            LogService.Write($"[{backend.Descriptor.Id}] 参数校验未通过，已阻止启动。");
            return false;
        }
        if (backend is LlamaCppBackend)
        {
            // llama.cpp's values already live in ArgsTextBox; SaveCurrentConfig validates the
            // port and writes Args/Port back to the main config.
            SaveCurrentConfig();
            return true;
        }
        var settings = _config.Engines.GetValueOrDefault(backend.Descriptor.Id) ?? new EngineSettings();
        foreach (var spec in backend.Parameters)
        {
            if (!_engineParameterControls.TryGetValue(spec.Key, out var control)) continue;
            var value = ReadControlValue(control, spec);
            // Only record what the user actually chose. Persisting an untouched default would
            // silently pin behaviour the engine or the legacy config block used to decide,
            // e.g. Strata's expert slot count that depends on the context size.
            var untouched = string.Equals(value, spec.Default ?? "", StringComparison.OrdinalIgnoreCase)
                && !settings.Parameters.ContainsKey(spec.Key);
            if (untouched) continue;
            settings.Parameters[spec.Key] = value;
        }
        if (string.IsNullOrEmpty(settings.ModelPath)) settings.ModelPath = EngineModelFor(backend);
        _config.Engines[backend.Descriptor.Id] = settings;
        ConfigService.SaveEngineSettings(_config, backend.Descriptor.Id, settings);
        return true;
    }

    /// <summary>Tells the user why a start was refused and puts the cursor in the offending field.</summary>
    private void ShowParameterValidationFailure()
    {
        var first = _engineParameterRows
            .Select(r => r.Spec)
            .FirstOrDefault(spec => _engineParameterErrors.TryGetValue(spec.Key, out var error)
                && error.Visibility == Visibility.Visible);
        if (first != null && _engineParameterControls.TryGetValue(first.Key, out var control))
        {
            if (control is TextBox box) { box.Focus(); box.SelectAll(); }
            else control.Focus();
        }
        ShowError("参数有误", new InvalidOperationException(
            first == null ? "有参数超出允许范围，已取消启动。" : $"“{first.Label}”超出允许范围，已取消启动。"));
    }

    private async void EngineBootstrapButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = SelectedEngineBackend();
        if (backend == null || _serverStarting || _serverStopping) return;
        EngineOutputBox.Clear();
        LogService.Write($"=== 开始配置环境：{backend.Descriptor.DisplayName} ===");
        try
        {
            await BootstrapService.RunAsync(backend.Descriptor.Id,
                new DelegateLogSink(msg => { LogService.Write(msg); AppendEngineOutput(msg); }), _lifetime.Token);
            AppendEngineOutput("初始化完成。");
            LogService.Write("初始化完成。");
        }
        catch (OperationCanceledException) { AppendEngineOutput("已取消。"); LogService.Write("已取消。"); }
        catch (Exception ex) { ShowError("初始化失败", ex); }
        finally { await RefreshEngineAsync(); await RefreshStatusAsync(); }
    }

    private async void EngineTopBootstrapButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = SelectedEngineBackend();
        if (backend == null || _serverStarting || _serverStopping) return;
        EngineTopBootstrapButton.IsEnabled = false;
        ConfigurationTabs.SelectedIndex = ConfigurationTabs.Items.Count - 1; // 切换到日志页
        LogService.Write($"=== 一键配置运行环境：{backend.Descriptor.DisplayName} ===");
        try
        {
            var steps = await BootstrapService.RunAsync(backend.Descriptor.Id,
                new DelegateLogSink(msg => { LogService.Write(msg); AppendEngineOutput(msg); }),
                _lifetime.Token);
            var allOk = steps.All(s => s.Ok);
            LogService.Write(allOk ? $"=== {backend.Descriptor.DisplayName} 环境配置完成，可以直接启动服务 ===" : "=== 部分步骤未成功，请检查日志 ===");
            if (allOk)
                MessageBox.Show($"{backend.Descriptor.DisplayName} 运行环境已配置完成！\n\n现在可以在「模型」页选择模型后点击「启动服务」。", "配置环境完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { LogService.Write("已取消环境配置。"); }
        catch (Exception ex) { ShowError("环境配置失败", ex); }
        finally
        {
            EngineTopBootstrapButton.IsEnabled = true;
            await RefreshEngineAsync();
            await RefreshStatusAsync();
        }
    }

    private async void EngineDoctorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_doctorRunning) return;
        _doctorRunning = true;
        EngineDoctorButton.IsEnabled = false;
        LogService.Write("—— 一键体检开始 ——");
        try
        {
            var report = await Task.Run(() => DoctorService.RunAsync(checkUpdates: true, _lifetime.Token), _lifetime.Token);
            foreach (var finding in report.Findings)
            {
                LogService.Write($"[体检] {finding.Engine}: {finding.Status} — {finding.Detail}");
                if (finding.Action != null) LogService.Write($"[体检]     → {finding.Action}");
            }
            LogService.Write($"—— 一键体检结束：{report.Summary} ——");
        }
        catch (OperationCanceledException) { LogService.Write("—— 一键体检已取消 ——"); }
        catch (Exception ex) { ShowError("体检失败", ex); }
        finally
        {
            _doctorRunning = false;
            EngineDoctorButton.IsEnabled = true;
            // Results are global (they cover every engine), so surface the log page.
            ConfigurationTabs.SelectedIndex = ConfigurationTabs.Items.Count - 1;
            RefreshLog();
        }
    }

    private void EngineCopyUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EngineBaseUrlTextBox.Text)) return;
        try { Clipboard.SetText(EngineBaseUrlTextBox.Text); EngineRunStatusText.Text = "已复制接入地址。"; }
        catch (Exception ex) { ShowError("复制失败", ex); }
    }

    private void AppendEngineOutput(string text) =>
        Dispatcher.BeginInvoke(() => { EngineOutputBox.AppendText(text + "\n"); EngineOutputBox.ScrollToEnd(); });
}
