using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private bool _loaded, _parsingArgs, _refreshing, _serverStarting, _serverStopping, _embeddingStarting, _speedTesting, _selecting, _scanning;
    private int _updateCount, _statusVersion;
    private string? _lastLogText;

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
            var recovery = ConfigService.RecoveryWarning;
            await RefreshModelsAsync();
            ApplyConfig();
            await ApplyEngineConfigAsync();
            _loaded = true;
            if (recovery != null) MessageBox.Show(recovery, "配置恢复");
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
            _statusTimer.Start();
            await RefreshStatusAsync();
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
            try { SaveCurrentConfig(); }
            catch (Exception ex) { ShowError("保存失败，请修正参数后再关闭", ex); e.Cancel = true; return; }
        }
        _statusTimer?.Stop();
        _lifetime.Cancel();
        _startCancellation?.Cancel();
        _embeddingCancellation?.Cancel();
        _speedCancellation?.Cancel();
    }

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
        var text = string.Join("\n", LogService.ReadRecent(backendId));
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


