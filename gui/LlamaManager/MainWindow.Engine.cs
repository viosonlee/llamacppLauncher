using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager;

/// <summary>
/// The backend-agnostic engine page. It lists every registered backend, generates its
/// parameter controls from the declared schema, starts/stops the service and shows the
/// base URL an agent (pi, Claude Code, Codex) can attach to.
/// </summary>
public partial class MainWindow
{
    private bool _engineRunning, _engineBusy, _engineUpdate;
    private readonly Dictionary<string, TextBox> _engineParameterBoxes = new(StringComparer.OrdinalIgnoreCase);
    private EngineSession? _engineSession;

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
        RebuildEngineParameters();
        await RefreshEngineAsync();
    }

    private IEngineBackend? SelectedEngineBackend()
    {
        var index = EngineBackendBox.SelectedIndex;
        var all = BackendRegistry.All();
        return index >= 0 && index < all.Count ? all[index] : null;
    }

    private async void EngineBackendBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_engineUpdate) return;
        var backend = SelectedEngineBackend();
        if (backend != null)
        {
            _config.ActiveBackendId = backend.Descriptor.Id;
            ConfigService.SaveActiveBackend(_config, backend.Descriptor.Id);
        }
        RebuildEngineParameters();
        await RefreshEngineAsync();
    }

    private async void EngineBootstrapButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = SelectedEngineBackend();
        if (backend == null || _engineBusy) return;
        _engineBusy = true;
        RefreshEngineButtons();
        EngineOutputBox.Clear();
        try
        {
            await BootstrapService.RunAsync(backend.Descriptor.Id, new DelegateLogSink(AppendEngineOutput), _lifetime.Token);
            AppendEngineOutput("初始化完成。");
        }
        catch (OperationCanceledException) { AppendEngineOutput("已取消。"); }
        catch (Exception ex) { ShowError("初始化失败", ex); }
        finally { _engineBusy = false; await RefreshEngineAsync(); RefreshEngineButtons(); }
    }

    private async void EngineRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        EngineBackendBox.ItemsSource = BackendRegistry.All().Select(b => b.Descriptor.DisplayName).ToArray();
        RebuildEngineParameters();
        await RefreshEngineAsync();
    }

    private void RebuildEngineParameters()
    {
        _engineUpdate = true;
        try
        {
            _engineParameterBoxes.Clear();
            EngineParametersPanel.Children.Clear();
            var backend = SelectedEngineBackend();
            if (backend == null) return;
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
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
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
                        // A path parameter gets a browse button beside the text box.
                        var pathRow = new DockPanel();
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
                            pathBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                        };
                        DockPanel.SetDock(browse, Dock.Right);
                        pathRow.Children.Add(browse);
                        pathRow.Children.Add(pathBox);
                        row.Children.Add(pathRow);
                    }
                    else row.Children.Add(control);
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
            var running = _config.GetRunning(backend.Descriptor.Id) != null || _config.RunningServer != null && backend.Descriptor.Id == "llamacpp";
            _engineRunning = running;
            EngineStartButton.IsEnabled = status.Available && !_engineBusy;
            EngineStopButton.IsEnabled = running && !_engineBusy;
            EngineBaseUrlTextBox.Text = running
                ? $"http://127.0.0.1:{ResolveDisplayPort(backend)}/v1"
                : "";
            var model = backend.Descriptor.Id == "llamacpp" ? _config.ModelPath
                : _config.Engines.GetValueOrDefault(backend.Descriptor.Id)?.ModelPath ?? "";
            // Legacy fallback: an install configured before the engine page kept the model in StrataConfig.
            if (string.IsNullOrWhiteSpace(model) && backend.Descriptor.Id == "strata-upstream")
                model = _config.Strata.ModelPath;
            EngineModelTextBox.Text = model;
            if (!string.IsNullOrWhiteSpace(model) && File.Exists(model))
            {
                var report = await backend.InspectAsync(model, _lifetime.Token);
                EngineCompatText.Text = report.Summary + (report.Estimate != null ? "\n" + report.Estimate.Note : "");
                EngineCompatText.ToolTip = report.Blockers.Count > 0 ? string.Join("\n", report.Blockers) : null;
                EngineStartButton.IsEnabled = status.Available && report.Runnable && !_engineBusy;
            }
            else EngineCompatText.Text = "选择模型后检查兼容性。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EngineStatusText.Text = "探测失败：" + ex.Message; }
    }

    private int ResolveDisplayPort(IEngineBackend backend)
    {
        if (backend is IServerBackend server) return server.ResolvePort(new EngineStartRequest { Config = _config });
        return 0;
    }

    private void EngineModelButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择模型 (.gguf)", Filter = "GGUF 模型|*.gguf" };
        if (dialog.ShowDialog() != true) return;
        EngineModelTextBox.Text = dialog.FileName;
        var backend = SelectedEngineBackend();
        if (backend == null) return;
        if (backend.Descriptor.Id == "llamacpp") _config.ModelPath = dialog.FileName;
        else
        {
            // Every non-llama.cpp backend (Strata, plugins) keeps its model in its own settings.
            var settings = _config.Engines.GetValueOrDefault(backend.Descriptor.Id) ?? new EngineSettings();
            settings.ModelPath = dialog.FileName;
            _config.Engines[backend.Descriptor.Id] = settings;
            ConfigService.SaveEngineSettings(_config, backend.Descriptor.Id, settings);
        }
        _ = RefreshEngineAsync();
    }

    private async void EngineStartButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = SelectedEngineBackend();
        if (backend is not IServerBackend server || _engineBusy || _engineRunning) return;
        SaveEngineParameters(backend);
        // GPU mutual exclusion: a GPU-heavy engine cannot start while another holds the GPU.
        var conflict = EngineArbiter.ConflictFor(_config, backend.Descriptor.Id);
        if (conflict != null)
        {
            var decision = MessageBox.Show(
                $"{EngineArbiter.DisplayName(conflict)} 正在运行，两者共享显卡。\n\n现在停止它并继续启动 {backend.Descriptor.DisplayName} 吗？",
                "显存冲突", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (decision != MessageBoxResult.OK) return;
            try
            {
                var other = BackendRegistry.ServerById(conflict)!;
                var otherSession = _config.GetRunning(conflict) ?? _config.RunningServer;
                await other.StopAsync(otherSession != null ? LlamaCppBackend.ToEngineSession(otherSession) : new EngineSession { BackendId = conflict },
                    new DelegateLogSink(LogService.Write));
            }
            catch (Exception ex) { ShowError("停止冲突引擎失败", ex); return; }
        }
        _engineBusy = true;
        RefreshEngineButtons();
        EngineOutputBox.Clear();
        try
        {
            _engineSession = await server.StartAsync(new EngineStartRequest { Config = _config }, new DelegateLogSink(AppendEngineOutput), _lifetime.Token);
            _config = ConfigService.ReadConfig();
            EngineRunStatusText.Text = $"服务已启动：http://127.0.0.1:{_engineSession.Port}/v1";
            EngineBaseUrlTextBox.Text = $"http://127.0.0.1:{_engineSession.Port}/v1";
        }
        catch (OperationCanceledException) { AppendEngineOutput("已取消。"); }
        catch (Exception ex) { ShowError("启动引擎失败", ex); AppendEngineOutput("失败：" + ex.Message); }
        finally { _engineBusy = false; await RefreshEngineAsync(); RefreshEngineButtons(); }
    }

    private async void EngineStopButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = SelectedEngineBackend();
        if (backend is not IServerBackend server || _engineBusy) return;
        _engineBusy = true;
        RefreshEngineButtons();
        try
        {
            EngineSession? session = _config.GetRunning(backend.Descriptor.Id) is { } slot ? LlamaCppBackend.ToEngineSession(slot) : null;
            if (session == null && backend.Descriptor.Id == "llamacpp" && _config.RunningServer is { } main)
                session = LlamaCppBackend.ToEngineSession(main);
            session ??= _engineSession ?? new EngineSession { BackendId = backend.Descriptor.Id };
            await server.StopAsync(session, new DelegateLogSink(AppendEngineOutput));
            _config = ConfigService.ReadConfig();
            EngineRunStatusText.Text = "服务已停止。";
        }
        catch (Exception ex) { ShowError("停止引擎失败", ex); }
        finally { _engineBusy = false; await RefreshEngineAsync(); RefreshEngineButtons(); }
    }

    private void EngineCopyUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EngineBaseUrlTextBox.Text)) return;
        try { Clipboard.SetText(EngineBaseUrlTextBox.Text); EngineRunStatusText.Text = "已复制接入地址。"; }
        catch (Exception ex) { ShowError("复制失败", ex); }
    }

    private void SaveEngineParameters(IEngineBackend backend)
    {
        if (_engineUpdate || backend.Parameters.Count == 0) return;
        var settings = _config.Engines.GetValueOrDefault(backend.Descriptor.Id) ?? new EngineSettings();
        var panel = EngineParametersPanel;
        var index = 0;
        foreach (var spec in backend.Parameters)
        {
            if (!_engineParameterBoxes.TryGetValue(spec.Key, out var box)) { index++; continue; }
            settings.Parameters[spec.Key] = box.Text;
        }
        if (settings.ModelPath.Length == 0) settings.ModelPath = EngineModelTextBox.Text;
        _config.Engines[backend.Descriptor.Id] = settings;
        ConfigService.SaveEngineSettings(_config, backend.Descriptor.Id, settings);
    }

    private void AppendEngineOutput(string text)
    {
        Dispatcher.BeginInvoke(() => { EngineOutputBox.AppendText(text + "\n"); EngineOutputBox.ScrollToEnd(); });
    }

    private void RefreshEngineButtons()
    {
        EngineStartButton.Content = _engineBusy ? "处理中…" : "启动服务";
        EngineStartButton.IsEnabled = !_engineBusy;
        EngineStopButton.IsEnabled = !_engineBusy && _engineRunning;
    }
}
