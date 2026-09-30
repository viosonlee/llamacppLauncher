using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager;

public partial class MainWindow : Window
{
    private ManagerConfig _config = new();
    private DispatcherTimer? _statusTimer;
    private List<ModelInfo> _models = [];
    private bool _loaded;
    private bool _parsingArgs;
    private int _updateCount;
    private bool _embeddingStarting;
    private bool _serverStarting;
    private string? _lastLogText;

    public MainWindow()
    {
        InitializeComponent();
        PopulateCpuCores();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void PopulateCpuCores()
    {
        var maxCores = Environment.ProcessorCount;
        for (int i = 1; i <= maxCores; i++)
            CpuCoresBox.Items.Add(i.ToString());
        CpuCoresBox.SelectedIndex = Math.Min(maxCores - 1, 7); // min(8, max)
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _config = ConfigService.ReadConfig();
            RefreshModels();

            // Restore last-used model's preset
            if (!string.IsNullOrEmpty(_config.ModelPath))
            {
                var preset = ConfigService.GetModelPreset(_config, _config.ModelPath);
                if (preset != null)
                {
                    _config.Args = preset.Args;
                    _config.Port = preset.Port;
                }
            }

            RefreshStatus();
            RefreshLog();
            ApplyConfig();
            AutoOpenBrowserCheckBox.IsChecked = _config.AutoOpenBrowser;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
        _loaded = true;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try { SaveCurrentConfig(); } catch { }
    }

    private void RefreshModels()
    {
        var modelsDir = string.IsNullOrEmpty(_config.ModelsDirectory) ? Path.Combine(ConfigService.GetRoot(), "models") : _config.ModelsDirectory;
        _models.Clear();

        if (Directory.Exists(modelsDir))
        {
            try
            {
                var ggufFiles = Directory.GetFiles(modelsDir, "*.gguf", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith("mmproj-", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f)
                    .Select(f =>
                    {
                        var info = new FileInfo(f);
                        string relPath = f;
                        try
                        {
                            relPath = Path.GetRelativePath(modelsDir, f);
                        }
                        catch { }

                        return new ModelInfo
                        {
                            FullName = Path.GetFullPath(f),
                            FileName = info.Name,
                            RelativePath = relPath,
                            SizeBytes = info.Length
                        };
                    })
                    .ToList();

                _models.AddRange(ggufFiles);
            }
            catch (Exception ex)
            {
                LogService.Write($"扫描模型目录失败: {ex.Message}");
            }
        }
        else
        {
            LogService.Write($"模型目录不存在: {modelsDir}");
        }

        ModelListBox.ItemsSource = null;
        ModelListBox.ItemsSource = _models;

        if (ModelCountText != null)
            ModelCountText.Text = $"共 {_models.Count} 个模型";

        if (!string.IsNullOrEmpty(_config.ModelPath))
        {
            var idx = _models.FindIndex(m =>
                string.Equals(m.FullName, _config.ModelPath, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                ModelListBox.SelectedIndex = idx;
        }
    }

    private void RefreshStatus()
    {
        try
        {
            if (_serverStarting) return;

            var running = ServerService.GetManagedProcess(_config);
            running ??= ServerService.FindMainProcess(_config);
            if (running != null)
            {
                var port = ServerService.ParsePortFromArgs(_config.Args, _config.Port);
                _ = Task.Run(async () =>
                {
                    var isReady = ServerService.TestApiReady(port);
                    var speedStr = isReady ? await SpeedMonitorService.QuerySpeedAsync(port) : "";
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (_serverStarting) return;

                        if (isReady)
                        {
                            StatusDot.Fill = (Brush)FindResource("SuccessBrush");
                            StatusText.Text = $"服务运行中 (PID {running.Id})";

                            ActionStatusDot.Fill = (Brush)FindResource("SuccessBrush");
                            ActionStatusText.Text = $"服务已就绪！API 接口正常响应 (PID {running.Id})";
                            ActionStatusText.Foreground = (Brush)FindResource("TextPrimaryBrush");
                            ActionEndpointText.Text = $"http://127.0.0.1:{port}";
                            ApiStatusBadge.Text = "API: 在线就绪";
                            ApiStatusBadge.Foreground = (Brush)FindResource("SuccessBrush");

                            StartButton.Content = "🔄 重启服务";
                            StartButton.IsEnabled = true;
                            StopButton.Content = "■ 停止服务";
                            StopButton.IsEnabled = true;
                            OpenButton.IsEnabled = true;
                        }
                        else
                        {
                            StatusDot.Fill = (Brush)FindResource("WarningBrush");
                            StatusText.Text = $"正在加载模型…（PID {running.Id}）";

                            ActionStatusDot.Fill = (Brush)FindResource("WarningBrush");
                            ActionStatusText.Text = $"模型加载与显存分配中 (PID {running.Id})，请稍候...";
                            ActionStatusText.Foreground = (Brush)FindResource("WarningBrush");
                            ActionEndpointText.Text = $":{port} (就绪中)";
                            ApiStatusBadge.Text = "API: 加载中...";
                            ApiStatusBadge.Foreground = (Brush)FindResource("WarningBrush");

                            StartButton.Content = "⏳ 正在加载模型...";
                            StartButton.IsEnabled = false;
                            StopButton.Content = "■ 终止启动";
                            StopButton.IsEnabled = true;
                            OpenButton.IsEnabled = false;
                        }

                        if (LiveSpeedText != null)
                        {
                            LiveSpeedText.Text = speedStr;
                            LiveSpeedText.Foreground = speedStr.Contains("生成中") || speedStr.Contains("读取")
                                ? (Brush)FindResource("AccentBrush")
                                : (Brush)FindResource("SuccessBrush");
                        }
                        if (ProcessInfoText != null)
                        {
                            var extra = string.IsNullOrEmpty(speedStr) ? "" : $" | {speedStr}";
                            var isPrism = ConfigService.IsPrismModel(_config.ModelPath) && ConfigService.HasPrismServer();
                            var engineLabel = isPrism ? " [Prism引擎]" : " [官方引擎]";
                            ProcessInfoText.Text = $"进程: PID {running.Id}{engineLabel} | 端口 {port}{extra}";
                        }
                    });
                });
            }
            else
            {
                StatusDot.Fill = (Brush)FindResource("ErrorBrush");
                StatusText.Text = "已停止";

                ActionStatusDot.Fill = (Brush)FindResource("ErrorBrush");
                ActionStatusText.Text = "服务已停止，点击【启动服务】开始运行";
                ActionStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
                ActionEndpointText.Text = "未监听";
                ApiStatusBadge.Text = "API: 离线";
                ApiStatusBadge.Foreground = (Brush)FindResource("TextSecondaryBrush");

                StartButton.Content = "▶ 启动服务";
                StartButton.IsEnabled = true;
                StopButton.Content = "■ 停止服务";
                StopButton.IsEnabled = false;
                OpenButton.IsEnabled = false;

                if (LiveSpeedText != null) LiveSpeedText.Text = "";
                ProcessInfoText.Text = "进程: 无";
            }
        }
        catch { }

        try
        {
            var emb = ServerService.GetEmbeddingProcess(_config);
            emb ??= ServerService.FindEmbeddingProcess(_config);
            if (emb != null)
            {
                EmbeddingStatusDot.Fill = new SolidColorBrush(Colors.Green);
                EmbeddingStatusText.Text = $"嵌入服务: 运行中 (PID {emb.Id})";
                EmbeddingToggleButton.Content = "停止";
            }
            else
            {
                EmbeddingStatusDot.Fill = new SolidColorBrush(Colors.Gray);
                EmbeddingStatusText.Text = "嵌入服务: 未启动";
                EmbeddingToggleButton.Content = "启动";
            }
            EmbeddingToggleButton.IsEnabled = !_embeddingStarting;
        }
        catch { }

        try
        {
            Task.Run(() =>
            {
                var cuda = CudaService.Detect();
                if (_loaded)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            CudaText.Text = cuda.Available ? "CUDA: 可用" : "CUDA: 不可用";
                            CudaText.Foreground = cuda.Available
                                ? (Brush)FindResource("SuccessBrush")
                                : (Brush)FindResource("TextSecondaryBrush");
                        }
                        catch { }
                    });
                }
            });
        }
        catch { }

        try { PortText.Text = $":{_config.Port}"; } catch { }
        try { AutostartStatusText.Text = AutostartService.GetStatusText(); } catch { }
        try { AutostartButton.Content = AutostartService.IsPrimaryEnabled() ? "关闭自启动" : "开启自启动"; } catch { }
    }

    private void RefreshLog()
    {
        try
        {
            var lines = LogService.ReadRecent(500);
            var text = string.Join("\n", lines);
            if (text == _lastLogText) return;
            _lastLogText = text;
            LogTextBox.Text = text;
            LogScrollViewer.ScrollToEnd();
        }
        catch { }
    }

    private void ApplyConfig()
    {
        _updateCount++;
        try
        {
            ArgsTextBox.Text = _config.Args;
            PortTextBox.Text = _config.Port.ToString();
            ModelsDirTextBox.Text = string.IsNullOrEmpty(_config.ModelsDirectory) 
                ? Path.Combine(ConfigService.GetRoot(), "models") 
                : _config.ModelsDirectory;
            if (AutoOpenBrowserCheckBox != null)
                AutoOpenBrowserCheckBox.IsChecked = _config.AutoOpenBrowser;
            ParseArgsToControls();
            UpdateSelectedModelText();
        }
        finally { _updateCount--; }
    }

    private void AutoOpenBrowserCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.AutoOpenBrowser = AutoOpenBrowserCheckBox.IsChecked == true;
        SaveCurrentConfig();
    }

    private void ModelsDirButton_Click(object sender, RoutedEventArgs e)
    {
        var initialDir = string.IsNullOrEmpty(_config.ModelsDirectory) ? Path.Combine(ConfigService.GetRoot(), "models") : _config.ModelsDirectory;
        if (!Directory.Exists(initialDir)) initialDir = ConfigService.GetRoot();

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择模型文件夹（将自动递归扫描其所有子目录下的 GGUF 模型）",
            InitialDirectory = initialDir
        };

        if (dialog.ShowDialog() == true)
        {
            _config.ModelsDirectory = Path.GetFullPath(dialog.FolderName);
            ModelsDirTextBox.Text = _config.ModelsDirectory;
            SaveCurrentConfig();
            RefreshModels();
            LogService.Write($"已更新模型目录: {_config.ModelsDirectory}，共扫描到 {_models.Count} 个模型。");
            RefreshLog();
        }
    }

    private void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshModels();
        LogService.Write($"已重新扫描模型目录，共扫描到 {_models.Count} 个模型。");
        RefreshLog();
    }

    private void ModelsDirTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            ApplyModelsDirectoryChange();
        }
    }

    private void ModelsDirTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyModelsDirectoryChange();
    }

    private void ApplyModelsDirectoryChange()
    {
        var inputDir = ModelsDirTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(inputDir)) return;

        if (!string.Equals(inputDir, _config.ModelsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(inputDir))
            {
                _config.ModelsDirectory = Path.GetFullPath(inputDir);
                SaveCurrentConfig();
                RefreshModels();
                LogService.Write($"已更新模型目录: {_config.ModelsDirectory}，共扫描到 {_models.Count} 个模型。");
                RefreshLog();
            }
            else
            {
                LogService.Write($"指定目录不存在: {inputDir}");
                RefreshLog();
            }
        }
    }

    private void ParseArgsToControls()
    {
        var args = ArgsTextBox.Text;

        // Context Length: -c NNNN
        var m = Regex.Match(args, @"(?:^|\s)-c\s+(\d+)");
        ContextLengthBox.Text = m.Success ? m.Groups[1].Value : "8192";

        // Context Quant: -ctk q8_0 / -ctv q4_0 (K、V 可独立配置；缺省 -ctv 时沿用 -ctk)
        m = Regex.Match(args, @"(?:^|\s)-ctk\s+(\S+)");
        var ctk = m.Success ? m.Groups[1].Value : null;
        var m2 = Regex.Match(args, @"(?:^|\s)-ctv\s+(\S+)");
        var ctv = m2.Success ? m2.Groups[1].Value : ctk;
        SetContextQuantBox(ContextQuantKBox, ctk);
        SetContextQuantBox(ContextQuantVBox, ctv);

        // Offload Layers: -ngl NNN/all/auto
        m = Regex.Match(args, @"(?:^|\s)-ngl\s+(\S+)");
        if (m.Success)
        {
            var val = m.Groups[1].Value;
            var found = false;
            for (int i = 0; i < OffloadLayersBox.Items.Count; i++)
            {
                if (OffloadLayersBox.Items[i] is ComboBoxItem item && string.Equals(item.Content?.ToString(), val, StringComparison.Ordinal))
                { OffloadLayersBox.SelectedIndex = i; found = true; break; }
            }
            if (!found) { OffloadLayersBox.Text = val; }
        }
        else OffloadLayersBox.Text = "all";

        // CPU Cores: -t N
        m = Regex.Match(args, @"(?:^|\s)-t\s+(\d+)");
        if (m.Success)
        {
            var val = m.Groups[1].Value;
            for (int i = 0; i < CpuCoresBox.Items.Count; i++)
            {
                if ((string)CpuCoresBox.Items[i] == val)
                { CpuCoresBox.SelectedIndex = i; break; }
            }
        }

        // Reasoning: --reasoning on/off
        m = Regex.Match(args, @"(?:^|\s)--reasoning\s+(\S+)");
        if (m.Success && m.Groups[1].Value == "on")
            ReasoningBox.SelectedIndex = 1;
        else
            ReasoningBox.SelectedIndex = 0;

        // Reasoning Budget: --reasoning-budget N
        m = Regex.Match(args, @"(?:^|\s)--reasoning-budget\s+(-?\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var bVal) && bVal > 0)
        {
            var found = false;
            for (int i = 0; i < ReasoningBudgetBox.Items.Count; i++)
            {
                if (ReasoningBudgetBox.Items[i] is ComboBoxItem item)
                {
                    var itemText = item.Content?.ToString() ?? "";
                    if (itemText == bVal.ToString() || itemText.StartsWith($"{bVal} "))
                    {
                        ReasoningBudgetBox.SelectedIndex = i;
                        found = true;
                        break;
                    }
                }
            }
            if (!found) { ReasoningBudgetBox.Text = bVal.ToString(); }
        }
        else
        {
            ReasoningBudgetBox.SelectedIndex = 0;
        }

        // Flash Attention: -fa / --flash-attn
        m = Regex.Match(args, @"(?:^|\s)(?:-fa|--flash-attn)(?:\s+(on|1))?");
        FlashAttentionBox.SelectedIndex = m.Success ? 1 : 0;

        // Batch Size: -b N
        m = Regex.Match(args, @"(?:^|\s)-b\s+(\d+)");
        if (m.Success)
        {
            var val = m.Groups[1].Value;
            var found = false;
            for (int i = 0; i < BatchSizeBox.Items.Count; i++)
            {
                if (BatchSizeBox.Items[i] is ComboBoxItem item && string.Equals(item.Content?.ToString(), val, StringComparison.Ordinal))
                { BatchSizeBox.SelectedIndex = i; found = true; break; }
            }
            if (!found) { BatchSizeBox.Text = val; }
        }
        else BatchSizeBox.Text = "2048";

        // UBatch Size: -ub N
        m = Regex.Match(args, @"(?:^|\s)-ub\s+(\d+)");
        if (m.Success)
        {
            var val = m.Groups[1].Value;
            var found = false;
            for (int i = 0; i < UBatchSizeBox.Items.Count; i++)
            {
                if (UBatchSizeBox.Items[i] is ComboBoxItem item && string.Equals(item.Content?.ToString(), val, StringComparison.Ordinal))
                { UBatchSizeBox.SelectedIndex = i; found = true; break; }
            }
            if (!found) { UBatchSizeBox.Text = val; }
        }
        else UBatchSizeBox.Text = "512";

        // MoE Offload: --n-cpu-moe N
        m = Regex.Match(args, @"(?:^|\s)--n-cpu-moe\s+(\d+)");
        if (m.Success)
        {
            var val = m.Groups[1].Value;
            var found = false;
            for (int i = 0; i < MoeOffloadBox.Items.Count; i++)
            {
                if (MoeOffloadBox.Items[i] is ComboBoxItem item)
                {
                    var content = item.Content?.ToString() ?? "";
                    if (content == val || content.StartsWith(val + " "))
                    {
                        MoeOffloadBox.SelectedIndex = i;
                        found = true;
                        break;
                    }
                }
            }
            if (!found) { MoeOffloadBox.Text = val; }
        }
        else MoeOffloadBox.SelectedIndex = 0;

        // CPU MoE toggle: --cpu-moe
        CpuMoeCheckbox.IsChecked = Regex.IsMatch(args, @"(?:^|\s)--cpu-moe\b");

        // Verbose Logging: -v / --verbose / --log-verbose
        VerboseLogBox.SelectedIndex = Regex.IsMatch(args, @"(?:^|\s)(?:-v|--verbose|--log-verbose)\b") ? 1 : 0;
    }

    private void UpdateSelectedModelText()
    {
        if (string.IsNullOrEmpty(_config.ModelPath))
        {
            SelectedModelText.Text = "未选择模型";
            ModelInfoText.Text = "";
            MoeSection.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var fileName = Path.GetFileName(_config.ModelPath);
            if (File.Exists(_config.ModelPath))
            {
                var fi = new FileInfo(_config.ModelPath);
                var sizeStr = fi.Length >= 1L << 30 ? $"{fi.Length / (double)(1L << 30):N2} GB" : $"{fi.Length / (double)(1L << 20):N1} MB";
                SelectedModelText.Text = $"{fileName}  ({sizeStr})";

                var info = new ModelInfo
                {
                    FullName = _config.ModelPath,
                    FileName = fileName,
                    SizeBytes = fi.Length
                };
                var category = info.Category;
                var type = info.ModelTypeDisplay;
                var ctx = info.RecommendedContext;
                var batch = info.RecommendedBatch;
                var offload = info.CanFullOffload ? "all" : "auto";

                MoeSection.Visibility = info.IsMoE ? Visibility.Visible : Visibility.Collapsed;

                var extra = info.IsMoE ? "  ·  Flash Attn" : "";
                ModelInfoText.Text = $"{type}  ·  {category}  ·  -c {ctx}  ·  -b {batch}  ·  -ngl {offload}{extra}";
            }
            else
            {
                SelectedModelText.Text = $"{fileName}  (文件不存在)";
                ModelInfoText.Text = "";
                MoeSection.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            SelectedModelText.Text = _config.ModelPath;
            ModelInfoText.Text = "";
            MoeSection.Visibility = Visibility.Collapsed;
        }
    }

    private void ContextLength_TextChanged(object sender, TextChangedEventArgs e) => SyncArgsFromControls();
    private void ContextQuant_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncArgsFromControls();
    private void OffloadLayers_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncArgsFromControls();
    private void OffloadLayers_LostFocus(object sender, RoutedEventArgs e) => SyncArgsFromControls();
    private void CpuCores_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncArgsFromControls();
    private void Reasoning_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncArgsFromControls();
    private void FlashAttention_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncArgsFromControls();
    private void BatchSize_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncArgsFromControls();
    private void BatchSize_LostFocus(object sender, RoutedEventArgs e) => SyncArgsFromControls();
    private void UBatchSize_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncArgsFromControls();
    private void UBatchSize_LostFocus(object sender, RoutedEventArgs e) => SyncArgsFromControls();
    private void MoeOffload_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncArgsFromControls();
    private void MoeOffload_LostFocus(object sender, RoutedEventArgs e) => SyncArgsFromControls();
    private void CpuMoeCheckbox_Changed(object sender, RoutedEventArgs e) => SyncArgsFromControls();
    private void VerboseLog_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncArgsFromControls();
    private void ReasoningBudget_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncArgsFromControls();
    private void ReasoningBudget_LostFocus(object sender, RoutedEventArgs e) => SyncArgsFromControls();

    private void SyncArgsFromControls()
    {
        if (_updateCount > 0 || !_loaded) return;
        if (ArgsTextBox == null) return;
        _parsingArgs = true;
        try
        {
            var args = ArgsTextBox.Text;
            if (int.TryParse(ContextLengthBox.Text, out var ctx) && ctx > 0)
                args = UpdateArgInString(args, "-c", ctx.ToString());

            args = ApplyContextQuant(args, "-ctk", GetContextQuant(ContextQuantKBox));
            args = ApplyContextQuant(args, "-ctv", GetContextQuant(ContextQuantVBox));

            args = UpdateArgInString(args, "-ngl", OffloadLayersBox.Text);
            var cores = CpuCoresBox.Text;
            if (int.TryParse(cores, out _) || cores == "all" || cores == "auto")
            {
                args = UpdateArgInString(args, "-t", cores);
                args = UpdateArgInString(args, "-tb", cores);
            }
            args = UpdateArgInString(args, "--reasoning", ReasoningBox.SelectedIndex == 1 ? "on" : "off");

            if (FlashAttentionBox.SelectedIndex == 1)
            {
                if (!Regex.IsMatch(args, @"(?:^|\s)(?:-fa|--flash-attn)\b"))
                    args = (args.TrimEnd() + " -fa on").TrimStart();
                else if (Regex.IsMatch(args, @"(?:^|\s)(?:-fa|--flash-attn)\s*$"))
                    args = args + " on"; // Fix missing value
            }
            else
            {
                args = Regex.Replace(args, @"(?:^|\s)(?:-fa|--flash-attn)(?:\s+(?:on|off|auto|1|0))?\b", "").Trim();
                args = Regex.Replace(args, @"\s+", " ").Trim();
            }

            if (int.TryParse(BatchSizeBox.Text, out var bSize) && bSize > 0)
                args = UpdateArgInString(args, "-b", bSize.ToString());
            
            if (int.TryParse(UBatchSizeBox.Text, out var ubSize) && ubSize > 0)
                args = UpdateArgInString(args, "-ub", ubSize.ToString());
            
            var isCurrentModelMoE = !string.IsNullOrEmpty(_config.ModelPath) && Models.ModelInfo.DetectIsMoE(_config.ModelPath);

            if (!isCurrentModelMoE)
            {
                // Dense 模型：彻底清除任何 MoE 参数，杜绝交叉污染
                args = Regex.Replace(args, @"(?:^|\s)--n-cpu-moe\s+\d+", "").Trim();
                args = Regex.Replace(args, @"(?:^|\s)--cpu-moe\b", "").Trim();
                args = Regex.Replace(args, @"\s+", " ").Trim();
            }
            else
            {
                // MoE 模型：处理 MoE 卸载
                var moeText = MoeOffloadBox.Text?.Trim() ?? "0";
                if (moeText.Contains(' ')) moeText = moeText[..moeText.IndexOf(' ')].Trim();
                if (int.TryParse(moeText, out var moeLayers) && moeLayers >= 0)
                {
                    if (moeLayers == 0)
                    {
                        args = Regex.Replace(args, @"(?:^|\s)--n-cpu-moe\s+\d+", "").Trim();
                        args = Regex.Replace(args, @"\s+", " ").Trim();
                    }
                    else
                    {
                        args = UpdateArgInString(args, "--n-cpu-moe", moeLayers.ToString());
                    }
                }

                if (CpuMoeCheckbox.IsChecked == true)
                {
                    if (!Regex.IsMatch(args, @"(?:^|\s)--cpu-moe\b"))
                        args = (args.TrimEnd() + " --cpu-moe").TrimStart();
                }
                else
                {
                    args = Regex.Replace(args, @"(?:^|\s)--cpu-moe\b", "").Trim();
                    args = Regex.Replace(args, @"\s+", " ").Trim();
                }
            }

            if (VerboseLogBox.SelectedIndex == 1)
            {
                if (!Regex.IsMatch(args, @"(?:^|\s)(?:-v|--verbose|--log-verbose)\b"))
                    args = (args.TrimEnd() + " -v").TrimStart();
            }
            else
            {
                args = Regex.Replace(args, @"(?:^|\s)(?:-v|--verbose|--log-verbose)\b", "").Trim();
                args = Regex.Replace(args, @"\s+", " ").Trim();
            }

            // Reasoning Budget: --reasoning-budget N
            var budgetText = ReasoningBudgetBox.Text?.Trim() ?? "";
            if (budgetText.Contains(' '))
                budgetText = budgetText[..budgetText.IndexOf(' ')].Trim();
            if (int.TryParse(budgetText, out var budget) && budget > 0)
            {
                args = UpdateArgInString(args, "--reasoning-budget", budget.ToString());
            }
            else
            {
                args = Regex.Replace(args, @"(?:^|\s)--reasoning-budget\s+-?\d+", "").Trim();
                args = Regex.Replace(args, @"\s+", " ").Trim();
            }

            ArgsTextBox.Text = args;
        }
        finally { _parsingArgs = false; }
    }

    private static string GetContextQuant(ComboBox box)
    {
        if (box == null || box.SelectedIndex <= 0) return "default";
        if (box.SelectedItem is ComboBoxItem item)
        {
            var content = item.Content.ToString()!;
            var idx = content.IndexOf(' ');
            return idx > 0 ? content[..idx] : content;
        }
        return "default";
    }

    private static void SetContextQuantBox(ComboBox box, string? value)
    {
        if (box == null) return;
        if (string.IsNullOrEmpty(value))
        {
            box.SelectedIndex = 0;
            return;
        }
        for (int i = 1; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && item.Content?.ToString() is string content
                && content.StartsWith(value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string ApplyContextQuant(string args, string key, string quant)
    {
        if (string.IsNullOrEmpty(quant) || quant == "default")
        {
            args = Regex.Replace(args, $@"(?:^|\s){Regex.Escape(key)}\s+\S+", "");
            return Regex.Replace(args, @"\s+", " ").Trim();
        }
        return UpdateArgInString(args, key, quant);
    }

    private static string UpdateArgInString(string args, string key, string value)
    {
        var pattern = $@"(?:^|\s){Regex.Escape(key)}\s+\S+";
        var replacement = $" {key} {value}";
        if (Regex.IsMatch(args, pattern))
            return Regex.Replace(args, pattern, replacement);
        else
            return (args.TrimEnd() + replacement).TrimStart();
    }

    private void ArgsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_parsingArgs) return;
        if (!_loaded) return;
        _updateCount++;
        try { ParseArgsToControls(); }
        finally { _updateCount--; }
    }

    private void SaveCurrentConfig()
    {
        if (int.TryParse(PortTextBox.Text, out var port) && port > 0)
            _config.Port = port;
        _config.Args = ArgsTextBox.Text?.Trim() ?? "";
        _config.EmbeddingPort = 8081;
        if (AutoOpenBrowserCheckBox != null)
            _config.AutoOpenBrowser = AutoOpenBrowserCheckBox.IsChecked == true;
        ConfigService.SaveConfig(_config);
    }

    // ─── Model Selection ───

    private void ModelListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelListBox.SelectedItem is not ModelInfo model) return;
        if (!_loaded) return;

        try
        {
            _config.ModelPath = model.FullName;

            // Restore saved preset if exists
            var preset = ConfigService.GetModelPreset(_config, model.FullName);
            if (preset != null)
            {
                _config.Args = preset.Args;
                _config.Port = preset.Port;
                LogService.Write($"已选择模型 {model.FileName}（恢复上次配置）。");
            }
            else
            {
                _config.Args = PresetService.GeneratePresetArgs(model.FullName, _config.Port);
                _config.Port = ServerService.ParsePortFromArgs(_config.Args, _config.Port);
                ConfigService.SaveModelPreset(_config, model.FullName, _config.Args, _config.Port);
                LogService.Write($"已选择模型 {model.FileName}（自动生成预设并保存）。");
            }

            // 先刷新 UI 到新模型的参数，再保存，避免用旧模型残留的文本框值覆盖新模型预设
            ApplyConfig();
            SaveCurrentConfig();
            RefreshLog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"选择模型失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var initialDir = string.IsNullOrEmpty(_config.ModelsDirectory) ? Path.Combine(ConfigService.GetRoot(), "models") : _config.ModelsDirectory;
        if (!Directory.Exists(initialDir)) initialDir = ConfigService.GetRoot();

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 GGUF 模型文件",
            Filter = "GGUF 模型 (*.gguf)|*.gguf|所有文件 (*.*)|*.*",
            InitialDirectory = initialDir
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var newPath = Path.GetFullPath(dialog.FileName);
                _config.ModelPath = newPath;

                var preset = ConfigService.GetModelPreset(_config, newPath);
                if (preset != null)
                {
                    _config.Args = preset.Args;
                    _config.Port = preset.Port;
                    LogService.Write($"已选择自定义模型（恢复上次配置）。");
                }
                else
                {
                    _config.Args = PresetService.GeneratePresetArgs(newPath, _config.Port);
                    _config.Port = ServerService.ParsePortFromArgs(_config.Args, _config.Port);
                    ConfigService.SaveModelPreset(_config, newPath, _config.Args, _config.Port);
                    LogService.Write($"已选择自定义模型（自动生成预设并保存）。");
                }

                if (_models.All(m => m.FullName != _config.ModelPath))
                {
                    var fileInfo = new FileInfo(_config.ModelPath);
                    _models.Add(new ModelInfo
                    {
                        FullName = _config.ModelPath,
                        FileName = fileInfo.Name,
                        SizeBytes = fileInfo.Length
                    });
                    ModelListBox.Items.Refresh();
                    ModelListBox.SelectedItem = _models.Last();
                    return; // SelectionChanged 负责载入新模型参数
                }

                // 先刷新 UI 到新模型参数，再保存，避免旧模型残留值覆盖新模型预设
                ApplyConfig();
                SaveCurrentConfig();
                RefreshLog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"选择模型失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ─── Info Buttons ───

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        var (modelSize, cuda) = GetModelContext();
        var info = GetParameterInfo(tag, modelSize, cuda);
        MessageBox.Show(info.Text, info.Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private (long modelSize, CudaInfo cuda) GetModelContext()
    {
        long size = 0;
        if (!string.IsNullOrEmpty(_config.ModelPath) && File.Exists(_config.ModelPath))
        {
            try { size = new FileInfo(_config.ModelPath).Length; } catch { }
        }
        var cuda = CudaService.Detect();
        return (size, cuda);
    }

    private static (string Title, string Text) GetParameterInfo(string tag, long modelSize, CudaInfo cuda)
    {
        var gb = modelSize / (double)(1L << 30);
        var ctxRec = gb switch
        {
            <= 1 => "40960（推荐）",
            <= 3.5 => "20480（推荐）",
            <= 5 => "10240（推荐）",
            <= 6 => "8192（推荐）",
            _ => "4096（推荐）"
        };

        var modelMb = modelSize / (1024.0 * 1024.0);
        var vramMb = cuda.FreeVramMb > 0 ? cuda.FreeVramMb : cuda.TotalVramMb;

        var nglRec = cuda.Available
            ? (vramMb > 0 && modelMb <= vramMb ? "all（全部卸载到 GPU，速度最快）" : "auto（自动分配，交由 --fit on 调度）")
            : "0（无 GPU，仅 CPU）";

        var recommendedCores = Math.Max(1, Environment.ProcessorCount <= 4 ? Environment.ProcessorCount : Environment.ProcessorCount - 2);
        if (recommendedCores > 8) recommendedCores = 8;
        var cpuRec = $"{recommendedCores} 核心（保留核心供系统、HTTP服务及 GPU 轮询，本机最大 {Environment.ProcessorCount} 核心）";

        var recs = modelSize > 0
            ? $"\n\n当前模型: {gb:N2} GB\n━━━━━━━━━━━━━━━━━\n推荐值:"
            : "\n\n请先选择模型以获取推荐值。";

        return tag switch
        {
            "context_length" => ("上下文长度 (-c)", "模型一次能处理的 Token 数量。数值越大，长文本能力越强，但显存/内存占用也越高。"
                + $"\n{recs}\n{ctxRec}"),

            "context_quant" => ("Key 缓存量化 (-ctk)",
                "对注意力缓存的 Key 部分单独量化，以节省显存。K 与 V 可分别设置不同级别，自由组合。\n- 默认 (未量化/f16): 原生精度，速度最快\n- q8_0: 8-bit 量化 (显存吃紧时平衡)\n- q4_0: 4-bit 量化 (极大节省显存)\n- q4_1: 4-bit 量化\n\n💡 提示: 对于 Ling-3.0、DeepSeek 等采用 MLA 架构的模型，KV Cache 天然只有十几兆，完全无需量化。选择「默认」可消除反量化损耗。"
                + $"\n{recs}\n默认 (未量化/f16)（推荐）"),

            "context_quant_v" => ("Value 缓存量化 (-ctv)",
                "对注意力缓存的 Value 部分单独量化，与 K 量化相互独立，可组合出如 K=q8_0 + V=q4_0 等搭配。\n- 默认 (未量化/f16): 原生精度，速度最快\n- q8_0: 8-bit 量化 (显存吃紧时平衡)\n- q4_0: 4-bit 量化 (极大节省显存)\n- q4_1: 4-bit 量化\n\n💡 提示: 通常 K 对精度更敏感，可让 K 保持较高精度 (q8_0)、V 用更低精度 (q4_0) 来兼顾质量与显存。"
                + $"\n{recs}\n默认 (未量化/f16)（推荐）"),

            "offload_layers" => ("卸载层数 (-ngl)", "将多少层 Transformer 卸载到 GPU 运行。\n- all: 全部卸载（最快）\n- auto: 自动分配\n- 0: 仅 CPU\n- 数字: 指定层数"
                + $"\n{recs}\n{nglRec}"),

            "cpu_cores" => ("CPU 核心数 (-t/-tb)", "用于推理的 CPU 线程数。建议不要吃满物理核心，需留出核心供系统、HTTP 监听及 GPU 驱动轮询。"
                + $"\n{recs}\n{cpuRec}"),

            "reasoning" => ("思考模式 (--reasoning)", "控制是否启用模型的思维链（Chain-of-Thought）能力。\n- 关闭: 直接回答，速度更快\n- 开启: 先生成思考过程再回答，质量更高但更慢"
                + $"\n{recs}\n默认关闭，非推理模型开启无效"),

            "flash_attention" => ("Flash Attention (-fa)", "启用 Flash Attention 优化计算速度并降低长上下文的显存占用。\n在现代 NVIDIA 显卡上无论是 Dense 还是 MoE 模型均强烈推荐开启。"
                + $"\n{recs}\n开启 (-fa)（推荐）"),

            "batch_size" => ("批处理大小 (-b)", "控制单次推理的最大 Token 数量。\n适当调低（如 512）能有效降低显存峰值占用，避免小显存设备 OOM 崩溃。"
                + $"\n{recs}\n6G 显存推荐 512~1024，显存充足推荐 2048"),

            "ubatch_size" => ("物理批大小 (-ub)", "内部计算推理的物理分块大小。\n建议设置为与 Batch Size 相同或更小（如 256/128），进一步减少长提示词（Prompt）处理阶段的显存压力。"
                + $"\n{recs}\n6G 显存极低负载推荐 128~256，显存充足推荐 512"),

            "n_cpu_moe" => ("MoE CPU 卸载 (--n-cpu-moe)", "专用于 MoE (混合专家) 模型的优化。\n将指定层数的专家权重强制保留在内存/CPU，而将公共核心层（Attention）保留在 GPU。\n\n"
                + "- 0 (全GPU): 不强制将专家留在内存，所有专家全在 GPU 运行，配合 --fit 自动适应（速度最快！）\n"
                + "- 数字: 强制将前 N 层的专家权重保留在内存中\n\n"
                + "⚠️ 极重要性能提醒: 任何 MoE 专家放在 CPU 都会引起单 Token 多次 PCIe 跨总线频繁往返，速度大幅暴跌（如跌至 4 t/s）！"
                + $"\n{recs}\n"
                + (cuda.Available
                    ? (vramMb > 0 && modelMb > 0
                        ? (modelMb <= vramMb
                            ? "0 (全GPU)（推荐：当前可用显存充足，全部专家在 GPU 运行以获得最高速度！）"
                            : $"当前模型 ({gb:N2} GB) 超出可用显存约 {(int)(modelMb - vramMb)} MB。\n建议设为 0 (全GPU)，交由 --fit on 自动调度；若确实卡顿崩溃，仅微调 1~3 层。\n切勿设置 10~30，否则大量专家堆在 CPU 会导致性能彻底报废！建议换用 Q4 量化以全进显存。")
                        : "0 (全GPU)（推荐），显存不足卡死时才微调 1~3 层")
                    : "0（无 GPU）")),

            "verbose_log" => ("详细调用日志 (-v / --verbose)", "控制是否开启详细的运行与接口调用日志。\n- 开启: 输出外部 API 调用请求、Prompt 预处理进度、Token 生成速度统计（t/s）、Slot 槽位分配及流式传输状态等详细信息\n- 关闭: 仅输出基础服务状态与错误信息\n\n⚠️ 警告: 开启详细日志会高频写盘，可能引起推理卡顿，排查问题完毕后请务必关闭！"
                + "\n\n推荐: 默认关闭；仅在调试外部工具接入时开启"),

            "reasoning_budget" => ("思考上限 (--reasoning-budget)", "控制推理模型（如 DeepSeek-R1、Qwen、Spark 等）单次回复的最大思考 Token 数量。\n\n- 无限制 (-1/默认): 模型自行决定思考何时结束。\n- 设置数值（如 2048）: 当思考达到上限时，llama-server 会强制注入闭合标记并结束思考，促使模型直接开始输出正文。\n- 优点: 防止长篇思考耗尽单次输出上限，彻底杜绝 OpenCode 等外部 Agent 客户端的 5 分钟超时中断问题。"
                + "\n\n推荐值:\n通用对话建议 1024~2048；代码 Agent 任务建议 2048~4096"),

            _ => ("参数说明", "暂无详细说明")
        };
    }

    // ─── Actions ───

    private void RegenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_config.ModelPath) || !File.Exists(_config.ModelPath))
        {
            MessageBox.Show("请先选择有效模型。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _config.Args = PresetService.GeneratePresetArgs(_config.ModelPath, _config.Port);
            _config.Port = ServerService.ParsePortFromArgs(_config.Args, _config.Port);
            SaveCurrentConfig();
            ApplyConfig();
            LogService.Write("已重新生成预设启动参数。");
            RefreshLog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"生成参数失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PortTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(PortTextBox.Text, out var port) && port > 0 && port <= 65535)
        {
            _config.Port = port;
            // Update --port in args
            _parsingArgs = true;
            try { ArgsTextBox.Text = UpdateArgInString(ArgsTextBox.Text, "--port", port.ToString()); }
            finally { _parsingArgs = false; }
            SaveCurrentConfig();
        }
        else
            PortTextBox.Text = _config.Port.ToString();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentConfig();

        var already = ServerService.GetManagedProcess(_config);
        if (already != null)
        {
            ServerService.Stop(_config, msg => LogService.Write(msg));
            LogService.Write("已停止旧服务，即将启动新实例…");
        }

        _serverStarting = true;
        StartButton.Content = "⏳ 正在加载模型...";
        StartButton.IsEnabled = false;
        StopButton.Content = "■ 终止启动";
        StopButton.IsEnabled = true;
        OpenButton.IsEnabled = false;

        StatusDot.Fill = (Brush)FindResource("WarningBrush");
        StatusText.Text = "正在启动服务…";
        ActionStatusDot.Fill = (Brush)FindResource("WarningBrush");
        ActionStatusText.Text = "正在启动服务进程与加载模型权重，请稍候...";
        ActionEndpointText.Text = $"端口 {_config.Port}";
        ApiStatusBadge.Text = "API: 正在启动...";
        ApiStatusBadge.Foreground = (Brush)FindResource("WarningBrush");

        try
        {
            await ServerService.StartAsync(_config, openBrowser: _config.AutoOpenBrowser, msg => Dispatcher.BeginInvoke(() =>
            {
                LogService.Write(msg);
                RefreshLog();
            }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            LogService.Write($"错误：{ex.Message}");
        }
        finally
        {
            _serverStarting = false;
        }

        RefreshStatus();
        RefreshLog();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var running = ServerService.GetManagedProcess(_config) ?? ServerService.FindMainProcess(_config);
            if (running == null)
            {
                MessageBox.Show("服务尚未启动，请先点击【启动服务】。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var port = ServerService.ParsePortFromArgs(_config.Args, _config.Port);
            if (!ServerService.TestApiReady(port))
            {
                MessageBox.Show("服务正在加载模型中（模型较大时约需数秒），尚未完全就绪，请稍候再打开网页。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SaveCurrentConfig();
            ServerService.OpenWebUi(_config);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _serverStarting = false;
        try
        {
            SaveCurrentConfig();
            ServerService.Stop(_config, msg =>
            {
                LogService.Write(msg);
                RefreshLog();
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "停止失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        RefreshStatus();
        RefreshLog();
    }

    private void AutostartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AutostartService.IsPrimaryEnabled())
            {
                AutostartService.Disable(_config);
                LogService.Write("已关闭登录后自启动。");
            }
            else
            {
                SaveCurrentConfig();
                AutostartService.Enable(_config);
                LogService.Write("已开启登录后自启动。");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "自启动设置失败", MessageBoxButton.OK, MessageBoxImage.Error);
            LogService.Write($"自启动设置失败：{ex.Message}");
        }

        RefreshStatus();
        RefreshLog();
    }

    private async void EmbeddingToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_embeddingStarting) return;

        var running = ServerService.GetEmbeddingProcess(_config);
        running ??= ServerService.FindEmbeddingProcess(_config);
        if (running != null)
        {
            ServerService.StopEmbedding(_config, msg => LogService.Write(msg));
            RefreshStatus();
            RefreshLog();
            return;
        }

        if (string.IsNullOrEmpty(_config.EmbeddingModelPath))
        {
            var defaultPath = Path.Combine(ConfigService.GetLlamaCppRoot(), "models", "bge-m3-q8_0.gguf");
            if (File.Exists(defaultPath))
                _config.EmbeddingModelPath = defaultPath;
            else
            {
                MessageBox.Show("请设置嵌入模型路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        _embeddingStarting = true;
        EmbeddingToggleButton.IsEnabled = false;
        EmbeddingToggleButton.Content = "启动中…";

        try
        {
            await ServerService.StartEmbeddingAsync(_config, msg => Dispatcher.BeginInvoke(() =>
            {
                LogService.Write(msg);
                RefreshLog();
            }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "启动嵌入服务失败", MessageBoxButton.OK, MessageBoxImage.Error);
            LogService.Write($"嵌入服务错误：{ex.Message}");
        }
        finally
        {
            _embeddingStarting = false;
            RefreshStatus();
            RefreshLog();
        }
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        RefreshStatus();
        RefreshLog();
    }

    private void SetButtonsEnabled(bool enabled)
    {
        if (_serverStarting) return;
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        OpenButton.IsEnabled = enabled;
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Text = "";
        _lastLogText = "";
    }

    private void OpenLogFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logPath = ConfigService.GetMainStderrPath();
            if (File.Exists(logPath))
            {
                Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show("尚未生成日志文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开日志失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
