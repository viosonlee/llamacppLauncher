using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        if (box.IsEditable) { box.Text = value ?? "默认"; return; }
        for (var i = 0; i < box.Items.Count; i++)
        {
            var text = box.Items[i] is ComboBoxItem item ? item.Content?.ToString() : box.Items[i].ToString();
            if ((value == null && text?.StartsWith("默认") == true) || text?.Split(' ')[0] == value)
            { box.SelectedIndex = i; return; }
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
            DraftModelBox.BorderBrush = Brushes.IndianRed;
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

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        var text = tag switch
        {
            "context_length" => "上下文长度 (-c)：影响 KV 缓存占用。新预设从不超过模型原生上限的 4096 开始；根据实际显存余量增大。",
            "context_quant" or "context_quant_v" => "K/V 缓存精度可以分别设置。默认表示不传此参数，由引擎决定。量化缓存可节省显存，其支持程度、精度和速度取决于模型与后端。",
            "offload_layers" => "GPU 卸载层数 (-ngl)：0 为 CPU，数字指定层数。all/auto 是否支持取决于引擎版本；支持 --fit 的引擎可自动调整。层数不代表所有张量都在 GPU，需查看加载日志中的 GPU 权重。",
            "cpu_cores" => "CPU 解码线程数 (-t)。此控件不修改批处理线程数 -tb；可以在原始参数中单独设置 -tb。",
            "reasoning" => "思考模式 (--reasoning)：默认不传参数，on/off 显式启用或关闭。具体行为取决于模型模板和引擎支持。",
            "flash_attention" => "Flash Attention (-fa)：默认不传参数；off/on/auto 分别表示关闭、开启和由引擎选择。请根据当前后端支持情况设置。",
            "batch_size" => "逻辑批大小 (-b)：降低通常可减小提示词处理阶段的内存峰值。新预设从 512 开始。",
            "ubatch_size" => "物理批大小 (-ub)：应不超过 -b。新预设从 128 开始，可按显存余量与实测吞吐调整。",
            "n_cpu_moe" => "MoE CPU 层数 (--n-cpu-moe)：强制部分专家权重驻留 CPU。0 表示不强制，实际分配仍受 -ngl 和显存影响。混合卸载的收益取决于模型、带宽和硬件，应实测。",
            "verbose_log" => "详细日志 (-v)：排查问题时启用。可能记录请求内容并增加写盘量。",
            "reasoning_budget" => "思考预算 (--reasoning-budget)：默认不传参数；-1 显式无限制；0 显式零预算；正数为上限。不同引擎或模板的支持情况可能不同。",
            "spec_type" => "投机解码 (--spec-type)：默认不传参数（引擎默认不启用）。draft-mtp 使用多 Token 预测头；draft-simple/draft-eagle3 需要配套草稿模型；ngram-mod 为 n-gram 投机，无需草稿文件。支持与拒绝率取决于草稿质量，应实测。",
            "draft_model" => "草稿模型 (-md)：投机解码使用的草稿/头模型文件，与上方投机解码选项配合使用，例如独立 MTP 头配 draft-mtp。文件不存在会在启动时报错。",
            _ => "具体参数以所选引擎的 --help 为准。"
        };
        MessageBox.Show(text, "参数说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
