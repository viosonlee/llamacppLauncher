using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager;

public partial class MainWindow
{
    private bool _strataRunning, _strataStopping;
    private string? _strataLogPath;

    private void ApplyStrataConfig()
    {
        var s = _config.Strata;
        if (string.IsNullOrWhiteSpace(s.PackageRoot))
        {
            try
            {
                var detected = StrataService.DetectPackageRoot();
                if (detected != null) { s.PackageRoot = detected; ConfigService.SaveStrata(_config); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _strataUpdate++;
        StrataRootTextBox.Text = s.PackageRoot;
        StrataModelTextBox.Text = s.ModelPath;
        StrataPromptTextBox.Text = s.Prompt;
        StrataReserveBox.Text = s.ReserveMib.ToString();
        StrataContextBox.Text = s.Context.ToString();
        StrataMaxNewBox.Text = s.MaxNew.ToString();
        StrataPrefillBox.Text = s.Prefill.ToString();
        StrataWorkersBox.Text = s.Workers.ToString();
        if (StrataKvBox.Items.Count == 0)
            foreach (var kv in new[] { "int8", "fp16", "k8v4", "q4_0" }) StrataKvBox.Items.Add(kv);
        StrataKvBox.SelectedItem = s.Kv;
        _strataUpdate--;
        StrataStatsText.Text = s.LastStats ?? "";
        _strataLogPath = StrataService.ResolvePathsSafe(_config) is { } paths ? paths.LastRunLog : null;
        StrataOpenLogButton.IsEnabled = _strataLogPath != null && File.Exists(_strataLogPath);
        RefreshStrataButtons();
    }

    private bool SaveStrataSettings()
    {
        var s = _config.Strata;
        s.PackageRoot = StrataRootTextBox.Text.Trim();
        s.ModelPath = StrataModelTextBox.Text.Trim();
        s.Prompt = StrataPromptTextBox.Text;
        if (!int.TryParse(StrataReserveBox.Text.Trim(), out var reserve) || reserve < 0 || reserve > 60000)
            throw new ArgumentException("Reserve 必须为 0-60000 的 MiB 数。");
        s.ReserveMib = reserve;
        if (!int.TryParse(StrataContextBox.Text.Trim(), out var context) || context < 512 || context > 1048576)
            throw new ArgumentException("上下文必须为 512-1048576。");
        s.Context = context;
        if (!int.TryParse(StrataMaxNewBox.Text.Trim(), out var maxNew) || maxNew < 1 || maxNew > 65536)
            throw new ArgumentException("生成长度必须为 1-65536。");
        s.MaxNew = maxNew;
        if (!int.TryParse(StrataPrefillBox.Text.Trim(), out var prefill) || prefill < 128 || prefill > 65536)
            throw new ArgumentException("Prefill 必须为 128-65536。");
        s.Prefill = prefill;
        if (!int.TryParse(StrataWorkersBox.Text.Trim(), out var workers) || workers < 0 || workers > 256)
            throw new ArgumentException("池线程必须为 0-256（0 = 自动）。");
        s.Workers = workers;
        s.Kv = StrataKvBox.SelectedItem?.ToString() ?? "int8";
        ConfigService.SaveStrata(_config);
        return true;
    }

    private void RefreshStrataButtons()
    {
        StrataRunButton.Content = _strataRunning ? "⏳ 推理中…" : "▶ 运行推理";
        StrataRunButton.IsEnabled = !_strataRunning && !_strataStopping && !_serverStarting;
        StrataStopButton.IsEnabled = _strataRunning || _strataStopping;
        StrataOpenLogButton.IsEnabled = _strataLogPath != null && File.Exists(_strataLogPath);
    }

    private void AppendStrataOutput(string text)
    {
        StrataOutputBox.AppendText(text + "\n");
        StrataOutputScroll.ScrollToEnd();
    }

    private async void StrataRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_strataRunning || _strataStopping) return;
        try { SaveStrataSettings(); }
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
        RefreshStrataButtons();
        StrataOutputBox.Clear();
        AppendStrataOutput($"[{DateTime.Now:HH:mm:ss}] 开始 Strata 推理…");
        var config = _config;
        try
        {
            var result = await Task.Run(() => StrataService.RunAsync(config,
                message => Dispatcher.BeginInvoke(() => AppendStrataOutput(message)), _lifetime.Token));
            AppendStrataOutput("");
            AppendStrataOutput("=== 回答 ===");
            AppendStrataOutput(result.Answer);
            AppendStrataOutput("");
            StrataStatsText.Text = result.Stats;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError("Strata 推理失败", ex); AppendStrataOutput("失败：" + ex.Message); }
        finally
        {
            _strataRunning = false;
            RefreshStrataButtons();
        }
    }

    private void StrataStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_strataStopping) return;
        _strataStopping = true;
        RefreshStrataButtons();
        try { StrataService.Stop(_config, LogService.Write); }
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
        _config.Strata.ModelPath = dialog.FileName;
        StrataModelTextBox.Text = dialog.FileName;
        try { SaveStrataSettings(); } catch (Exception ex) { ShowError("保存失败", ex); }
    }

    private void StrataModelResetButton_Click(object sender, RoutedEventArgs e)
    {
        _config.Strata.ModelPath = "";
        StrataModelTextBox.Text = "";
        try { SaveStrataSettings(); } catch (Exception ex) { ShowError("保存失败", ex); }
    }

    private void StrataPromptTextBox_LostFocus(object sender, RoutedEventArgs e) => SaveStrataField();

    private void StrataParam_LostFocus(object sender, RoutedEventArgs e) => SaveStrataField();

    private void StrataKvBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveStrataField();

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
