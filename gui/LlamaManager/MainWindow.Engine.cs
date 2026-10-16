using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private readonly Dictionary<string, TextBox> _engineParameterBoxes = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>llama.cpp has hand-written panels; other engines get the schema-driven ones.</summary>
    private void ApplyEnginePanelVisibility()
    {
        var llamacpp = IsLlamaCppSelected();
        LlamaCppParamsScroll.Visibility = llamacpp ? Visibility.Visible : Visibility.Collapsed;
        EngineParamsScroll.Visibility = llamacpp ? Visibility.Collapsed : Visibility.Visible;
        LlamaCppAdvancedPanel.Visibility = llamacpp ? Visibility.Visible : Visibility.Collapsed;
        EngineAdvancedPanel.Visibility = llamacpp ? Visibility.Collapsed : Visibility.Visible;
        EmbeddingPanel.Visibility = llamacpp ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildEngineParameters()
    {
        _engineUpdate = true;
        try
        {
            _engineParameterBoxes.Clear();
            EngineParametersPanel.Children.Clear();
            var backend = SelectedEngineBackend();
            if (backend == null || backend.Parameters.Count == 0) return;
            var settings = _config.Engines.GetValueOrDefault(backend.Descriptor.Id);
            foreach (var group in backend.Parameters.GroupBy(p => p.Group))
            {
                EngineParametersPanel.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 4),
                    Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
                });
                foreach (var spec in group)
                {
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var label = new TextBlock
                    {
                        Text = spec.Label,
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
                    };
                    Grid.SetColumn(label, 0);
                    row.Children.Add(label);
                    var current = settings?.Parameters.GetValueOrDefault(spec.Key) ?? spec.Default ?? "";
                    Control control = spec.Type switch
                    {
                        ParamType.Boolean => new CheckBox { IsChecked = current is "1" or "true" or "True" },
                        ParamType.Choice => BuildChoice(spec, current),
                        _ => new TextBox { Text = current, FontSize = 11 },
                    };
                    if (spec.ToolTip != null) control.ToolTip = spec.ToolTip;
                    Grid.SetColumn(control, 1);
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
                                var dialog = new Microsoft.Win32.OpenFileDialog { Title = spec.Label, Filter = "可执行文件|*.exe|所有文件|*.*" };
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
                    EngineParametersPanel.Children.Add(row);
                    if (control is TextBox textBox) _engineParameterBoxes[spec.Key] = textBox;
                }
            }
        }
        finally { _engineUpdate = false; }
    }

    private static ComboBox BuildChoice(ParameterSpec spec, string current)
    {
        var combo = new ComboBox { FontSize = 11 };
        foreach (var choice in spec.Choices ?? []) combo.Items.Add(choice);
        combo.SelectedItem = combo.Items.Contains(current) ? current : combo.Items.Cast<string>().FirstOrDefault();
        return combo;
    }

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

    /// <summary>Persists the schema panel's values before a start.</summary>
    private void SaveEngineParameters(IEngineBackend backend)
    {
        if (_engineUpdate || backend.Parameters.Count == 0) return;
        var settings = _config.Engines.GetValueOrDefault(backend.Descriptor.Id) ?? new EngineSettings();
        foreach (var spec in backend.Parameters)
            if (_engineParameterBoxes.TryGetValue(spec.Key, out var box))
                settings.Parameters[spec.Key] = box.Text;
        if (string.IsNullOrEmpty(settings.ModelPath)) settings.ModelPath = EngineModelFor(backend);
        _config.Engines[backend.Descriptor.Id] = settings;
        ConfigService.SaveEngineSettings(_config, backend.Descriptor.Id, settings);
    }

    private async void EngineBootstrapButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = SelectedEngineBackend();
        if (backend == null || _serverStarting || _serverStopping) return;
        EngineOutputBox.Clear();
        try
        {
            await BootstrapService.RunAsync(backend.Descriptor.Id, new DelegateLogSink(AppendEngineOutput), _lifetime.Token);
            AppendEngineOutput("初始化完成。");
        }
        catch (OperationCanceledException) { AppendEngineOutput("已取消。"); }
        catch (Exception ex) { ShowError("初始化失败", ex); }
        finally { await RefreshEngineAsync(); }
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
