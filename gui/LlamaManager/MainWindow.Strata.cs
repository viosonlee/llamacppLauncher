using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager;

public partial class MainWindow
{
    private bool _strataRunning, _strataStopping, _strataPreparing;
    private string? _strataLogPath;
    private CancellationTokenSource? _strataRunCancellation;
    private CancellationTokenSource? _strataPreparationCancellation;

    private async Task ApplyStrataConfigAsync()
    {
        var s = _config.Strata;
        if (string.IsNullOrWhiteSpace(s.PackageRoot))
        {
            try
            {
                var detected = await Task.Run(StrataService.DetectPackageRoot);
                if (_lifetime.IsCancellationRequested) return;
                if (detected != null) { s.PackageRoot = detected; ConfigService.SaveStrata(_config); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _strataUpdate++;
        StrataRootTextBox.Text = s.PackageRoot;
        StrataModelTextBox.Text = s.ModelPath;
        StrataPackTextBox.Text = s.PackDir;
        StrataProfileTextBox.Text = s.ProfilePath;
        StrataMtpTextBox.Text = s.MtpDir;
        StrataPythonTextBox.Text = s.PythonPath;
        StrataCacheTextBox.Text = s.PackCacheDirectory;
        StrataArenaPinBox.Text = s.ArenaPinGib.ToString();
        StrataSpecBox.Text = s.Spec.ToString();
        StrataMtpCheckBox.IsChecked = s.MtpEnabled;
        StrataMtpPlainCheckBox.IsChecked = s.MtpPlain;
        StrataPromptTextBox.Text = s.Prompt;
        StrataReserveBox.Text = s.ReserveMib.ToString();
        StrataContextBox.Text = s.Context.ToString();
        StrataMaxNewBox.Text = s.MaxNew.ToString();
        StrataPrefillBox.Text = s.Prefill.ToString();
        StrataWorkersBox.Text = s.Workers.ToString();
        if (StrataKvBox.Items.Count == 0)
            foreach (var kv in new[] { "int8", "fp16", "k8v4", "q4_0" }) StrataKvBox.Items.Add(kv);
        StrataKvBox.SelectedItem = s.Kv;
        if (StrataAffinityBox.Items.Count == 0)
            foreach (var affinity in new[] { "auto", "all", "p-cores" }) StrataAffinityBox.Items.Add(affinity);
        StrataAffinityBox.SelectedItem = s.Affinity;
        _strataUpdate--;
        StrataStatsText.Text = s.LastStats ?? "";
        if (!string.IsNullOrWhiteSpace(s.PackDir)) StrataPackStatusText.Text = "当前关联 Pack：" + s.PackDir;
        _strataLogPath = StrataService.ResolvePathsSafe(_config) is { } paths ? paths.LastRunLog : null;
        StrataOpenLogButton.IsEnabled = _strataLogPath != null && File.Exists(_strataLogPath);
        RefreshStrataButtons();
    }

    private bool SaveStrataSettings()
    {
        int Number(TextBox field, int min, int max, string label)
        {
            if (!int.TryParse(field.Text.Trim(), out var value) || value < min || value > max)
                throw new ArgumentException($"{label} 必须为 {min}-{max}。");
            return value;
        }
        // Validate all values before changing the editable snapshot or persisting it.
        var spec = Number(StrataSpecBox, 2, 8, "Spec");
        var reserve = Number(StrataReserveBox, 0, 60000, "显存预留 MiB");
        var context = Number(StrataContextBox, 512, 1048576, "上下文");
        var maxNew = Number(StrataMaxNewBox, 1, 65536, "生成长度");
        var prefill = Number(StrataPrefillBox, 128, 65536, "Prefill");
        var workers = Number(StrataWorkersBox, 0, 256, "池线程（0 自动）");
        var pin = Number(StrataArenaPinBox, 0, 128, "锁页 RAM GiB（0 自动）");
        if (maxNew >= context) throw new ArgumentException("生成长度必须小于上下文容量。");
        var s = _config.Strata;
        s.PackageRoot = StrataRootTextBox.Text.Trim();
        s.ModelPath = StrataModelTextBox.Text.Trim();
        s.PackDir = StrataPackTextBox.Text.Trim();
        s.ProfilePath = StrataProfileTextBox.Text.Trim();
        s.MtpDir = StrataMtpTextBox.Text.Trim();
        s.MtpEnabled = StrataMtpCheckBox.IsChecked == true;
        s.MtpPlain = StrataMtpPlainCheckBox.IsChecked == true;
        s.PythonPath = StrataPythonTextBox.Text.Trim();
        s.PackCacheDirectory = StrataCacheTextBox.Text.Trim();
        s.ArenaPinGib = pin;
        s.Spec = spec;
        s.Prompt = StrataPromptTextBox.Text;
        s.ReserveMib = reserve;
        s.Context = context;
        s.MaxNew = maxNew;
        s.Prefill = prefill;
        s.Workers = workers;
        s.Kv = StrataKvBox.SelectedItem?.ToString() ?? "int8";
        s.Affinity = StrataAffinityBox.SelectedItem?.ToString() ?? "auto";
        ConfigService.SaveStrata(_config);
        return true;
    }

    private void RefreshStrataButtons()
    {
        StrataRunButton.Content = _strataRunning ? "推理中…" : "运行推理";
        StrataRunButton.IsEnabled = !_strataRunning && !_strataStopping && !_strataPreparing && !_serverStarting && !_serverStopping;
        StrataStopButton.IsEnabled = _strataRunning || _strataStopping || _strataPreparing;
        StrataGeneratePackButton.IsEnabled = !_strataRunning && !_strataStopping && !_strataPreparing;
        StrataSourcePanel.IsEnabled = !_strataPreparing;
        StrataPackProgress.Visibility = _strataPreparing ? Visibility.Visible : Visibility.Collapsed;
        StrataOpenLogButton.IsEnabled = _strataLogPath != null && File.Exists(_strataLogPath);
        // Footer lifecycle buttons mirror the Strata lock (shared VRAM, cannot coexist).
        _ = RefreshStatusAsync();
    }

    private void AppendStrataOutput(string text)
    {
        StrataOutputBox.AppendText(text + "\n");
        StrataOutputScroll.ScrollToEnd();
    }

    private async void StrataRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_strataRunning || _strataStopping || _strataPreparing) return;
        try
        {
            SaveStrataSettings();
            var paths = StrataService.ResolvePaths(_config);
            StrataService.CreateEngineStart(_config, paths, StrataService.ResolveModel(_config, paths), "tokens.txt");
        }
        catch (Exception ex) { ShowError("Strata 参数无效", ex); return; }

        // Strata 与 llama-server 共享显存：后者在运行时必须先停（AGENTS.md：不能替用户静默停真实服务）。
        if (ServerService.IsRunning(_config))
        {
            var decision = MessageBox.Show(
                "Strata 与 llama-server 共享显卡，无法同时运行。\n\n现在停止 llama-server 并继续启动 Strata 吗？",
                "显存冲突", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (decision != MessageBoxResult.OK) return;
            try { await ServerService.StopAsync(_config, LogService.Write); }
            catch (Exception ex) { ShowError("停止 llama-server 失败", ex); return; }
            await RefreshStatusAsync();
        }

        _strataRunning = true;
        _strataRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        RefreshStrataButtons();
        StrataOutputBox.Clear();
        AppendStrataOutput($"[{DateTime.Now:HH:mm:ss}] 开始 Strata 推理…");
        var config = _config;
        try
        {
            var result = await Task.Run(() => StrataService.RunAsync(config,
                message => Dispatcher.BeginInvoke(() => AppendStrataOutput(message)), _strataRunCancellation.Token));
            AppendStrataOutput("");
            AppendStrataOutput("=== 回答 ===");
            AppendStrataOutput(result.Answer);
            AppendStrataOutput("");
            StrataStatsText.Text = result.Stats;
            _strataLogPath = result.LogPath;
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) AppendStrataOutput("推理已取消。"); }
        catch (Exception ex) { ShowError("Strata 推理失败", ex); AppendStrataOutput("失败：" + ex.Message); }
        finally
        {
            _strataRunning = false;
            _strataRunCancellation?.Dispose();
            _strataRunCancellation = null;
            RefreshStrataButtons();
        }
    }

    private async void StrataStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_strataStopping) return;
        _strataStopping = true;
        RefreshStrataButtons();
        try
        {
            // All three in order: a live engine must be stopped even if a stale
            // cancellation token survived a race; Stop() is a no-op when idle.
            if (_strataPreparationCancellation != null) _strataPreparationCancellation.Cancel();
            if (_strataRunCancellation != null) _strataRunCancellation.Cancel();
            await Task.Run(() => StrataService.Stop(_config, LogService.Write));
        }
        catch (Exception ex) { ShowError("停止失败", ex); }
        finally { _strataStopping = false; RefreshStrataButtons(); }
    }

    private void StrataRootButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择 Strata 包目录" };
        if (dialog.ShowDialog() != true) return;
        StrataRootTextBox.Text = dialog.FolderName;
        _config.Strata.PackageRoot = dialog.FolderName;
        try
        {
            _strataUpdate++;
            StrataModelTextBox.Text = _config.Strata.ModelPath = "";
            _strataUpdate--;
            SaveStrataSettings();
        }
        catch (Exception ex) { ShowError("保存失败", ex); }
    }

    private void StrataModelButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择 Strata 模型 (.gguf)", Filter = "GGUF 模型|*.gguf" };
        if (dialog.ShowDialog() != true) return;
        SelectStrataModel(dialog.FileName);
        try { SaveStrataSettings(); } catch (Exception ex) { ShowError("保存失败", ex); }
    }

    private void StrataModelResetButton_Click(object sender, RoutedEventArgs e)
    {
        SelectStrataModel("");
        try { SaveStrataSettings(); } catch (Exception ex) { ShowError("保存失败", ex); }
    }

    private void StrataPromptTextBox_LostFocus(object sender, RoutedEventArgs e) => SaveStrataField();

    private void StrataParam_LostFocus(object sender, RoutedEventArgs e) => SaveStrataField();

    private void StrataKvBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveStrataField();

    private void StrataMtp_Changed(object sender, RoutedEventArgs e) => SaveStrataField();

    private void StrataPathButton_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag?.ToString();
        if (tag is "Profile" or "Python")
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = tag == "Python" ? "选择 Python 3.10+" : "选择路由 profile",
                Filter = tag == "Python" ? "Python executable|python.exe|Executable|*.exe" : "Expert profile|*.bin"
            };
            if (dialog.ShowDialog() != true) return;
            (tag == "Python" ? StrataPythonTextBox : StrataProfileTextBox).Text = dialog.FileName;
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = tag == "Pack" ? "选择模型 pack" : "选择 MTP runtime" };
            if (dialog.ShowDialog() != true) return;
            (tag == "Pack" ? StrataPackTextBox : tag == "Cache" ? StrataCacheTextBox : StrataMtpTextBox).Text = dialog.FolderName;
        }
        SaveStrataField();
    }

    private void SelectStrataModel(string model)
    {
        if (PathIdentity.Equals(_config.Strata.ModelPath, model)) return;
        _strataUpdate++;
        try
        {
            _config.Strata.ModelPath = StrataModelTextBox.Text = model;
            StrataPackTextBox.Text = StrataProfileTextBox.Text = StrataMtpTextBox.Text = "";
            StrataMtpCheckBox.IsChecked = false;
            StrataPackStatusText.Text = "新模型需要对应的 Pack；点击一键生成可创建或复用缓存。";
        }
        finally { _strataUpdate--; }
    }

    private async void StrataGeneratePackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_strataPreparing || _strataRunning || _strataStopping) return;
        try { SaveStrataSettings(); }
        catch (Exception ex) { ShowError("Strata 参数无效", ex); return; }
        _strataPreparing = true;
        _strataPreparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var s = _config.Strata;
        var options = new StrataPackService.Options(s.PackageRoot, s.ModelPath, s.PythonPath, s.PackCacheDirectory);
        RefreshStrataButtons();
        StrataOutputBox.Clear();
        StrataPackStatusText.Text = "准备中；可点击停止取消。已有模型服务无需停止。";
        try
        {
            var result = await StrataPackService.PrepareAsync(options, message =>
            {
                if (!_lifetime.IsCancellationRequested)
                    Dispatcher.BeginInvoke(() => AppendStrataOutput(message));
            }, _strataPreparationCancellation.Token);
            if (_lifetime.IsCancellationRequested) return;
            _strataUpdate++;
            try
            {
                StrataPackTextBox.Text = result.PackDir;
                StrataProfileTextBox.Text = result.ProfilePath;
            }
            finally { _strataUpdate--; }
            SaveStrataSettings();
            _strataLogPath = result.LogPath;
            StrataPackStatusText.Text = result.Reused ? "已复用匹配的 Pack，可运行推理。" : "Pack 与初始 Profile 已生成并关联，可运行推理。";
        }
        catch (OperationCanceledException) { StrataPackStatusText.Text = "准备已取消；原有 Pack 未改动。"; }
        catch (Exception ex)
        {
            StrataPackStatusText.Text = "准备失败：" + ex.Message;
            AppendStrataOutput(StrataPackStatusText.Text);
            if (!_lifetime.IsCancellationRequested) ShowError("生成 Pack 失败", ex);
        }
        finally
        {
            _strataPreparationCancellation?.Dispose();
            _strataPreparationCancellation = null;
            _strataPreparing = false;
            RefreshStrataButtons();
        }
    }

    private void StrataRecommendedButton_Click(object sender, RoutedEventArgs e)
    {
        _strataUpdate++;
        try
        {
            StrataReserveBox.Text = "800";
            StrataContextBox.Text = "4096";
            StrataMaxNewBox.Text = "128";
            StrataPrefillBox.Text = "2048";
            StrataWorkersBox.Text = Math.Clamp(Environment.ProcessorCount - 1, 1, 6).ToString();
            StrataKvBox.SelectedItem = "int8";
            StrataAffinityBox.SelectedItem = "auto";
            StrataSpecBox.Text = "2";
            StrataArenaPinBox.Text = (sender as Button)?.Tag?.ToString() == "24gb" ? "12" : "0";
            StrataMtpPlainCheckBox.IsChecked = true;
        }
        finally { _strataUpdate--; }
        SaveStrataField();
    }

    private void StrataHelpButton_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextBox
        {
            Text = StrataParameterHelp.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16), FontSize = 13
        };
        new Window
        {
            Title = "Strata 参数与新模型准备", Owner = this, Width = 650, Height = 620,
            MinWidth = 420, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background, Content = text
        }.ShowDialog();
    }

    private void SaveStrataField()
    {
        if (!_loaded || _strataUpdate > 0) return;
        try { SaveStrataSettings(); } catch (Exception ex) { ShowError("保存失败", ex); }
    }

    private void StrataOpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_strataLogPath == null || !File.Exists(_strataLogPath)) return;
        Process.Start(new ProcessStartInfo(_strataLogPath) { UseShellExecute = true });
    }
}
