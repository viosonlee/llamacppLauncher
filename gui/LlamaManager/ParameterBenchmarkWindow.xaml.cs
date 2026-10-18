using System.ComponentModel;
using System.IO;
using System.Windows;
using LlamaManager.Services;

namespace LlamaManager;

/// <summary>
/// Collects an A/B plan (one "label|args" line per variant) and runs it against the model the
/// launcher currently has selected. The results are written to the log page, which is where the
/// user already looks for benchmark output; the dialog itself stays open until the sweep ends.
/// </summary>
public partial class ParameterBenchmarkWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _modelPath;
    private CancellationTokenSource? _cancellation;
    private bool _running;

    public ParameterBenchmarkWindow(string modelPath, string? suggest)
    {
        InitializeComponent();
        _modelPath = modelPath;
        Title = $"参数实测对比 - {Path.GetFileName(modelPath)}";
        if (!string.IsNullOrWhiteSpace(suggest)) PlanBox.Text = suggest;
        PlanHint.Text = $"模型：{Path.GetFileName(modelPath)}";
    }

    private void RefreshHint()
    {
        if (_running) return;
        try
        {
            var plan = ParameterBenchmarkService.ParsePlan(PlanBox.Text);
            var minutes = plan.Count * Math.Max(1, ParseRuns()) * 2.0 / 6.0; // ~2 min per run, rough
            PlanHint.Text = $"{plan.Count} 组 × {ParseRuns()} 轮，约需 {minutes:0}–{minutes * 2:0} 分钟";
            RunButton.IsEnabled = plan.Count > 0;
        }
        catch (Exception ex) { PlanHint.Text = ex.Message; RunButton.IsEnabled = false; }
    }

    private void PlanBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshHint();

    private int ParseRuns() => int.TryParse(RunsBox.Text, out var runs) && runs >= 1 ? Math.Min(runs, 5) : 1;

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        IReadOnlyList<(string Label, string Args)> plan;
        try { plan = ParameterBenchmarkService.ParsePlan(PlanBox.Text); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "计划有误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (plan.Count == 0) return;

        _running = true;
        _cancellation = new CancellationTokenSource();
        RunButton.Content = "实测中…（关闭窗口取消）";
        RunButton.IsEnabled = false;
        StatusText.Text = "开始…";
        try
        {
            var report = await ParameterBenchmarkService.RunAsync(_modelPath, "", plan, ParseRuns(),
                log => Dispatcher.Invoke(() => { StatusText.Text = log; LogService.Write(log); }),
                _cancellation.Token).ConfigureAwait(true);
            var summary = ParameterBenchmarkService.Describe(report);
            LogService.Write(summary);
            StatusText.Text = report.Errors.Count == 0 ? "完成，结果已写入「日志」页。" : "部分变体失败，详见「日志」页。";
            MessageBox.Show(this, summary, "实测完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消。"; LogService.Write("参数实测已取消。"); }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "实测失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            _cancellation?.Dispose(); _cancellation = null;
            RunButton.Content = "开始实测";
            RefreshHint();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_running)
        {
            var decision = MessageBox.Show(this, "实测正在进行，关闭会取消剩余变体。继续关闭？", "实测中",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (decision != MessageBoxResult.OK) { e.Cancel = true; return; }
            _cancellation?.Cancel();
        }
        base.OnClosing(e);
    }
}
