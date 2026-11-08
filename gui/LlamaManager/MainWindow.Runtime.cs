using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using LlamaManager.Engines;
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
            // The log page must never wait on the network probes below: a slow or failing
            // health check used to freeze it for as long as the failure lasted.
            RefreshLog();
            var backend = SelectedEngineBackend();
            var backendId = backend?.Descriptor.Id ?? "llamacpp";
            var isLlamacpp = backendId == "llamacpp";
            var session = SelectedSession();

            using var process = isLlamacpp ? ServerService.GetManagedProcess(_config) : null;
            using var embedding = ServerService.GetEmbeddingProcess(_config);
            var embeddingSession = embedding == null ? null : _config.RunningEmbedding;
            var readyTask = session == null ? Task.FromResult(false) : ServerService.TestApiReadyAsync(session.Port, _lifetime.Token);
            var embeddingReadyTask = embeddingSession == null ? Task.FromResult(false) : ServerService.TestApiReadyAsync(embeddingSession.Port, _lifetime.Token);
            var cudaTask = CudaService.DetectAsync(selectedModel);
            var autostartTask = AutostartService.GetStatusAsync();
            await Task.WhenAll(readyTask, embeddingReadyTask, cudaTask, autostartTask);
            var status = readyTask.Result && session != null
                ? await _speedMonitor.QuerySpeedAsync(session, backendId, _lifetime.Token)
                : new SpeedStatus("等待模型就绪…", SpeedKind.Idle);
            if (_lifetime.IsCancellationRequested || version != _statusVersion) return;
            // External CLI start/stop can change identity while HTTP checks are in flight.
            var latest = ConfigService.ReadConfig();
            if (latest.GetRunning(SlotFor(backendId)) != _config.GetRunning(SlotFor(backendId))) return;

            // A recorded session whose process is gone must not present itself as running; the
            // API check still wins, so an externally relaunched service keeps being shown.
            var alive = session == null || (isLlamacpp ? process?.HasExited == false : ServerService.IsSessionAlive(session));
            var running = session != null && (alive || readyTask.Result);
            var ready = running && readyTask.Result;
            var busy = _serverStarting || _serverStopping;
            var brush = (Brush)FindResource(ready ? "SystemFillColorSuccessBrush" : running || busy ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush");
            StatusDot.Fill = brush;
            StatusText.Text = _serverStopping ? "正在停止…" : running ? ready ? "服务运行中" : "模型加载中…" : busy ? "正在启动…" : "已停止";
            ActionStatusText.Text = session != null && running
                ? $"{Path.GetFileName(session.ModelPath)} · PID {session.ProcessId}"
                : $"选择模型后启动 {backend?.Descriptor.DisplayName ?? "服务"}";
            ActionStatusText.Foreground = (Brush)FindResource(ready ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
            ActionEndpointText.Text = session != null && running ? $"http://127.0.0.1:{session.Port}" : "未监听";
            ApiStatusBadge.Text = ready ? "API: 就绪" : running ? "API: 加载中" : "API: 离线";
            ApiStatusBadge.Foreground = brush;

            StartButton.Content = _serverStarting ? "正在启动…" : running ? "重启服务" : "启动服务";
            StopButton.Content = _serverStarting ? "终止启动" : "停止服务";
            StartButton.IsEnabled = !busy && !_selecting && !_scanning;
            StopButton.IsEnabled = !_serverStopping && (running || _serverStarting);
            OpenButton.IsEnabled = ready && !busy;
            SpeedTestButton.IsEnabled = (_speedTesting || ready && !busy);
            SpeedTestButton.ToolTip = "实测推理速度：首Token延迟(TTFT) 与解码速度(tok/s)";
            LiveSpeedText.Text = running ? status.Text : "";
            LiveSpeedBadge.Visibility = running && status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            LiveSpeedText.Foreground = (Brush)FindResource(status.IsActive ? "AccentFillColorDefaultBrush" : "SystemFillColorSuccessBrush");
            // The service's own telemetry (expert cache, VRAM, GPU load) beats a one-off parse of its
            // startup log, and is the only live source on PolyStrata.
            // Only a recorded session has logs worth reading; with no service this used to read the
            // stdout/stderr files on every poll for nothing, under the shared log lock.
            var gpuSummary = session == null ? ""
                : status.Detail.Length > 0 ? status.Detail : LogService.GetGpuSummary(session, backendId);
            ProcessInfoText.Text = running
                ? $"{Path.GetFileName(Path.GetDirectoryName(session!.EnginePath))} · PID {session.ProcessId}" +
                  (!string.IsNullOrEmpty(gpuSummary) ? $" · {gpuSummary}" : "") : "进程: 无";

            // PolyStrata writes no per-request log line, so the finished request the telemetry just
            // reported is written into the manager log once, on the poll that first sees it.
            if (status.Sample is { } sample && session != null)
            {
                if (_loggedSpeedSession != session) { _loggedSpeedSession = session; _loggedSpeedStamp = 0; }
                if (_loggedSpeedStamp == 0) _loggedSpeedStamp = sample.Stamp;
                else if (sample.Stamp != _loggedSpeedStamp)
                {
                    _loggedSpeedStamp = sample.Stamp;
                    var drafts = sample.DraftAcceptance is { } acceptance
                        ? $" · 草稿接受 {sample.DraftsAccepted}/{sample.DraftsOffered}（{acceptance:P0}）"
                        : "";
                    LogService.Write($"[{backendId}] 请求完成：{sample.Tokens:N0} tokens · {sample.Rate:F2} tok/s · {sample.Seconds:F1} s{drafts}");
                    RefreshLog();
                }
            }
            var cuda = cudaTask.Result;
            CudaText.Text = cuda.Available ? "CUDA: 可用（所选引擎）" : "CUDA: 未检测到";
            CudaText.ToolTip = cuda.Text + "\n设备内存为检测时快照；点击刷新可重新检测。";
            PortText.Text = session != null && running ? $":{session.Port}" : $":{ResolveConfiguredPort(backend)}";
            EngineBaseUrlTextBox.Text = running ? $"http://127.0.0.1:{session!.Port}/v1" : "";

            // Embedding is llama.cpp-only.
            EmbeddingStatusText.Text = embeddingSession == null ? "未启动" :
                $"{(embeddingReadyTask.Result ? "就绪" : "加载中")} · PID {embeddingSession.ProcessId} · :{embeddingSession.Port}";
            EmbeddingStatusDot.Fill = (Brush)FindResource(embeddingSession == null ? "TextFillColorSecondaryBrush" : embeddingReadyTask.Result ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
            EmbeddingToggleButton.Content = _embeddingStarting ? "终止启动" : embeddingSession == null ? "启动服务" : "停止服务";
            EmbeddingToggleButton.IsEnabled = !_embeddingStarting && isLlamacpp;
            if (_updateCount == 0)
            {
                EmbeddingPortBox.Text = embeddingSession?.Port > 0 ? embeddingSession.Port.ToString() : _config.EmbeddingPort.ToString();
                EmbeddingModelBox.Text = _config.EmbeddingModelPath;
            }

            AutostartStatusText.Text = autostartTask.Result.Text;
            AutostartButton.Content = autostartTask.Result.Enabled ? "关闭自启" : "开启自启";
            // The log page refreshes on its own when selected; rebuilding it twice per poll for a
            // hidden tab was wasted work. The early call above covers the visible case.
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Write("状态检测失败：" + ex.Message); }
        finally { _refreshing = false; }
    }

    private int ResolveConfiguredPort(IEngineBackend? backend)
    {
        if (backend is IServerBackend server)
        {
            try { return server.ResolvePort(new EngineStartRequest { Config = _config }); }
            catch { }
        }
        return _config.Port;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_serverStarting || _serverStopping || _selecting || _scanning || _engineMaintenanceCancellation != null) return;
        var backend = SelectedEngineBackend();
        if (backend is not IServerBackend server) return;

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
                var otherSlot = SlotFor(conflict);
                var otherSession = _config.GetRunning(otherSlot);
                await other.StopAsync(otherSession != null ? LlamaCppBackend.ToEngineSession(otherSession) : new EngineSession { BackendId = conflict },
                    new DelegateLogSink(LogService.Write));
                _config = ConfigService.ReadConfig();
            }
            catch (Exception ex) { ShowError("停止冲突引擎失败", ex); return; }
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
            // One-click environment configuration and start:
            // If the backend probe reports unavailable, offer to automatically bootstrap first!
            var probe = await backend.ProbeAsync(_startCancellation.Token);
            if (!probe.Available)
            {
                if (BootstrapService.CanDownload(backend.Descriptor.Id))
                {
                    var decision = MessageBox.Show(
                        $"{backend.Descriptor.DisplayName} 尚未配置运行环境：\n{probe.Problem}\n\n是否立即一键自动下载并配置环境，并在完成后自动启动服务？",
                        "一键配置环境并启动", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                    if (decision != MessageBoxResult.OK) return;

                    LogService.Write($"=== 开始为 {backend.Descriptor.DisplayName} 一键配置环境 ===");
                    ConfigurationTabs.SelectedIndex = ConfigurationTabs.Items.Count - 1; // 切换到日志页
                    ActionStatusText.Text = "正在一键配置环境…";

                    var steps = await BootstrapService.RunAsync(backend.Descriptor.Id,
                        new DelegateLogSink(msg => { LogService.Write(msg); AppendEngineOutput(msg); }),
                        _startCancellation.Token);

                    if (steps.Any(s => !s.Ok))
                    {
                        var failed = string.Join("; ", steps.Where(s => !s.Ok).Select(s => $"{s.Name}: {s.Detail}"));
                        throw new InvalidOperationException("环境配置未完全成功：" + failed);
                    }
                    LogService.Write($"=== {backend.Descriptor.DisplayName} 环境配置完成，正在自动启动服务 ===");
                    await RefreshEngineAsync();
                }
                else
                {
                    throw new InvalidOperationException($"当前引擎不可用：{probe.Problem}");
                }
            }

            if (backend.Descriptor.Id == "llamacpp")
            {
                SaveCurrentConfig();
                var launch = new ManagerConfig { ModelPath = _config.ModelPath, Args = _config.Args, Port = _config.Port };
                var open = _config.AutoOpenBrowser;
                using var running = ServerService.GetManagedProcess(_config);
                if (running != null) await MainBackend.StopAsync(LlamaCppBackend.ToEngineSession(_config.RunningServer!), new DelegateLogSink(LogService.Write));
                _startCancellation.Token.ThrowIfCancellationRequested();
                await MainBackend.StartAsync(new EngineStartRequest { Config = launch, OpenBrowser = open },
                    new DelegateLogSink(LogService.Write), _startCancellation.Token);
            }
            else
            {
                if (!SaveEngineParameters(backend))
                {
                    ShowParameterValidationFailure();
                    return;
                }
                _config = ConfigService.ReadConfig();
                await server.StartAsync(new EngineStartRequest { Config = _config },
                    new DelegateLogSink(msg => { LogService.Write(msg); AppendEngineOutput(msg); }), _startCancellation.Token);
                _config = ConfigService.ReadConfig();
            }
        }
        catch (OperationCanceledException) { LogService.Write("已结束此次启动等待。"); }
        catch (Exception ex) { ShowError("启动失败", ex); }
        finally
        {
            _startCancellation.Dispose(); _startCancellation = null;
            _serverStarting = false; _statusVersion++;
            await RefreshEngineAsync();
            await RefreshStatusAsync();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_serverStopping) return;
        var backend = SelectedEngineBackend();
        if (backend is not IServerBackend server) return;
        _serverStopping = true;
        _statusVersion++;
        StopButton.IsEnabled = false;
        _startCancellation?.Cancel();
        _speedCancellation?.Cancel();
        try
        {
            var session = _config.GetRunning(SlotFor(backend.Descriptor.Id));
            var target = session != null ? LlamaCppBackend.ToEngineSession(session) : new EngineSession { BackendId = backend.Descriptor.Id };
            await server.StopAsync(target, new DelegateLogSink(msg => { LogService.Write(msg); AppendEngineOutput(msg); }));
            _config = ConfigService.ReadConfig();
        }
        catch (Exception ex) { ShowError("停止失败", ex); }
        finally { _serverStopping = false; _statusVersion++; await RefreshEngineAsync(); await RefreshStatusAsync(); }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var backend = SelectedEngineBackend();
            if (backend is not IServerBackend server) return;
            var session = SelectedSession();
            if (session == null) return;
            if (!await ServerService.TestApiReadyAsync(session.Port, _lifetime.Token)) return;
            if (backend is StrataBackend strata && !strata.HasWebUi(_config))
            {
                // Do not open a page the installed frontend cannot serve; "/props" is JSON.
                LogService.Write($"{backend.Descriptor.DisplayName} 完整包未附带 serve/web 网页界面；未打开浏览器。");
                MessageBox.Show(
                    $"当前 {backend.Descriptor.DisplayName} 完整包没有网页聊天界面（缺少 serve/web/index.html），根路径会报错。\n\n" +
                    $"请使用 OpenAI 兼容接口：http://127.0.0.1:{session.Port}/v1\n" +
                    "或在启动器日志中查看服务输出；拉取包含网页资源的引擎版本后再试。",
                    "没有网页界面", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            server.OpenWebUi(LlamaCppBackend.ToEngineSession(session));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("打开页面失败", ex); }
    }

    private async void SpeedTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_speedTesting) { _speedCancellation?.Cancel(); return; }
        try
        {
            var session = SelectedSession();
            if (session == null) return;
            _speedTesting = true;
            _speedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _speedCancellation.CancelAfter(TimeSpan.FromMinutes(10));
            SpeedTestButton.Content = "取消测速";
            var result = await SpeedTestService.RunAsync(session.Port, _speedCancellation.Token);
            if (_speedCancellation.IsCancellationRequested || ConfigService.ReadConfig().GetRunning(SlotFor(SelectedEngineBackend()!.Descriptor.Id)) != session) return;
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
            try { await MainBackend.StopAsync(new EngineSession { BackendId = "llamacpp", Extra = { ["embedding"] = "1" } }, new DelegateLogSink(LogService.Write)); }
            catch (Exception ex) { ShowError("停止嵌入服务失败", ex); }
            return;
        }
        _statusVersion++;
        try
        {
            using var running = ServerService.GetEmbeddingProcess(_config);
            if (running != null) await MainBackend.StopAsync(new EngineSession { BackendId = "llamacpp", Extra = { ["embedding"] = "1" } }, new DelegateLogSink(LogService.Write));
            else
            {
                _embeddingStarting = true;
                _embeddingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                EmbeddingToggleButton.Content = "终止启动";
                await MainBackend.StartAsync(new EngineStartRequest { Config = _config, Embedding = true },
                    new DelegateLogSink(LogService.Write), _embeddingCancellation.Token);
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

    private void EmbeddingPortBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _updateCount > 0) return;
        if (int.TryParse(EmbeddingPortBox.Text.Trim(), out var port) && port is >= 1 and <= 65535 && port != _config.EmbeddingPort)
        {
            _config.EmbeddingPort = port;
            ConfigService.SaveConfig(_config);
            _statusVersion++;
            _ = RefreshStatusAsync();
        }
    }

    private void EmbeddingModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _updateCount > 0) return;
        var path = EmbeddingModelBox.Text.Trim();
        if (path != _config.EmbeddingModelPath)
        {
            _config.EmbeddingModelPath = path;
            ConfigService.SaveConfig(_config);
        }
    }

    private void EmbeddingModelBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择嵌入模型",
            Filter = "GGUF 模型|*.gguf|所有文件|*.*",
            InitialDirectory = string.IsNullOrWhiteSpace(_config.ModelsDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : _config.ModelsDirectory,
        };
        if (dialog.ShowDialog() != true) return;
        EmbeddingModelBox.Text = dialog.FileName;
        _config.EmbeddingModelPath = dialog.FileName;
        ConfigService.SaveConfig(_config);
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

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Text = "";
        // Otherwise the unchanged file content compares equal and the page stays blank.
        _lastLogText = null;
    }

    private void OpenLogFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var backendId = SelectedEngineBackend()?.Descriptor.Id ?? _config.ActiveBackendId;
            var path = LogService.GetPrimaryLogPath(backendId);
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("打开日志失败", ex); }
    }
}
