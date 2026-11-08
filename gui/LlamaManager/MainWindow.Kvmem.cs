using System.Windows;
using LlamaManager.Engines;
using LlamaManager.Services;

namespace LlamaManager;

public partial class MainWindow
{
    private PreparedKvmemRecommendation? _preparedKvmem;

    private void RefreshKvmemScopePresentation()
    {
        var backend = SelectedEngineBackend();
        if (_preparedKvmem != null && backend?.Descriptor.Id == "kvmem" &&
            PathIdentity.Equals(EngineModelFor(backend), _preparedKvmem.Config.Engines["kvmem"].ModelPath)) return;
        _preparedKvmem = null;
        KvmemHardwareText.Text = "读取 CPU、内存和 GPU，为当前模型生成保守配置。";
        KvmemRecommendationText.Text = "此模型还未读取硬件配置。容量推算不是实测最优，也不保证整个进程 RAM 占用。";
    }

    private void SetKvmemBusy(bool busy, CancellationTokenSource? cancellation = null)
    {
        _engineMaintenanceCancellation = busy ? cancellation : null;
        EngineUpdateAllButton.Content = busy ? "取消硬件配置" : "一键更新";
        EngineTopBootstrapButton.IsEnabled = !busy;
        EngineBootstrapButton.IsEnabled = !busy;
        EngineDoctorButton.IsEnabled = !busy;
        EngineBackendBox.IsEnabled = !busy;
        ConfigurationTabs.IsEnabled = !busy;
    }

    private void DescribeKvmem(PreparedKvmemRecommendation prepared)
    {
        static string Gib(long? value) => value.HasValue ? $"{value.Value / 1073741824d:0.#} GiB" : "未知";
        var hardware = prepared.Hardware;
        var gpu = hardware.Gpus.FirstOrDefault(g => g.Index == 0);
        KvmemHardwareText.Text = $"{hardware.CpuName ?? "CPU 未知"} · {hardware.PhysicalCores?.ToString() ?? "?"}核/{hardware.LogicalProcessors?.ToString() ?? "?"}线程\n" +
            $"内存 {Gib(hardware.TotalRamBytes)} · GPU 0：{gpu?.Name ?? "未知"} / {Gib(gpu?.TotalMemoryBytes)}";
        var parameters = prepared.Recommendation.Parameters;
        KvmemRecommendationText.Text = parameters.Count == 0 ? "无法生成安全配置：" + string.Join("；", prepared.Recommendation.Reasons)
            : $"硬件推算（未实测）：上下文 {parameters.GetValueOrDefault("context")}，CPU {parameters.GetValueOrDefault("threads")} 线程，" +
              $"KV 内存 {parameters.GetValueOrDefault("kvmemHostMib")} MiB + SSD {parameters.GetValueOrDefault("kvmemDiskMib")} MiB。\n" +
              $"活跃预算 {parameters.GetValueOrDefault("kvmemBudget")} / 追加区 {parameters.GetValueOrDefault("kvmemGenReserve")}；预填充分块 {parameters.GetValueOrDefault("promptBatch")}，搬运区 {parameters.GetValueOrDefault("stageMib")} MiB。";
        KvmemRecommendationText.ToolTip = string.Join("\n", prepared.Recommendation.Reasons.Concat(prepared.Recommendation.Warnings));
    }

    private async void KvmemRecommendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_engineMaintenanceCancellation != null || SelectedEngineBackend()?.Descriptor.Id != "kvmem") return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SetKvmemBusy(true, cancellation);
        try
        {
            var prepared = await KvmemHardwareConfigurationService.PrepareAsync(ConfigService.ReadConfig(), cancellation.Token);
            _preparedKvmem = prepared;
            DescribeKvmem(prepared);
            if (!prepared.Recommendation.CanApply) throw new InvalidOperationException(string.Join("；", prepared.Recommendation.Reasons));
            await Task.Run(() => KvmemHardwareConfigurationService.Apply(prepared, prepared.Recommendation.Parameters), cancellation.Token);
            KvmemRecommendationText.Text += "\n已保存到当前模型，下次启动生效。";
            LogService.Write("已应用保守硬件配置（未实测，不宣称最优）；没有启动测试进程或保存推荐缓存。");
            foreach (var reason in prepared.Recommendation.Reasons) LogService.Write(reason);
            foreach (var warning in prepared.Recommendation.Warnings) LogService.Write(warning);
        }
        catch (OperationCanceledException) { KvmemRecommendationText.Text = "已取消硬件配置。"; }
        catch (Exception ex) { ShowError("硬件配置未应用", ex); }
        finally
        {
            SetKvmemBusy(false);
            _config = ConfigService.ReadConfig();
            RebuildEngineParameters();
        }
    }
}
