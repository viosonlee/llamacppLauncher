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
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                    var label = new TextBlock
                    {
                        Text = spec.Label,
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
                        ToolTip = spec.ToolTip,
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

                    var infoBtn = new Wpf.Ui.Controls.Button
                    {
                        Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.QuestionCircle24 },
                        FontSize = 11,
                        Width = 24,
                        Height = 22,
                        Padding = new Thickness(0),
                        Margin = new Thickness(4, 0, 0, 0),
                        ToolTip = spec.ToolTip ?? "点击查看详细说明与配置建议",
                    };
                    var capturedSpec = spec;
                    var backendDisplayName = backend.Descriptor.DisplayName;
                    infoBtn.Click += (_, _) => ShowParameterHelp($"{backendDisplayName} - {capturedSpec.Label}", GetEngineParamDetailedHelp(capturedSpec));
                    Grid.SetColumn(infoBtn, 2);
                    row.Children.Add(infoBtn);

                    EngineParametersPanel.Children.Add(row);
                    if (control is TextBox textBox) _engineParameterBoxes[spec.Key] = textBox;
                }
            }
        }
        finally { _engineUpdate = false; }
    }

    private static string GetEngineParamDetailedHelp(ParameterSpec spec)
    {
        return spec.Key switch
        {
            "packageRoot" =>
                "【引擎包目录 (packageRoot)】\n" +
                "• 作用：Strata 引擎二进制核心和配套服务脚本所在的根目录。\n" +
                "• 默认建议：留空即可（系统会自动匹配 engines/strata-upstream 或 engines/polystrata）。\n" +
                "• 包含文件：内含 engine\\strata.exe（或 engine-poly\\strata-poly.exe）与 serve\\server.py。",

            "serveRoot" =>
                "【服务源码根 (serveRoot)】\n" +
                "• 作用：提供 HTTP API（兼容 OpenAI / Anthropic 格式）的 Python 服务源码根目录。\n" +
                "• 默认建议：留空即可。留空时系统默认使用“引擎包目录”。",

            "pythonPath" =>
                "【Python 解释器路径 (pythonPath)】\n" +
                "• 作用：启动 API 服务端所需的 Python 可执行文件路径（要求 Python 3.10+）。\n" +
                "• 默认建议：留空即可。系统会自动使用通过“一键配置环境”准备就绪的专属虚拟环境（venv）。",

            "servePort" =>
                "【服务端口 (servePort)】\n" +
                "• 作用：Strata HTTP API 监听的端口。外部客户端（如 Claude Code、Codex、沉浸式翻译等）通过此端口连接模型。\n" +
                "• 默认建议：8091（已自动避开 llama.cpp 默认的 8080 端口）。\n" +
                "• 调优说明：若与其他软件冲突，可修改为 1024–65535 范围内的任意空闲端口。",

            "host" =>
                "【监听地址 (host)】\n" +
                "• 作用：指定 HTTP API 监听的网络 IP 地址。\n" +
                "• 选项建议：\n" +
                "  - 127.0.0.1（默认）：仅本机访问，安全性最高。\n" +
                "  - 0.0.0.0：允许局域网内其他电脑、手机通过本机内网 IP 调用此模型服务。",

            "context" =>
                "【上下文窗口 (context)】\n" +
                "• 作用：Strata 引擎单次能够记忆和处理的最大 Token 数量（--max-context）。\n" +
                "• 显存影响：上下文设得越大，KV 缓存占用的显存越多。\n" +
                "• 推荐配置：\n" +
                "  - 6GB 显存显卡：建议保持 2048 ~ 4096，防止爆显存 (OOM)\n" +
                "  - 8GB ~ 12GB 显存：推荐 4096 ~ 8192\n" +
                "  - 16GB+ 显存：可增大至 8192 ~ 16384",

            "vramReserveMib" =>
                "【显存预留 MiB (vramReserveMib)】\n" +
                "• 作用：指示 Strata 引擎在分配 GPU 显存时，强行预留指定 MiB 的安全显存（--vram-reserve-mib）。\n" +
                "• 为什么需要：防止模型把显存 100% 占满导致 Windows 桌面窗口黑屏卡死或显卡驱动崩溃重置。\n" +
                "• 推荐配置：\n" +
                "  - PolyStrata 默认 256 MiB，引擎会自动将扣除预留后的全部空闲显存尽可能多地用于缓存 MoE 专家。\n" +
                "  - 笔记本独显或单显卡用户：推荐 256 ~ 500 MiB 最佳。\n" +
                "  - 双显卡（核显负责屏幕显示，独显仅推理）：可设为 256 MiB 以腾出更多显存给模型。",

            "mtpDrafts" =>
                "【MTP 草稿数 (mtpDrafts)】\n" +
                "• 作用：Strata 引擎每步投机采样的草稿 Token 数（--mtp）。\n" +
                "• 原理：带有 MTP（Multi-Token Prediction）模块的模型可以在每个解码步同时预测多个未来 Token，命中即可一次性输出多个 Token，使解码速度大幅提升 1.8x ~ 2.5x。\n" +
                "• 推荐配置：\n" +
                "  - 推荐设为 2（默认），兼顾高接受率（70%~95%）与生成吞吐。\n" +
                "  - 设为 0 则关闭投机采样（单 Token 逐步生成）。",

            "expertSlots" =>
                "【显存专家槽位 (expertSlots)】\n" +
                "• 作用：Strata upstream 引擎中，每个 MoE 层常驻在显存中的专家槽位数（--expert-slots）。\n" +
                "• 原理：将高频使用的 MoE 专家预载进 GPU 显存，未命中的专家通过 CPU 线程池计算或动态置换，大幅提升 GPU 命中率。\n" +
                "• 推荐配置：\n" +
                "  - 6GB 显存：推荐 16 ~ 24（默认 16，约占用 0.75 GiB 显存，GPU 专家命中率可达 40%~50%，速度提升 2~3 倍）。\n" +
                "  - 8GB ~ 12GB 显存：推荐 32 ~ 64\n" +
                "  - 16GB+ 显存：推荐 128 或更大",

            "poolWorkers" =>
                "【CPU 池线程数 (poolWorkers)】\n" +
                "• 作用：Strata 引擎用于 CPU 密集型辅助计算（如文本特征处理、分词调度、投机候选搜索）的后台线程池大小。\n" +
                "• 推荐配置：默认填 0 即可（0 表示由引擎根据 CPU 物理核心数自动检测和分配最佳线程）。",

            "reasoningBudget" =>
                "【思考预算 (reasoningBudget)】\n" +
                "• 作用：针对 DeepSeek-R1 / Qwen-QwQ 等具备思维链的大模型，限制其单次思考输出的最大 Token 数。\n" +
                "• 推荐配置：\n" +
                "  - 0（默认）：不设上限，由模型自主推理完毕后作答。\n" +
                "  - 正整数（如 1024、2048）：限制思考深度，适合追求更快响应速度的日常问答场景。",

            "anthropicThinking" =>
                "【Anthropic 协议思考行为 (anthropicThinking)】\n" +
                "• 作用：当上游应用（如 Claude Code 等支持 Anthropic 接口的应用）连接时，若请求头未显式指定 thinking 字段时的默认行为。\n" +
                "• 选项说明：\n" +
                "  - model（默认）：遵循模型自身设定。思考模型（如 R1/QwQ）默认自动展开思考推理。\n" +
                "  - on_request：仅当客户端请求中显式包含 thinking 参数时才思考，否则按普通模型直接输出回答。",

            _ => string.IsNullOrWhiteSpace(spec.ToolTip)
                ? $"【{spec.Label}】\n默认值：{spec.Default ?? "无"}\n参数类型：{spec.Type}"
                : $"【{spec.Label}】\n{spec.ToolTip}\n\n默认值：{spec.Default ?? "无"}\n参数类型：{spec.Type}"
        };
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
        LogService.Write($"=== 开始配置环境：{backend.Descriptor.DisplayName} ===");
        try
        {
            await BootstrapService.RunAsync(backend.Descriptor.Id,
                new DelegateLogSink(msg => { LogService.Write(msg); AppendEngineOutput(msg); }), _lifetime.Token);
            AppendEngineOutput("初始化完成。");
            LogService.Write("初始化完成。");
        }
        catch (OperationCanceledException) { AppendEngineOutput("已取消。"); LogService.Write("已取消。"); }
        catch (Exception ex) { ShowError("初始化失败", ex); }
        finally { await RefreshEngineAsync(); await RefreshStatusAsync(); }
    }

    private async void EngineTopBootstrapButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = SelectedEngineBackend();
        if (backend == null || _serverStarting || _serverStopping) return;
        EngineTopBootstrapButton.IsEnabled = false;
        ConfigurationTabs.SelectedIndex = ConfigurationTabs.Items.Count - 1; // 切换到日志页
        LogService.Write($"=== 一键配置运行环境：{backend.Descriptor.DisplayName} ===");
        try
        {
            var steps = await BootstrapService.RunAsync(backend.Descriptor.Id,
                new DelegateLogSink(msg => { LogService.Write(msg); AppendEngineOutput(msg); }),
                _lifetime.Token);
            var allOk = steps.All(s => s.Ok);
            LogService.Write(allOk ? $"=== {backend.Descriptor.DisplayName} 环境配置完成，可以直接启动服务 ===" : "=== 部分步骤未成功，请检查日志 ===");
            if (allOk)
                MessageBox.Show($"{backend.Descriptor.DisplayName} 运行环境已配置完成！\n\n现在可以在「模型」页选择模型后点击「启动服务」。", "配置环境完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { LogService.Write("已取消环境配置。"); }
        catch (Exception ex) { ShowError("环境配置失败", ex); }
        finally
        {
            EngineTopBootstrapButton.IsEnabled = true;
            await RefreshEngineAsync();
            await RefreshStatusAsync();
        }
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
