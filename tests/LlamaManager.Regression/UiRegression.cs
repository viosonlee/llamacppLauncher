using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LlamaManager;
using LlamaManager.Services;

internal static class UiRegression
{
    public static Task RunAsync()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/LlamaManager;component/Styles.xaml", UriKind.Relative)
                });
                var window = new MainWindow();
                typeof(MainWindow).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                var args = (TextBox)window.FindName("ArgsTextBox");
                ComboBox Box(string name) => (ComboBox)window.FindName(name);
                args.Text = "--flash-attn off --reasoning-budget 0 --threads 4 --threads-batch 2 --prompt \"a  b\"";
                Program.Check(Box("FlashAttentionBox").SelectedItem is ComboBoxItem item && item.Content.ToString()!.StartsWith("off"), "off displayed as enabled");
                Program.Equal("0", Box("ReasoningBudgetBox").Text);
                Box("CpuCoresBox").SelectedItem = "8";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var parsed = new LauncherArguments(args.Text);
                Program.Equal("8", parsed.Get("-t")); Program.Equal("2", parsed.Get("-tb"));
                Program.Equal("off", parsed.Get("-fa")); Program.Equal("0", parsed.Get("--reasoning-budget"));
                Program.Equal("a  b", parsed.Get("--prompt"));
                args.Text = "-fa auto --reasoning-budget -1 -ctk q8_0";
                Program.Equal("-1", Box("ReasoningBudgetBox").Text);
                Program.Check(Box("ContextQuantVBox").SelectedIndex == 0, "V default silently inherited K");
                Box("ReasoningBudgetBox").SelectedIndex = 3;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("1024", new LauncherArguments(args.Text).Get("--reasoning-budget"));
                args.Text = "--spec-type draft-mtp -md \"C:/models/mtp-head-q4_0.gguf\"";
                Program.Check(Box("SpecTypeBox").SelectedItem is ComboBoxItem mtp && mtp.Content.ToString() == "draft-mtp", "spec type parsed to control");
                Program.Equal("C:/models/mtp-head-q4_0.gguf", ((TextBox)window.FindName("DraftModelBox")).Text);
                ((TextBox)window.FindName("DraftModelBox")).Text = "C:/draft/head.gguf";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var specArgs = new LauncherArguments(args.Text);
                Program.Equal("C:/draft/head.gguf", specArgs.Get("-md"));
                Program.Equal("draft-mtp", specArgs.Get("--spec-type"));
                Program.Check(((CheckBox)window.FindName("MtpCheckbox")).IsChecked == true, "mtp checkbox reflects draft-mtp");
                args.Text = "--spec-type ngram-mod";
                Program.Check(((CheckBox)window.FindName("MtpCheckbox")).IsChecked == false, "mtp checkbox ignores other spec types");
                args.Text = "";
                Program.Equal("默认", Box("ReasoningBudgetBox").Text);
                Program.Equal("", ((TextBox)window.FindName("DraftModelBox")).Text);
                Program.Equal(0, Box("FlashAttentionBox").SelectedIndex);
                // Render both changed panels without opening a desktop window.
                typeof(MainWindow).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
                var tabs = (TabControl)window.FindName("ConfigurationTabs");
                var content = (FrameworkElement)window.Content;
                ((TextBlock)window.FindName("SelectedModelText")).Text = "Sakura-Micro-Bonsai-2-GSQ-RCO.gguf";
                ((TextBlock)window.FindName("ModelInfoText")).Text = "qwen3 · 64 层 · 原生上下文 32768\n含 TQ1_0 张量；部分引擎的 CUDA 后端不支持此类型，可能回退 CPU。请以加载日志中的 GPU 权重为准。";
                foreach (var tab in new[] { 0, 1 })
                {
                    tabs.SelectedIndex = tab;
                    content.Measure(new Size(690, 775)); content.Arrange(new Rect(0, 0, 690, 775)); content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(690, 775, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var visual = new DrawingVisual();
                    using (var context = visual.RenderOpen())
                    {
                        context.DrawRectangle(window.Background, null, new Rect(0, 0, 690, 775));
                        context.DrawImage(bitmap, new Rect(0, 0, 690, 775));
                    }
                    var composited = new RenderTargetBitmap(690, 775, 96, 96, PixelFormats.Pbgra32);
                    composited.Render(visual);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(composited));
                    using var output = File.Create(Path.Combine(Directory.GetCurrentDirectory(), "logs", $"ui-regression-{tab}.png"));
                    encoder.Save(output);
                }
                app.Shutdown();
                complete.SetResult();
            }
            catch (Exception ex) { complete.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return complete.Task;
    }
}
