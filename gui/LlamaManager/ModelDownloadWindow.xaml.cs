using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LlamaManager.Services;

namespace LlamaManager;

/// <summary>
/// Lets a brand-new machine pick one curated GGUF and download it into the models folder.
/// The catalogue is ordered smallest first; entries beyond the detected VRAM stay selectable
/// but carry a warning, because a card too small for everything can still run a MoE hybrid.
/// </summary>
public partial class ModelDownloadWindow : Wpf.Ui.Controls.FluentWindow
{
    private sealed record Row(ModelDownload Model, string VramLabel, string SizeLabel);

    private CancellationTokenSource? _cancellation;
    private bool _downloading;

    /// <summary>The local path of a completed download; null until one succeeds.</summary>
    public string? DownloadedPath { get; private set; }

    public ModelDownloadWindow()
    {
        InitializeComponent();
        var device = CudaService.DetectAsync().GetAwaiter().GetResult();
        var freeVram = device.Available ? device.TotalVramMb : 0;
        ModelList.ItemsSource = ModelCatalog.Recommendations.Select(m => new Row(m,
            freeVram > 0 ? $"≈{m.VramMb / 1024.0:0.#} GiB" + (m.Fits(freeVram) ? "" : " · 超出显存") : $"≈{m.VramMb / 1024.0:0.#} GiB",
            EngineDownloadService.FormatBytes(m.Bytes))).ToList();
        ModelList.SelectedIndex = 0;
        var root = Path.GetPathRoot(Path.GetFullPath(ModelDownloadService.TargetDirectory));
        try
        {
            if (root != null)
                DiskHint.Text = $"保存到 {ModelDownloadService.TargetDirectory}（{root} 剩余 {EngineDownloadService.FormatBytes(new DriveInfo(root).AvailableFreeSpace)}）";
        }
        catch { DiskHint.Text = $"保存到 {ModelDownloadService.TargetDirectory}"; }
    }

    private ModelDownload? Selected => (ModelList.SelectedItem as Row)?.Model;

    private void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        DownloadButton.IsEnabled = !_downloading && Selected != null;

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var model = Selected;
        if (model == null || _downloading) return;
        _downloading = true;
        _cancellation = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        DownloadStatus.Visibility = Visibility.Visible;
        DownloadProgress.Visibility = Visibility.Visible;
        try
        {
            var result = await ModelDownloadService.DownloadAsync(model,
                log => Dispatcher.Invoke(() => DownloadStatus.Text = log),
                _cancellation.Token,
                progress: (received, total) => Dispatcher.Invoke(() =>
                {
                    DownloadStatus.Text = $"{model.DisplayName}：{EngineDownloadService.FormatBytes(received)}" +
                        (total is > 0 ? $" / {EngineDownloadService.FormatBytes(total.Value)}" : "");
                    DownloadProgress.IsIndeterminate = total is not > 0;
                    DownloadProgress.Value = total is > 0 ? 100.0 * received / total.Value : 0;
                })).ConfigureAwait(true);
            DownloadStatus.Text = $"完成：{result.Path}";
            DownloadedPath = result.Path;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            DownloadStatus.Text = "已取消。";
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "下载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _downloading = false;
            _cancellation?.Dispose(); _cancellation = null;
            DownloadButton.IsEnabled = Selected != null;
            CancelButton.Visibility = Visibility.Collapsed;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) { _cancellation?.Cancel(); return; }
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_downloading)
        {
            var decision = MessageBox.Show(this, "正在下载，关闭窗口会取消本次下载。继续关闭？", "下载中",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (decision != MessageBoxResult.OK) { e.Cancel = true; return; }
            _cancellation?.Cancel();
        }
        base.OnClosing(e);
    }
}
