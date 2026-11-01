using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private ManagerConfig _config = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _startCancellation, _speedCancellation, _embeddingCancellation;
    private readonly SpeedMonitorService _speedMonitor = new();
    private DispatcherTimer? _statusTimer;
    private List<ModelInfo> _models = [];
    private ICollectionView? _modelView;
    private string _modelQuery = "";
    private int _modelSortMode;
    private bool _loaded, _parsingArgs, _refreshing, _serverStarting, _serverStopping, _embeddingStarting, _speedTesting, _selecting, _scanning;
    private int _updateCount, _statusVersion;
    private string? _lastLogText;
    // One log line per finished request: the request stamp already seen for the current session.
    private ServerSession? _loggedSpeedSession;
    private double _loggedSpeedStamp;

    /// <summary>The built-in llama.cpp backend, resolved once from the registry.</summary>
    private static IServerBackend MainBackend => BackendRegistry.ServerById("llamacpp")!;

    public MainWindow()
    {
        InitializeComponent();
        // CPU-core choices are built by the schema (LlamaCppParameters), not by hand here.
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _config = ConfigService.ReadConfig();
            RestoreWindowPlacement();
            var recovery = ConfigService.RecoveryWarning;
            // Render the controls before the slower model scan and engine probe. The window is
            // interactive immediately; Start remains gated by _scanning until the model list is ready.
            InitializeEngineSelection();
            ApplyConfig();
            _loaded = true;
            if (recovery != null) MessageBox.Show(recovery, "配置恢复");
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
            _statusTimer.Start();
            await RefreshStatusAsync();
            await Task.WhenAll(RefreshModelsAsync(), RefreshEngineAsync());
        }
        catch (Exception ex)
        {
            ShowError("初始化失败", ex);
            // Keep the invalid file intact: closing a failed initialization must never save defaults.
            IsEnabled = false;
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_loaded)
        {
            try
            {
                CaptureWindowPlacement();
                SaveCurrentConfig();
                var backend = SelectedEngineBackend();
                if (backend != null && backend is not LlamaCppBackend)
                {
                    SaveEngineParameters(backend);
                }
            }
            catch (Exception ex) { ShowError("保存失败，请修正参数后再关闭", ex); e.Cancel = true; return; }
        }
        _statusTimer?.Stop();
        _lifetime.Cancel();
        _startCancellation?.Cancel();
        _embeddingCancellation?.Cancel();
        _speedCancellation?.Cancel();
    }

    /// <summary>
    /// Restores the last window size/position without letting a config edited on another monitor
    /// make the window disappear. Out-of-range values fall back to CenterScreen.
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var saved = _config.Window ?? new WindowSettings();
        var maxWidth = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
        var maxHeight = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
        Width = Math.Clamp(saved.Width, MinWidth, maxWidth);
        Height = Math.Clamp(saved.Height, MinHeight, maxHeight);

        if (saved.Left is { } left && saved.Top is { } top)
        {
            var virtualScreen = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var candidate = new Rect(left, top, Width, Height);
            if (virtualScreen.IntersectsWith(candidate) && left < virtualScreen.Right && top < virtualScreen.Bottom)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    private void CaptureWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            _config.Window.Width = Math.Round(bounds.Width);
            _config.Window.Height = Math.Round(bounds.Height);
            if (double.IsFinite(bounds.Left)) _config.Window.Left = Math.Round(bounds.Left);
            if (double.IsFinite(bounds.Top)) _config.Window.Top = Math.Round(bounds.Top);
        }
        _config.Window.Maximized = WindowState == WindowState.Maximized;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key == Key.Enter && StartButton.IsEnabled)
        {
            StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S && StopButton.IsEnabled)
        {
            StopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            ConfigurationTabs.SelectedIndex = 0;
            ModelSearchBox.Focus();
            ModelSearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            ConfigurationTabs.SelectedIndex = ConfigurationTabs.Items.Count - 1;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ModelSearchBox.IsKeyboardFocusWithin)
        {
            ModelSearchBox.Clear();
            e.Handled = true;
        }
    }

    private void ConfigurationTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (ConfigurationTabs.SelectedIndex == ConfigurationTabs.Items.Count - 1) RefreshLog();
    }

    /// <summary>The log page and the foreign-engine output box are the only surfaces that render logs.</summary>
    private bool IsLogSurfaceVisible() =>
        ConfigurationTabs.SelectedIndex == ConfigurationTabs.Items.Count - 1
        || (ConfigurationTabs.SelectedIndex == 2 && EngineAdvancedPanel.Visibility == Visibility.Visible);

    private void ApplyConfig()
    {
        _updateCount++;
        try
        {
            ArgsTextBox.Text = _config.Args;
            PortTextBox.Text = _config.Port.ToString();
            ModelsDirTextBox.Text = string.IsNullOrEmpty(_config.ModelsDirectory) ? Path.Combine(ConfigService.GetRoot(), "models") : _config.ModelsDirectory;
            AutoOpenBrowserCheckBox.IsChecked = _config.AutoOpenBrowser;
            // Projects the loaded model's MoE/MTP flags and the raw args onto the schema panel.
            UpdateSelectedModelText();
        }
        finally { _updateCount--; }
    }

    private void SaveCurrentConfig()
    {
        var args = new LauncherArguments(ArgsTextBox.Text);
        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
            throw new ArgumentException("端口必须为 1–65535。");
        _config.Args = ArgsTextBox.Text.Trim();
        _config.Port = args.GetPort(port);
        _config.AutoOpenBrowser = AutoOpenBrowserCheckBox.IsChecked == true;
        ConfigService.SaveModelPreset(_config, _config.ModelPath, _config.Args, _config.Port);
        ConfigService.SaveConfig(_config);
    }

    private void AutoOpenBrowserCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _updateCount > 0) return;
        try { SaveCurrentConfig(); } catch (Exception ex) { ShowError("保存失败", ex); }
    }

    private void RefreshLog()
    {
        var backendId = SelectedEngineBackend()?.Descriptor.Id ?? _config.ActiveBackendId;
        string text;
        try { text = string.Join("\n", LogService.ReadRecent(backendId)); }
        catch (Exception ex)
        {
            // A half-rotated log file must not take the status poll down.
            text = _lastLogText ?? "";
            LogService.Write("读取日志失败：" + ex.Message);
            return;
        }
        if (text == _lastLogText) return;
        _lastLogText = text;
        LogTextBox.Text = text;
        LogScrollViewer.ScrollToEnd();
    }

    private void ShowError(string title, Exception ex)
    {
        LogService.Write($"{title}：{ex.Message}");
        if (!_lifetime.IsCancellationRequested) MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}


