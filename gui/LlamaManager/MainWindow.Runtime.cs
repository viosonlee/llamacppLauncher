using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager;

public partial class MainWindow
{
    private async Task RefreshStatusAsync()
    {
        if (_refreshing || !_loaded || _lifetime.IsCancellationRequested) return;
        _refreshing = true;
        var version = _statusVersion;
        var selectedModel = _config.ModelPath;
        try
        {
            using var process = ServerService.GetManagedProcess(_config);
            using var embedding = ServerService.GetEmbeddingProcess(_config);
            var session = process == null ? null : _config.RunningServer;
            var embeddingSession = embedding == null ? null : _config.RunningEmbedding;
            var readyTask = session == null ? Task.FromResult(false) : ServerService.TestApiReadyAsync(session.Port, _lifetime.Token);
            var embeddingReadyTask = embeddingSession == null ? Task.FromResult(false) : ServerService.TestApiReadyAsync(embeddingSession.Port, _lifetime.Token);
            var cudaTask = CudaService.DetectAsync(selectedModel);
            var autostartTask = AutostartService.GetStatusAsync();
            await Task.WhenAll(readyTask, embeddingReadyTask, cudaTask, autostartTask);
            var status = readyTask.Result ? await _speedMonitor.QuerySpeedAsync(session!, _lifetime.Token) : new SpeedStatus("等待模型就绪…", SpeedKind.Idle);
            if (_lifetime.IsCancellationRequested || version != _statusVersion) return;
            // External CLI start/stop can change identity while HTTP checks are in flight.
            var latest = ConfigService.ReadConfig();
            if (latest.RunningServer != _config.RunningServer || latest.RunningEmbedding != _config.RunningEmbedding) return;
            var running = session != null && process?.HasExited == false;
            var ready = running && readyTask.Result;
            var busy = _serverStarting || _serverStopping;
            var brush = (Brush)FindResource(ready ? "SystemFillColorSuccessBrush" : running || busy ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush");
            StatusDot.Fill = brush;
            StatusText.Text = _serverStopping ? "正在停止…" : running ? ready ? "服务运行中" : "模型加载中…" : busy ? "正在启动…" : "已停止";
            ActionStatusText.Text = _strataRunning || _strataStopping
                ? "Strata 运行中 · llama-server 不可用（共享显存）"
                : session != null && running
                    ? $"{Path.GetFileName(session.ModelPath)} · PID {session.ProcessId}"
                    : "选择模型后启动服务";
            ActionStatusText.Foreground = (Brush)FindResource(ready && !(_strataRunning || _strataStopping) ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
            ActionEndpointText.Text = session != null && running ? $"http://127.0.0.1:{session.Port}" : "未监听";
            ApiStatusBadge.Text = ready ? "API: 就绪" : running ? "API: 加载中" : "API: 离线";
            ApiStatusBadge.Foreground = brush;
            // Icons come from XAML (WPF-UI SymbolIcon); Content stays text-only.
            StartButton.Content = _serverStarting ? "正在启动…" : running ? "重启服务" : "启动服务";
            StopButton.Content = _serverStarting ? "终止启动" : "停止服务";
            // Strata 与 llama-server 共享显存：Strata 活动期间不允许拉起主服务。
            var strataBusy = _strataRunning || _strataStopping;
            StartButton.IsEnabled = !busy && !_selecting && !_scanning && !strataBusy;
            StopButton.IsEnabled = !_serverStopping && (running || _serverStarting);
            OpenButton.IsEnabled = ready && !busy && !strataBusy;
            SpeedTestButton.IsEnabled = (_speedTesting || ready && !busy) && !strataBusy;
            EmbeddingToggleButton.IsEnabled = !_embeddingStarting && !strataBusy;
            StartButton.ToolTip = strataBusy ? "Strata 正在运行，与 llama-server 共享显存，无法同时启动" : null;
            SpeedTestButton.ToolTip = strataBusy
                ? "Strata 正在运行，无法测速"
                : "实测推理速度：首Token延迟(TTFT) 与解码速度(tok/s)";
            LiveSpeedText.Text = running ? status.Text : "";
            LiveSpeedText.Foreground = (Brush)FindResource(status.IsActive ? "AccentFillColorDefaultBrush" : "SystemFillColorSuccessBrush");
            ProcessInfoText.Text = running
                ? $"{Path.GetFileName(Path.GetDirectoryName(session!.EnginePath))} · PID {session.ProcessId} · {LogService.GetGpuSummary(session)}" : "进程: 无";
            var cuda = cudaTask.Result;
            CudaText.Text = cuda.Available ? "CUDA: 可用（所选引擎）" : "CUDA: 未检测到";
            CudaText.ToolTip = cuda.Text + "\n设备内存为检测时快照；点击模型列表旁的刷新可重新检测。";
            PortText.Text = session != null && running ? $":{session.Port}" : $":{_config.Port}";
            EmbeddingStatusText.Text = embeddingSession == null ? "嵌入: 未启动" :
                $"嵌入: {(embeddingReadyTask.Result ? "就绪" : "加载中")} · {embeddingSession.Port}";
            EmbeddingStatusDot.Fill = (Brush)FindResource(embeddingSession == null ? "TextFillColorSecondaryBrush" : embeddingReadyTask.Result ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
            EmbeddingToggleButton.Content = _embeddingStarting ? "终止启动" : embeddingSession == null ? "启动" : "停止";
            AutostartStatusText.Text = autostartTask.Result.Text;
            AutostartButton.Content = autostartTask.Result.Enabled ? "关闭自启" : "开启自启";
            RefreshLog();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Write("状态检测失败：" + ex.Message); }
        finally { _refreshing = false; }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_serverStarting || _serverStopping || _selecting || _scanning) return;
        if (_strataRunning || _strataStopping)
        {
            MessageBox.Show("Strata 正在运行，与 llama-server 共享显存，无法同时启动。\n\n请先停止 Strata。",
                "显存冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _serverStarting = true;
        _statusVersion++;
        _speedCancellation?.Cancel();
        _startCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StopButton.Content = "终止启动";
        try
        {
            SaveCurrentConfig();
            var launch = new ManagerConfig { ModelPath = _config.ModelPath, Args = _config.Args, Port = _config.Port };
            var open = _config.AutoOpenBrowser;
            using var running = ServerService.GetManagedProcess(_config);
            if (running != null) await ServerService.StopAsync(_config, LogService.Write);
            _startCancellation.Token.ThrowIfCancellationRequested();
            await ServerService.StartAsync(launch, open, LogService.Write, _startCancellation.Token);
        }
        catch (OperationCanceledException) { LogService.Write("已结束此次启动等待。"); }
        catch (Exception ex) { ShowError("启动失败", ex); }
        finally
        {
            _startCancellation.Dispose(); _startCancellation = null;
            _serverStarting = false; _statusVersion++;
            await RefreshStatusAsync();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_serverStopping) return;
        _serverStopping = true;
        _statusVersion++;
        StopButton.IsEnabled = false;
        _startCancellation?.Cancel();
        _speedCancellation?.Cancel();
        try { await ServerService.StopAsync(_config, LogService.Write); }
        catch (Exception ex) { ShowError("停止失败", ex); }
        finally { _serverStopping = false; _statusVersion++; await RefreshStatusAsync(); }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = ServerService.GetManagedProcess(_config);
            if (process == null) return;
            if (await ServerService.TestApiReadyAsync(ServerService.GetRunningPort(_config), _lifetime.Token))
                ServerService.OpenWebUi(_config);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("打开页面失败", ex); }
    }

    private async void SpeedTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_speedTesting) { _speedCancellation?.Cancel(); return; }
        try
        {
            using var process = ServerService.GetManagedProcess(_config);
            if (process == null) return;
            var session = _config.RunningServer!;
            _speedTesting = true;
            _speedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _speedCancellation.CancelAfter(TimeSpan.FromMinutes(10));
            SpeedTestButton.Content = "取消测速";
            var result = await SpeedTestService.RunAsync(session.Port, _speedCancellation.Token);
            if (_speedCancellation.IsCancellationRequested || ConfigService.ReadConfig().RunningServer != session) return;
            if (!result.Success) throw new InvalidOperationException(result.Error);
            var tokens = result.HasUsage ? result.CompletionTokens.ToString() : "服务未报告";
            var text = $"模型：{result.Model}\n首个内容片段延迟：{result.TtftSeconds:F3} s\n" +
                $"解码速度（估算）：{result.DecodeTps?.ToString("F2") ?? "无法计算"} tok/s\n" +
                $"生成 tokens：{tokens}\n流事件数：{result.StreamEvents}\n请求耗时：{result.TotalSeconds:F3} s";
            LogService.Write(text);
            MessageBox.Show(text, "测速结果");
        }
        catch (Exception ex) { ShowError("测速失败", ex); }
        finally
        {
            _speedCancellation?.Dispose(); _speedCancellation = null;
            _speedTesting = false;
            SpeedTestButton.Content = "测速";
            await RefreshStatusAsync();
        }
    }

    private async void EmbeddingToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_embeddingStarting)
        {
            _embeddingCancellation?.Cancel();
            try { await ServerService.StopEmbeddingAsync(_config, LogService.Write); }
            catch (Exception ex) { ShowError("停止嵌入服务失败", ex); }
            return;
        }
        _statusVersion++;
        try
        {
            using var running = ServerService.GetEmbeddingProcess(_config);
            if (running != null) await ServerService.StopEmbeddingAsync(_config, LogService.Write);
            else
            {
                _embeddingStarting = true;
                _embeddingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                EmbeddingToggleButton.Content = "终止启动";
                await ServerService.StartEmbeddingAsync(_config, LogService.Write, _embeddingCancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("嵌入服务失败", ex); }
        finally
        {
            _embeddingCancellation?.Dispose(); _embeddingCancellation = null;
            _embeddingStarting = false; _statusVersion++;
            await RefreshStatusAsync();
        }
    }

    private async void AutostartButton_Click(object sender, RoutedEventArgs e)
    {
        AutostartButton.IsEnabled = false;
        try
        {
            SaveCurrentConfig();
            if ((await AutostartService.GetStatusAsync()).Enabled) await AutostartService.DisableAsync(_config);
            else await AutostartService.EnableAsync(_config);
        }
        catch (Exception ex) { ShowError("设置自启动失败", ex); }
        finally { AutostartButton.IsEnabled = true; await RefreshStatusAsync(); }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogTextBox.Text = "";

    private void OpenLogFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = ConfigService.GetMainStderrPath();
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("打开日志失败", ex); }
    }
}
