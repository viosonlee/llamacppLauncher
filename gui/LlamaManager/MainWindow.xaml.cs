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
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
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
                        return new ModelInfo
                        {
                            FullName = Path.GetFullPath(f),
                            FileName = info.Name,
                            SizeBytes = info.Length
                        };
                    })
                    .ToList();

                _models.AddRange(ggufFiles);
            }
            catch { }
        }

        ModelListBox.ItemsSource = _models;

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
            var running = ServerService.GetManagedProcess(_config);
            running ??= ServerService.FindMainProcess(_config);
            if (running != null)
            {
                StatusDot.Fill = new SolidColorBrush(Colors.Green);
                StatusText.Text = $"运行中（PID {running.Id}）";
                ProcessInfoText.Text = $"进程: PID {running.Id} | 端口 {_config.Port}";
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
            }
            else
            {
                StatusDot.Fill = new SolidColorBrush(Colors.Red);
                StatusText.Text = "已停止";
                ProcessInfoText.Text = "进程: 无";
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
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
            var lines = LogService.ReadRecent(200);
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
            ParseArgsToControls();
            UpdateSelectedModelText();
        }
        finally { _updateCount--; }
    }

    private void ModelsDirButton_Click(object sender, RoutedEventArgs e)
    {
        var initialDir = string.IsNullOrEmpty(_config.ModelsDirectory) ? Path.Combine(ConfigService.GetRoot(), "models") : _config.ModelsDirectory;
        if (!Directory.Exists(initialDir)) initialDir = ConfigService.GetRoot();

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择模型默认文件夹",
            InitialDirectory = initialDir
        };

        if (dialog.ShowDialog() == true)
        {
            _config.ModelsDirectory = dialog.FolderName;
            ModelsDirTextBox.Text = dialog.FolderName;
            ConfigService.SaveConfig(_config);
            RefreshModels();
        }
    }

    private void ParseArgsToControls()
    {
        var args = ArgsTextBox.Text;

        // Context Length: -c NNNN
        var m = Regex.Match(args, @"(?:^|\s)-c\s+(\d+)");
        ContextLengthBox.Text = m.Success ? m.Groups[1].Value : "8192";

        // Context Quant: -ctk q8_0 -ctv q8_0
        m = Regex.Match(args, @"(?:^|\s)-ctk\s+(\S+)");
        if (m.Success)
        {
            var val = m.Groups[1].Value;
            for (int i = 0; i < ContextQuantBox.Items.Count; i++)
            {
                if (ContextQuantBox.Items[i] is ComboBoxItem item && item.Content?.ToString() is string content && content.StartsWith(val))
                { ContextQuantBox.SelectedIndex = i; break; }
            }
        }
        else ContextQuantBox.SelectedIndex = 0;

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
                if (MoeOffloadBox.Items[i] is ComboBoxItem item && string.Equals(item.Content?.ToString(), val, StringComparison.Ordinal))
                { MoeOffloadBox.SelectedIndex = i; found = true; break; }
            }
            if (!found) { MoeOffloadBox.Text = val; }
        }
        else MoeOffloadBox.Text = "0";

        // CPU MoE toggle: --cpu-moe
        CpuMoeCheckbox.IsChecked = Regex.IsMatch(args, @"(?:^|\s)--cpu-moe\b");
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
            args = UpdateArgInString(args, "-ctk", GetContextQuant());
            args = UpdateArgInString(args, "-ctv", GetContextQuant());
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
            
            if (int.TryParse(MoeOffloadBox.Text, out var moeLayers) && moeLayers >= 0)
            {
                if (moeLayers == 0)
                    args = Regex.Replace(args, @"(?:^|\s)--n-cpu-moe\s+\d+", "").Trim();
                else
                    args = UpdateArgInString(args, "--n-cpu-moe", moeLayers.ToString());
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

            ArgsTextBox.Text = args;
        }
        finally { _parsingArgs = false; }
    }

    private string GetContextQuant()
    {
        if (ContextQuantBox.SelectedItem is ComboBoxItem item)
        {
            var content = item.Content.ToString()!;
            var idx = content.IndexOf(' ');
            return idx > 0 ? content[..idx] : content;
        }
        return "q8_0";
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
        var (modelSize, hasCuda) = GetModelContext();
        var info = GetParameterInfo(tag, modelSize, hasCuda);
        MessageBox.Show(info.Text, info.Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private (long modelSize, bool hasCuda) GetModelContext()
    {
        long size = 0;
        if (!string.IsNullOrEmpty(_config.ModelPath) && File.Exists(_config.ModelPath))
        {
            try { size = new FileInfo(_config.ModelPath).Length; } catch { }
        }
        var cuda = CudaService.Detect();
        return (size, cuda.Available);
    }

    private static (string Title, string Text) GetParameterInfo(string tag, long modelSize, bool hasCuda)
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

        var nglRec = hasCuda
            ? (gb <= 3.5 ? "all（全部卸载到 GPU）" : "auto（自动分配）")
            : "0（无 GPU，仅 CPU）";

        var cpuRec = Math.Min(8, Environment.ProcessorCount).ToString();

        var recs = modelSize > 0
            ? $"\n\n当前模型: {gb:N1} GB\n━━━━━━━━━━━━━━━━━\n推荐值:"
            : "\n\n请先选择模型以获取推荐值。";

        return tag switch
        {
            "context_length" => ("上下文长度 (-c)", "模型一次能处理的 Token 数量。数值越大，长文本能力越强，但显存/内存占用也越高。"
                + $"\n{recs}\n{ctxRec}"),

            "context_quant" => ("K/V 缓存量化 (-ctk/-ctv)",
                "对注意力缓存进行量化以节省显存。\n- q8_0: 8-bit 量化 (平衡)\n- q4_0: 4-bit 量化 (更省显存)\n- q4_1: 4-bit 量化 (略高精度)\n- f16: 半精度 (最高精度)"
                + $"\n{recs}\nq8_0（推荐），f16 精度最高但显存占用最大"),

            "offload_layers" => ("卸载层数 (-ngl)", "将多少层 Transformer 卸载到 GPU 运行。\n- all: 全部卸载（最快）\n- auto: 自动分配\n- 0: 仅 CPU\n- 数字: 指定层数"
                + $"\n{recs}\n{nglRec}"),

            "cpu_cores" => ("CPU 核心数 (-t/-tb)", "用于推理的 CPU 线程数。通常设为物理核心数即可，过高会导致上下文切换开销。"
                + $"\n{recs}\n{cpuRec} 核心（本机最大 {Environment.ProcessorCount} 核心）"),

            "reasoning" => ("思考模式 (--reasoning)", "控制是否启用模型的思维链（Chain-of-Thought）能力。\n- 关闭: 直接回答，速度更快\n- 开启: 先生成思考过程再回答，质量更高但更慢"
                + $"\n{recs}\n默认关闭，非推理模型开启无效"),

            "flash_attention" => ("Flash Attention (-fa)", "启用 Flash Attention 优化计算速度并降低长上下文的显存占用。\n部分模型或量化格式可能不完全兼容，如遇报错请关闭。"
                + $"\n{recs}\n默认关闭，需要时开启"),

            "batch_size" => ("批处理大小 (-b)", "控制单次推理的最大 Token 数量。\n适当调低（如 512）能有效降低显存峰值占用，避免小显存设备 OOM 崩溃。"
                + $"\n{recs}\n6G 显存推荐 512，显存充足推荐 2048"),

            "ubatch_size" => ("物理批大小 (-ub)", "内部计算推理的物理分块大小。\n建议设置为与 Batch Size 相同或更小（如 256/128），进一步减少长提示词（Prompt）处理阶段的显存压力。"
                + $"\n{recs}\n6G 显存极低负载推荐 128~256，显存充足推荐 512"),

            "n_cpu_moe" => ("MoE CPU 卸载 (--n-cpu-moe)", "专用于 MoE (混合专家) 模型的优化。\n将多少层不常激活的专家权重强制留在内存/CPU，而把 Attention 等公共核心层保留在 GPU。\n- 0: 混合自动分配\n- 数字: 强制保留在内存的专家层数"
                + $"\n{recs}\n若运行 MoE 模型导致显存溢出卡顿，可尝试设置为 10~30"),

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

        SetButtonsEnabled(false);

        try
        {
            await ServerService.StartAsync(_config, openBrowser: true, msg => Dispatcher.BeginInvoke(() =>
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

        RefreshStatus();
        RefreshLog();
        SetButtonsEnabled(true);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
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
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        // OpenButton 始终保持可点击，方便用户在启动期间随时尝试打开浏览器
    }
}
