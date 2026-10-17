using System.IO;
using System.Windows;
using System.Windows.Controls;
using LlamaManager.Services;

namespace LlamaManager;

/// <summary>
/// The pieces of the llama.cpp parameter surface that are not generated from the schema: the raw
/// argument box, the port field and the shared help dialog.
/// </summary>
/// <remarks>
/// llama.cpp's controls are now built by <c>RebuildEngineParameters</c> from
/// <see cref="LlamaManager.Engines.LlamaCppParameters.Specs"/>, so the per-control sync handlers
/// that used to live here are gone. The raw <c>ArgsTextBox</c> remains the single source of truth:
/// the panel projects it (LlamaCppParameters.ReadFromArgs) and writes edits back into it
/// (LlamaCppParameters.Apply).
/// </remarks>
public partial class MainWindow
{
    /// <summary>Re-projects the raw argument string onto the schema panel after a manual edit.</summary>
    private void ArgsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded || _parsingArgs || _updateCount > 0) return;
        _updateCount++;
        try
        {
            RebuildEngineParameters();
            ArgsTextBox.ToolTip = null;
        }
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

    /// <summary>Bundled MTP heads get speculative decoding by default; explicit user choices win.</summary>
    private static string EnsureBundledMtp(string path, string args) =>
        GgufService.TryRead(path) is { HasMtp: true } && !new LauncherArguments(args).Has("--spec-type")
            ? new LauncherArguments(args).Set("--spec-type", "draft-mtp")
            : args;

    public static void ShowParameterHelp(string title, string? content)
    {
        var text = string.IsNullOrWhiteSpace(content) ? "暂无详细说明，具体参数请以对应引擎官方文档与 --help 为准。" : content;
        MessageBox.Show(text, $"参数说明 - {title}", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
