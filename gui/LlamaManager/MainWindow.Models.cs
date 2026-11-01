using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LlamaManager.Models;
using LlamaManager.Services;

namespace LlamaManager;

public partial class MainWindow
{
    private async Task RefreshModelsAsync()
    {
        if (_scanning || _selecting) return;
        _scanning = true;
        UpdateStartButtonState();
        ModelCountText.Text = "正在扫描模型…";
        EmptyModelsHint.Visibility = Visibility.Collapsed;
        try
        {
            var directory = string.IsNullOrEmpty(_config.ModelsDirectory) ? Path.Combine(ConfigService.GetRoot(), "models") : _config.ModelsDirectory;
            // Snapshot on the dispatcher; the worker never reads or mutates the live preset map.
            var presetSnapshot = ConfigService.CreatePresetRebindSnapshot(_config);
            var scan = await Task.Run(() =>
            {
                var list = new List<ModelInfo>();
                if (Directory.Exists(directory))
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                    list = Directory.EnumerateFiles(directory, "*.gguf", options)
                        .Where(f => !ModelPairingService.IsAuxiliaryModel(f))
                        .OrderBy(f => f).Select(f =>
                        {
                            _ = GgufService.TryRead(f); // warm metadata cache away from the dispatcher
                            return new ModelInfo { FullName = Path.GetFullPath(f), FileName = Path.GetFileName(f),
                                RelativePath = Path.GetRelativePath(directory, f), SizeBytes = new FileInfo(f).Length };
                        }).ToList();
                }
                // Signature sampling and scoring stay on the worker; applying the plan happens below.
                return (Models: list,
                    Plan: ConfigService.PlanOrphanPresetRebind(
                        presetSnapshot, list.Select(m => m.FullName)));
            });
            if (_lifetime.IsCancellationRequested) return;
            ConfigService.ApplyOrphanPresetRebind(_config, scan.Plan, save: true);
            _selecting = true;
            try
            {
                _models = scan.Models;
                _modelView = CollectionViewSource.GetDefaultView(_models);
                _modelView.Filter = item => item is ModelInfo model && ModelMatchesQuery(model);
                ModelListBox.ItemsSource = _modelView;
                ApplyModelFilter();
                var selected = SelectedEngineModelPath();
                ModelListBox.SelectedItem = _models.FirstOrDefault(m => PathIdentity.Equals(m.FullName, selected));
                UpdateModelCountText();
            }
            finally { _selecting = false; }
        }
        finally
        {
            _scanning = false;
            UpdateStartButtonState();
        }
    }

    private void ModelSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        _modelQuery = ModelSearchBox.Text.Trim();
        ApplyModelFilter();
    }

    private void ModelSortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _modelSortMode = Math.Max(0, ModelSortBox.SelectedIndex);
        ApplyModelFilter();
    }

    private void ModelSearchClearButton_Click(object sender, RoutedEventArgs e) => ModelSearchBox.Clear();

    private bool ModelMatchesQuery(ModelInfo model)
    {
        if (string.IsNullOrWhiteSpace(_modelQuery)) return true;
        foreach (var term in _modelQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (model.FileName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || model.RelativePath.Contains(term, StringComparison.OrdinalIgnoreCase)
                || model.ModelTypeDisplay.Contains(term, StringComparison.OrdinalIgnoreCase)
                || model.Category.Contains(term, StringComparison.OrdinalIgnoreCase))
                continue;
            return false;
        }
        return true;
    }

    private void ApplyModelFilter()
    {
        if (_modelView == null) return;
        using (_modelView.DeferRefresh())
        {
            _modelView.SortDescriptions.Clear();
            var sortProperty = _modelSortMode == 0 ? nameof(ModelInfo.DisplayName) : nameof(ModelInfo.SizeBytes);
            var sortDirection = _modelSortMode == 2 ? ListSortDirection.Descending : ListSortDirection.Ascending;
            _modelView.SortDescriptions.Add(new SortDescription(sortProperty, sortDirection));
        }
        UpdateModelCountText();
    }

    private void UpdateModelCountText()
    {
        var visible = _modelView?.Cast<object>().Count() ?? _models.Count;
        ModelCountText.Text = visible == _models.Count
            ? $"共 {_models.Count} 个模型"
            : $"筛选 {visible} / {_models.Count}";

        if (_models.Count == 0)
        {
            EmptyModelsHintText.Text = "未找到 .gguf 模型。点击“刷新”，或用“选择文件夹…”指定模型目录。";
            DownloadModelButton.Visibility = Visibility.Visible;
            EmptyModelsHint.Visibility = Visibility.Visible;
        }
        else if (visible == 0)
        {
            EmptyModelsHintText.Text = "没有匹配的模型。调整关键词，或点“清空”查看全部。";
            DownloadModelButton.Visibility = Visibility.Collapsed;
            EmptyModelsHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyModelsHint.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The model the currently selected engine is configured with.</summary>
    private string SelectedEngineModelPath()
    {
        var backend = SelectedEngineBackend();
        if (backend == null || backend.Descriptor.Id == "llamacpp") return _config.ModelPath;
        return _config.Engines.GetValueOrDefault(backend.Descriptor.Id)?.ModelPath ?? "";
    }

    private void UpdateSelectedModelText()
    {
        var path = SelectedEngineModelPath();
        if (string.IsNullOrEmpty(path)) { SelectedModelText.Text = "未选择模型"; ModelInfoText.Text = ""; return; }
        SelectedModelText.Text = Path.GetFileName(path);
        var metadata = GgufService.TryRead(path);
        var pairedMmproj = ModelPairingService.FindPairedMmproj(path);
        var pairedDraft = ModelPairingService.FindPairedDraftModel(path);
        var extraNotes = new List<string>();
        if (pairedMmproj != null) extraNotes.Add($"已匹配视觉投影: {Path.GetFileName(pairedMmproj)}");
        if (pairedDraft != null) extraNotes.Add($"已匹配草稿模型: {Path.GetFileName(pairedDraft)}");
        var extraText = extraNotes.Count > 0 ? "\n" + string.Join(" · ", extraNotes) : "";
        ModelInfoText.Text = metadata == null ? "无法读取 GGUF 元数据，请检查文件。" :
            $"{metadata.Architecture} · {metadata.LayerCount} 层 · 原生上下文 {metadata.ContextLength}\n{metadata.CompatibilityNote}{extraText}";
        // The MoE / MTP sections are no longer hand-hidden here: the schema panel decides with
        // VisibleWhen="model.moe" / "model.mtp", so one rule covers every backend.
        RebuildEngineParameters();
    }

    private static bool IsEngineLevelParam(string key) =>
        key is "packageRoot" or "serveRoot" or "pythonPath" or "servePort" or "host" or "enginePath" or "executablePath";

    private async Task SelectModelAsync(string path)
    {
        if (_selecting || _scanning) return;
        var backend = SelectedEngineBackend();
        var backendId = backend?.Descriptor.Id ?? "llamacpp";

        // Non-llama.cpp engines use per-model presets within EngineSettings.ModelPresets.
        if (backendId != "llamacpp")
        {
            if (PathIdentity.Equals(path, SelectedEngineModelPath())) return;
            _selecting = true;
            ConfigurationTabs.IsEnabled = false;
            try
            {
                var previousModel = SelectedEngineModelPath();
                if (!string.IsNullOrWhiteSpace(previousModel))
                {
                    SaveEngineParameters(backend!);
                }

                var settings = _config.Engines.GetValueOrDefault(backendId) ?? new EngineSettings();
                var newKey = PathIdentity.Normalize(path);

                var enginePaths = settings.Parameters
                    .Where(kv => IsEngineLevelParam(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                if (settings.ModelPresets.TryGetValue(newKey, out var existingPreset))
                {
                    settings.Parameters = new Dictionary<string, string>(existingPreset, StringComparer.OrdinalIgnoreCase);
                    foreach (var (k, v) in enginePaths)
                    {
                        if (!settings.Parameters.ContainsKey(k) || string.IsNullOrWhiteSpace(settings.Parameters[k]))
                            settings.Parameters[k] = v;
                    }
                }
                else
                {
                    var newParams = new Dictionary<string, string>(enginePaths, StringComparer.OrdinalIgnoreCase);
                    var pairedMmproj = ModelPairingService.FindPairedMmproj(path);
                    if (!string.IsNullOrEmpty(pairedMmproj)) newParams["mmproj"] = pairedMmproj;
                    var pairedDraft = ModelPairingService.FindPairedDraftModel(path);
                    if (!string.IsNullOrEmpty(pairedDraft)) newParams["draftModel"] = pairedDraft;

                    settings.Parameters = newParams;
                    settings.ModelPresets[newKey] = new Dictionary<string, string>(newParams, StringComparer.OrdinalIgnoreCase);
                }

                settings.ModelPath = path;
                _config.Engines[backendId] = settings;
                ConfigService.SaveEngineSettings(_config, backendId, settings);
                LogService.Write($"已为 {backend!.Descriptor.DisplayName} 选择 {Path.GetFileName(path)}。");
                UpdateSelectedModelText();
                _statusVersion++;
            }
            catch (Exception ex) { ShowError("选择模型失败", ex); }
            finally { _selecting = false; ConfigurationTabs.IsEnabled = true; }
            await RefreshEngineAsync();
            await RefreshStatusAsync();
            return;
        }

        if (PathIdentity.Equals(path, _config.ModelPath)) return;
        _selecting = true;
        ConfigurationTabs.IsEnabled = false;
        var previousPath = _config.ModelPath;
        try
        {
            SaveCurrentConfig(); // commit previous model's edits before changing identity
            var preset = ConfigService.GetModelPreset(_config, path);
            var args = preset?.Args ?? await Task.Run(() => PresetService.GeneratePresetArgs(path, _config.Port));
            args = ModelPairingService.ApplySpeculativeDefaults(path, args);
            args = EnsurePairedMmproj(path, args);
            if (_lifetime.IsCancellationRequested) return;
            _config.ModelPath = path;
            _config.Args = args;
            _config.Port = new LauncherArguments(args).GetPort(preset?.Port ?? _config.Port);
            ApplyConfig();
            SaveCurrentConfig();
            LogService.Write($"已选择 {Path.GetFileName(path)}，点击启动/重启后生效。");
            _statusVersion++;
        }
        catch (Exception ex)
        {
            ModelListBox.SelectedItem = _models.FirstOrDefault(m => PathIdentity.Equals(m.FullName, previousPath));
            ShowError("选择模型失败", ex);
        }
        finally { _selecting = false; ConfigurationTabs.IsEnabled = true; }
        await RefreshStatusAsync();
    }

    private async void ModelListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && !_selecting && ModelListBox.SelectedItem is ModelInfo model) await SelectModelAsync(model.FullName);
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择 GGUF 模型", Filter = "GGUF 模型|*.gguf" };
        if (dialog.ShowDialog() == true) await SelectModelAsync(Path.GetFullPath(dialog.FileName));
    }

    private async void DownloadModelButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ModelDownloadWindow { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.DownloadedPath)) return;
        await RefreshModelsAsync();
        var model = _models.FirstOrDefault(m => PathIdentity.Equals(m.FullName, dialog.DownloadedPath));
        if (model != null) await SelectModelAsync(model.FullName);
    }

    private async void RegenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selecting || _scanning) return;
        _selecting = true;
        ConfigurationTabs.IsEnabled = false;
        try
        {
            SaveCurrentConfig();
            var path = _config.ModelPath;
            var port = _config.Port;
            var args = ModelPairingService.ApplySpeculativeDefaults(
                path, await Task.Run(() => PresetService.GeneratePresetArgs(path, port)));
            if (_lifetime.IsCancellationRequested) return;
            _config.Args = args;
            ApplyConfig();
            SaveCurrentConfig();
            LogService.Write("已按模型元数据和当前设备生成保守预设，可按实测继续调优。");
        }
        catch (Exception ex) { ShowError("生成参数失败", ex); }
        finally { _selecting = false; ConfigurationTabs.IsEnabled = true; }
    }

    private async void ModelsDirButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择模型文件夹" };
        if (dialog.ShowDialog() == true) { ModelsDirTextBox.Text = dialog.FolderName; await ApplyModelsDirectoryChangeAsync(); }
    }

    private async void ModelsDirTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) await ApplyModelsDirectoryChangeAsync();
    }

    private async void ModelsDirTextBox_LostFocus(object sender, RoutedEventArgs e) => await ApplyModelsDirectoryChangeAsync();

    private async Task ApplyModelsDirectoryChangeAsync()
    {
        if (!_loaded || _updateCount > 0 || _selecting || _scanning) return;
        var directory = ModelsDirTextBox.Text.Trim();
        if (PathIdentity.Equals(directory, _config.ModelsDirectory)) return;
        try
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("模型目录不存在。");
            SaveCurrentConfig();
            _config.ModelsDirectory = Path.GetFullPath(directory);
            ConfigService.SaveConfig(_config);
            await RefreshModelsAsync();
        }
        catch (Exception ex) { ShowError("更新模型目录失败", ex); }
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selecting || _scanning) return;
        try
        {
            SaveCurrentConfig();
            CudaService.Invalidate();
            await RefreshModelsAsync();
            await RefreshStatusAsync();
        }
        catch (Exception ex) { ShowError("刷新失败", ex); }
    }
}
