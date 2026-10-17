using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LlamaManager.Services;

namespace LlamaManager;

public partial class MainWindow
{
    private static string? Choice(ComboBox box)
    {
        var text = box.IsEditable ? box.Text : box.SelectedItem is ComboBoxItem item ? item.Content?.ToString() : box.SelectedItem?.ToString();
        return string.IsNullOrWhiteSpace(text) || text.StartsWith("默认") ? null : text.Split(' ')[0];
    }

    private static void SelectValue(ComboBox box, string? value)
    {
        box.SelectedIndex = -1;
        for (var i = 0; i < box.Items.Count; i++)
        {
            var text = box.Items[i] is ComboBoxItem item ? item.Content?.ToString() : box.Items[i].ToString();
            if ((value == null && text?.StartsWith("默认") == true) || text?.Split(' ')[0] == value)
            {
                box.SelectedIndex = i;
                if (box.IsEditable) box.Text = text?.Split(' ')[0] ?? (value ?? "默认");
                return;
            }
        }
        if (box.IsEditable)
        {
            box.Text = value ?? "默认";
            return;
        }
        // Preserve values from newer engines even when this build has no predefined choice.
        if (value != null) { box.Items.Add(new ComboBoxItem { Content = value }); box.SelectedIndex = box.Items.Count - 1; }
    }

    private void ParseArgsToControls()
    {
        var args = new LauncherArguments(ArgsTextBox.Text);
        ContextLengthBox.Text = args.Get("-c") ?? "";
        SelectValue(ContextQuantKBox, args.Get("-ctk"));
        SelectValue(ContextQuantVBox, args.Get("-ctv"));
        SelectValue(OffloadLayersBox, args.Get("-ngl"));
        SelectValue(CpuCoresBox, args.Get("-t"));
        SelectValue(ReasoningBox, args.Get("--reasoning"));
        SelectValue(ReasoningBudgetBox, args.Get("--reasoning-budget"));
        SelectValue(FlashAttentionBox, args.Get("-fa"));
        SelectValue(BatchSizeBox, args.Get("-b"));
        SelectValue(UBatchSizeBox, args.Get("-ub"));
        SelectValue(MoeOffloadBox, args.Get("--n-cpu-moe"));
        CpuMoeCheckbox.IsChecked = args.Has("--cpu-moe");
        SelectValue(SpecTypeBox, args.Get("--spec-type"));
        DraftModelBox.Text = args.Get("-md") ?? "";
        UpdateDraftValidation(DraftModelBox.Text);
        MtpCheckbox.IsChecked = args.Get("--spec-type") == "draft-mtp";
        VerboseLogBox.SelectedIndex = args.Has("-v") ? 1 : 0;
        PortTextBox.Text = args.GetPort(_config.Port).ToString();
    }

    private void SyncSelection(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var text = e.AddedItems[0] is ComboBoxItem item ? item.Content?.ToString() : e.AddedItems[0]?.ToString();
        if (sender is ComboBox { IsEditable: true } box && text != null)
        {
            box.Text = text.StartsWith("默认") ? "默认" : text.Split(' ')[0];
        }
        SyncArgsFromControls(sender, text);
    }

    private void SyncArgsFromControls(object sender, string? selectionText = null)
    {
        if (!_loaded || _updateCount > 0 || _parsingArgs) return;
        try
        {
            var args = new LauncherArguments(ArgsTextBox.Text);
            string updated;
            if (sender == CpuMoeCheckbox) updated = args.SetFlag("--cpu-moe", CpuMoeCheckbox.IsChecked == true);
            else if (sender == VerboseLogBox) updated = args.SetFlag("-v", VerboseLogBox.SelectedIndex == 1);
            else if (sender == MtpCheckbox)
            {
                // Only claim draft-mtp; leave other speculative types untouched.
                if (MtpCheckbox.IsChecked == true) updated = args.Set("--spec-type", "draft-mtp");
                else updated = args.Get("--spec-type") == "draft-mtp" ? args.Set("--spec-type", null) : ArgsTextBox.Text;
            }
            else
            {
                var key = sender switch
                {
                    _ when sender == ContextLengthBox => "-c",
                    _ when sender == ContextQuantKBox => "-ctk",
                    _ when sender == ContextQuantVBox => "-ctv",
                    _ when sender == OffloadLayersBox => "-ngl",
                    _ when sender == CpuCoresBox => "-t",
                    _ when sender == ReasoningBox => "--reasoning",
                    _ when sender == ReasoningBudgetBox => "--reasoning-budget",
                    _ when sender == FlashAttentionBox => "-fa",
                    _ when sender == BatchSizeBox => "-b",
                    _ when sender == UBatchSizeBox => "-ub",
                    _ when sender == MoeOffloadBox => "--n-cpu-moe",
                    _ when sender == SpecTypeBox => "--spec-type",
                    _ when sender == DraftModelBox => "-md",
                    _ => throw new ArgumentException("未知参数控件。")
                };
                // Editable ComboBox.Text is updated after SelectionChanged. Use the event's new item.
                var value = selectionText != null
                    ? selectionText.StartsWith("默认") ? null : selectionText.Split(' ')[0]
                    : sender is ComboBox combo ? Choice(combo) : ((TextBox)sender).Text.Trim();
                if (string.IsNullOrEmpty(value)) value = null;
                if (value != null && key is "-c" or "-b" or "-ub" && (!int.TryParse(value, out var n) || n <= 0)) return;
                if (value != null && key == "--reasoning-budget" && (!int.TryParse(value, out var budget) || budget < -1)) return;
                if (value != null && key == "--n-cpu-moe" && (!int.TryParse(value, out var moe) || moe < 0)) return;
                if (value != null && key == "-ngl" && value is not ("all" or "auto") && (!int.TryParse(value, out var layers) || layers < -1)) return;
                updated = args.Set(key, value);
                if (sender == DraftModelBox) UpdateDraftValidation(value);
            }
            _parsingArgs = true;
            ArgsTextBox.Text = updated;
        }
        catch (ArgumentException ex) { ArgsTextBox.ToolTip = ex.Message; }
        finally { _parsingArgs = false; }
    }

    /// <summary>Bundled MTP heads get speculative decoding by default; explicit user choices win.</summary>
    private static string EnsureBundledMtp(string path, string args) =>
        GgufService.TryRead(path) is { HasMtp: true } && !new LauncherArguments(args).Has("--spec-type")
            ? new LauncherArguments(args).Set("--spec-type", "draft-mtp")
            : args;

    private void UpdateDraftValidation(string? path)
    {
        if (string.IsNullOrEmpty(path) || File.Exists(path))
        {
            DraftModelBox.ToolTip = string.IsNullOrEmpty(path) ? "投机解码草稿模型路径；与上方投机解码选项配合使用" : null;
            DraftModelBox.ClearValue(BorderBrushProperty);
        }
        else
        {
            DraftModelBox.ToolTip = "警告：草稿模型文件不存在，启动时将报错。";
            DraftModelBox.BorderBrush = (Brush)FindResource("SystemFillColorCriticalBrush");
        }
    }

    private void DraftModelBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择投机解码草稿模型",
            Filter = "GGUF 模型 (*.gguf)|*.gguf|所有文件 (*.*)|*.*"
        };
        var initial = Path.GetDirectoryName(_config.ModelPath);
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog(this) == true) DraftModelBox.Text = dialog.FileName;
    }

    private void ArgsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded || _parsingArgs || _updateCount > 0) return;
        _updateCount++;
        try { ParseArgsToControls(); ArgsTextBox.ToolTip = null; }
        catch (ArgumentException ex) { ArgsTextBox.ToolTip = ex.Message; }
        finally { _updateCount--; }
    }

    private void PortTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _updateCount > 0) return;
        try
        {
            if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535) throw new ArgumentException("端口必须为 1–65535。");
            ArgsTextBox.Text = new LauncherArguments(ArgsTextBox.Text).Set("--port", port.ToString());
            SaveCurrentConfig();
        }
        catch (Exception ex) { ShowError("端口设置失败", ex); }
    }

    private void ContextLength_TextChanged(object sender, TextChangedEventArgs e) => SyncArgsFromControls(sender);
    private void ContextQuant_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(sender, e);
    private void OffloadLayers_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(sender, e);
    private void OffloadLayers_LostFocus(object sender, RoutedEventArgs e) => SyncArgsFromControls(sender);
    private void CpuCores_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(sender, e);
    private void Reasoning_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(sender, e);
    private void FlashAttention_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(sender, e);
    private void BatchSize_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(sender, e);
    private void BatchSize_LostFocus(object sender, RoutedEventArgs e) => SyncArgsFromControls(sender);
    private void UBatchSize_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(sender, e);
    private void UBatchSize_LostFocus(object sender, RoutedEventArgs e) => SyncArgsFromControls(sender);
    private void MoeOffload_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(sender, e);
    private void MoeOffload_LostFocus(object sender, RoutedEventArgs e) => SyncArgsFromControls(sender);
    private void CpuMoeCheckbox_Changed(object sender, RoutedEventArgs e) => SyncArgsFromControls(sender);
    private void VerboseLog_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(sender, e);
    private void ReasoningBudget_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(sender, e);
    private void ReasoningBudget_LostFocus(object sender, RoutedEventArgs e) => SyncArgsFromControls(sender);
    private void SpecType_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(sender, e);
    private void DraftModel_TextChanged(object sender, TextChangedEventArgs e) => SyncArgsFromControls(sender);
    private void DraftModel_LostFocus(object sender, RoutedEventArgs e) => SyncArgsFromControls(sender);
    private void MtpCheckbox_Changed(object sender, RoutedEventArgs e) => SyncArgsFromControls(sender);

    private void EditableCombo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SyncArgsFromControls(sender);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    public static void ShowParameterHelp(string title, string? content)
    {
        var text = string.IsNullOrWhiteSpace(content) ? "暂无详细说明，具体参数请以对应引擎官方文档与 --help 为准。" : content;
        MessageBox.Show(text, $"参数说明 - {title}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        var (title, text) = tag switch
        {
            "context_length" => (
                "上下文长度 (-c)",
                "【功能说明】\n" +
                "模型单次问答能记忆的最大 Token 数量（包括历史对话、提问与本次生成内容）。\n\n" +
                "【显存影响】\n" +
                "KV 缓存占用的主要来源。设置越大，模型能阅读的文章和记忆越长，但显存开销成正比增加。\n\n" +
                "【推荐设置】\n" +
                "• 6GB 显存显卡（如笔记本 RTX 3060）：建议 4096 ~ 8192\n" +
                "• 8GB–12GB 显存：建议 8192 ~ 16384\n" +
                "• 16GB+ 显存：可设置 16384 ~ 32768\n\n" +
                "【注意事项】\n" +
                "请勿随意超过模型训练原生支持的上限，超出可能导致输出混乱胡言乱语。"
            ),
            "context_quant" or "context_quant_v" => (
                "K / V 缓存量化 (-ctk / -ctv)",
                "【功能说明】\n" +
                "对对话上下文保存在显存中的 Key / Value 缓存进行量化压缩。\n\n" +
                "【选项解析】\n" +
                "• 默认 (f16)：最高精度，占用显存最大。\n" +
                "• q8_0 (8-bit)：强烈推荐！节省约 40% 缓存显存，精度损失肉眼几乎不可察觉。\n" +
                "• q4_0 / q4_1 (4-bit)：节省约 70% 缓存显存，显存非常吃紧但想开大上下文时极佳。\n\n" +
                "【使用建议】\n" +
                "日常推荐保持“默认”；若想在有限显存下容纳更长上下文，建议将 K 和 V 量化同时设为 q8_0。"
            ),
            "offload_layers" => (
                "GPU 卸载层数 (-ngl)",
                "【功能说明】\n" +
                "将模型神经网络中的多少层加载到显卡（GPU）显存中加速计算。\n\n" +
                "【选项建议】\n" +
                "• all（全部卸载）：最推荐！所有层均进入 GPU 运算，生成速度最快（通常为 CPU 的数十倍）。\n" +
                "• auto（自动卸载）：由新版本引擎根据显存余量自动调整层数。\n" +
                "• 数字（如 20、25）：模型太大显存塞不下时，填入层数，剩余层走 CPU 内存混合推理。\n" +
                "• 0：纯 CPU 计算，速度最慢。\n\n" +
                "【查看技巧】\n" +
                "启动服务后可切换到右侧「日志」页，查看引擎输出中 GPU 权重加载百分比。"
            ),
            "cpu_cores" => (
                "CPU 解码线程数 (-t)",
                "【功能说明】\n" +
                "当有部分计算落入 CPU 运行（或纯 CPU 推理）时，分配给 CPU 计算的并发线程数。\n\n" +
                "【推荐设置】\n" +
                "• 建议设置为 CPU 的物理大核数量（通常为 4、6 或 8）。\n" +
                "• 勿超过物理核心数或开启全部超线程，线程过多反而会因 CPU 上下文调度争抢而变慢。"
            ),
            "reasoning" => (
                "思考模式 (--reasoning)",
                "【功能说明】\n" +
                "控制具备思考能力的大模型（如 DeepSeek-R1、Qwen-QwQ 等）是否生成思维链（<think>...</think>）。\n\n" +
                "【选项说明】\n" +
                "• 默认：不传参数，完全遵循模型自带的提示词模板。\n" +
                "• on：强制开启深度思考，输出完整推理逻辑后再给出答案。\n" +
                "• off：关闭思考过程，直接作答（响应更快，适合简单问答）。\n" +
                "• auto：由引擎或客户端请求自动识别判定。"
            ),
            "reasoning_budget" => (
                "思考上限 (--reasoning-budget)",
                "【功能说明】\n" +
                "限制模型在思维链（<think>）思考环节最多消耗的 Token 数量。\n\n" +
                "【选项解析】\n" +
                "• 默认：不限制，由模型自身逻辑决定何时结束思考。\n" +
                "• -1：显式无限制长考。\n" +
                "• 0：零预算，强行跳过思考环节直接回答。\n" +
                "• 正整数（如 1024、2048、4096）：限制思考的最大 token 上限，防止无限死循环推理。"
            ),
            "flash_attention" => (
                "Flash Attention (-fa)",
                "【功能说明】\n" +
                "现代高效注意力优化算子。在大上下文文本处理时，大幅降低显存占用并成倍加速。\n\n" +
                "【推荐设置】\n" +
                "• 推荐选择 on 或 auto（NVIDIA RTX 20/30/40/50 系列主流独显均原生支持）。\n" +
                "• 遇到不支持的特殊显卡或兼容性报错时可置为 off。"
            ),
            "batch_size" => (
                "批处理大小 (-b)",
                "【功能说明】\n" +
                "逻辑批处理大小，决定输入长提示词（Prompt Processing）时的并行处理吞吐。\n\n" +
                "【使用建议】\n" +
                "• 默认 2048（处理长文本提示词时吞吐最高）。\n" +
                "• 显存紧张时可降至 512 或 1024，能显著降低输入解析瞬间的显存峰值。"
            ),
            "ubatch_size" => (
                "物理批大小 (-ub)",
                "【功能说明】\n" +
                "单次提交给 GPU 计算核心的微批次物理大小，数值必须小于或等于 -b。\n\n" +
                "【使用建议】\n" +
                "• 默认 512。\n" +
                "• 显存不足或输入大段提示词时显卡卡顿时，可尝试降为 128 或 256。"
            ),
            "verbose_log" => (
                "详细日志 (-v)",
                "【功能说明】\n" +
                "控制台及日志面板是否输出详细的底层推理通信、显存张量分配与 HTTP 请求体日志。\n\n" +
                "【使用建议】\n" +
                "日常建议保持“关闭 (默认)”；遇到服务崩溃、无法加载或需调试网络请求时再开启。"
            ),
            "spec_type" => (
                "投机解码 (--spec-type)",
                "【功能说明】\n" +
                "利用小模型或前向预测头（MTP）预测后续词，主模型并行验证，通常可提速 1.5x ~ 2.5x！\n\n" +
                "【选项解析】\n" +
                "• draft-mtp：使用多 Token 预测头（模型内置 MTP 时直接选此项，无需外挂草稿文件）。\n" +
                "• draft-simple / draft-eagle3：使用外部配套轻量草稿模型（需在下方“草稿模型”栏选择 GGUF 文件）。\n" +
                "• ngram-mod：根据已生成文本的 n-gram 词频统计推测后续词，无需任何草稿模型。\n" +
                "• 默认 (不传)：不启用投机解码。"
            ),
            "draft_model" => (
                "草稿模型 (-md)",
                "【功能说明】\n" +
                "与投机解码（draft-simple / draft-eagle3 或独立 MTP 头）配套使用的小模型文件路径。\n\n" +
                "【使用提示】\n" +
                "草稿模型必须与主模型的词表（Tokenizer）兼容。若指定的文件不存在，启动时将直接报错。"
            ),
            "n_cpu_moe" => (
                "MoE CPU 层数 (--n-cpu-moe)",
                "【功能说明】\n" +
                "仅适用于混合专家模型（MoE，如 DeepSeek、Qwen-MoE）。强制将指定层数的专家权重放到 CPU 内存计算。\n\n" +
                "【使用建议】\n" +
                "默认 0（不强制，全按 -ngl 卸载）。当显存刚好差 1~2GB 装不下完整 MoE 模型时，可调为 1~4 层。"
            ),
            "cpu_moe" => (
                "CPU MoE (--cpu-moe)",
                "【功能说明】\n" +
                "将 MoE 混合专家模型的全部专家层转移到 CPU 内存运算，仅将主干网络保留在显卡中。\n\n" +
                "【适用场景】\n" +
                "适用于显卡显存较小（如 6G/8G）但想要越级运行超大 MoE 模型的场景。"
            ),
            "mtp" => (
                "内置 MTP 投机解码",
                "【功能说明】\n" +
                "当前模型 GGUF 文件中已检测到内置多 Token 预测（MTP）头。\n\n" +
                "【优势与建议】\n" +
                "强烈推荐勾选！勾选后自动配置 --spec-type draft-mtp，无需配置任何外部草稿模型文件，即可获得 1.5x 以上的生成加速。"
            ),
            _ => ("参数说明", "具体参数定义与用法以所选引擎的官方文档与 --help 为准。")
        };
        ShowParameterHelp(title, text);
    }
}
