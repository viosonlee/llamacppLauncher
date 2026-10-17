using System.Linq;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LlamaManager;
using LlamaManager.Models;
using LlamaManager.Services;

internal static class UiRegression
{
    // ToggleSwitch (WPF-UI) replaced CheckBox; read IsChecked without casting.
    private static bool? IsChecked(MainWindow window, string name)
    {
        var control = window.FindName(name);
        var property = control!.GetType().GetProperty("IsChecked");
        return (bool?)property!.GetValue(control);
    }

    private static void SetFlag(MainWindow window, string field, bool value) =>
        typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, value);

    private static void InvokeStatusRefresh(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod("RefreshStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // RefreshStatusAsync is async; pump the dispatcher until it settles
        // (the STA thread has no await context).
        window.Dispatcher.Invoke(() => method.Invoke(window, null));
        for (var i = 0; i < 40; i++) window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static System.Collections.Generic.IEnumerable<T> FindDescendants<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindDescendants<T>(child)) yield return descendant;
        }
    }

    public static Task RunAsync()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // The real app runs a dispatcher loop; async continuations must post back to this
                // STA thread instead of resuming on the thread pool (which would touch WPF off-thread).
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                // Mirror App.xaml: WPF-UI owns control appearance, Brushes.xaml
                // supplies legacy tokens. Missing any of them reproduces the
                // StaticResourceException users would hit at runtime.
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/LlamaManager;component/Brushes.xaml", UriKind.Relative)
                });
                var window = new MainWindow();
                typeof(MainWindow).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                // Guard contrast: labels must not render light-on-light.
                var probeLabel = (TextBlock)window.FindName("SelectedModelText");
                Program.Check(probeLabel.Foreground is SolidColorBrush { Color: var c } && (c.R + c.G + c.B) / 3 < 128,
                    $"label foreground too light for a light theme: {probeLabel.Foreground}");
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
                Program.Check(IsChecked(window, "MtpCheckbox") == true, "mtp checkbox reflects draft-mtp");
                args.Text = "--spec-type ngram-mod";
                Program.Check(IsChecked(window, "MtpCheckbox") == false, "mtp checkbox ignores other spec types");
                args.Text = "";
                Program.Equal("默认", Box("ReasoningBudgetBox").Text);
                Program.Equal("", ((TextBox)window.FindName("DraftModelBox")).Text);
                Program.Equal(0, Box("FlashAttentionBox").SelectedIndex);

                // Editable combo tests: typing custom values, LostFocus commit, and selecting dropdown items
                Box("OffloadLayersBox").Text = "35";
                Box("OffloadLayersBox").RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("35", new LauncherArguments(args.Text).Get("-ngl"));
                Box("OffloadLayersBox").SelectedIndex = 1; // "auto"
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("auto", new LauncherArguments(args.Text).Get("-ngl"));
                Program.Equal("auto", Box("OffloadLayersBox").Text);

                Box("BatchSizeBox").Text = "4096";
                Box("BatchSizeBox").RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("4096", new LauncherArguments(args.Text).Get("-b"));
                Box("BatchSizeBox").SelectedIndex = 2; // "512"
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("512", new LauncherArguments(args.Text).Get("-b"));
                Program.Equal("512", Box("BatchSizeBox").Text);
                args.Text = "";

                // Render both changed panels without opening a desktop window.
                typeof(MainWindow).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
                var tabs = (TabControl)window.FindName("ConfigurationTabs");
                var content = (FrameworkElement)window.Content;
                ((TextBlock)window.FindName("SelectedModelText")).Text = "Sakura-Micro-Bonsai-2-GSQ-RCO.gguf";
                ((TextBlock)window.FindName("LiveSpeedText")).Text = "上次速度：23.29 tok/s · 256 tokens";
                ((TextBlock)window.FindName("ModelInfoText")).Text = "qwen3 · 64 层 · 原生上下文 32768\n含 TQ1_0 张量；部分引擎的 CUDA 后端不支持此类型，可能回退 CPU。请以加载日志中的 GPU 权重为准。";
                var logTab = tabs.Items.Count - 1;
                var embeddingTab = tabs.Items.Count - 2;
                Program.Check(tabs.Items.Count == 5, $"expected 5 nav pages (model/params/advanced/embedding/log), found {tabs.Items.Count}");
                Program.Check(((TextBlock)window.FindName("ProcessInfoText")).Text == "进程: 无", "log tab lost process info");
                Program.Check(window.FindName("LogScrollViewer") is ScrollViewer, "log scroll viewer missing");
                Program.Check(window.FindName("EngineBackendBox") != null, "engine backend selector missing");
                Program.Check(window.FindName("EngineBaseUrlTextBox") != null, "engine base URL field missing");
                Program.Check(window.FindName("AutoOpenBrowserCheckBox") != null, "auto-open checkbox missing");
                Program.Check(window.FindName("EngineDoctorButton") != null, "doctor button missing");
                Program.Check(window.FindName("EmbeddingModelBox") != null && window.FindName("EmbeddingPortBox") != null,
                    "embedding tab fields missing");
                foreach (var tab in new[] { 0, 1, 2, 3, 4 })
                {
                    tabs.SelectedIndex = tab;
                    content.Measure(new Size(690, 775)); content.Arrange(new Rect(0, 0, 690, 775)); content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(690, 775, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var visual = new DrawingVisual();
                    using (var context = visual.RenderOpen())
                    {
                        context.DrawRectangle(window.Background ?? (Brush)app.FindResource("ApplicationBackgroundBrush"), null, new Rect(0, 0, 690, 775));
                        context.DrawImage(bitmap, new Rect(0, 0, 690, 775));
                    }
                    var composited = new RenderTargetBitmap(690, 775, 96, 96, PixelFormats.Pbgra32);
                    composited.Render(visual);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(composited));
                    using var output = File.Create(Path.Combine(Directory.GetCurrentDirectory(), "logs", $"ui-regression-{tab}.png"));
                    encoder.Save(output);
                    if (tab == logTab)
                    {
                        var viewer = (ScrollViewer)window.FindName("LogScrollViewer");
                        Program.Check(viewer.ActualHeight > 200, $"log tab has no room to show logs: {viewer.ActualHeight}");
                    }
                }
                // The grid guard must run after layout: before Measure/Arrange the
                // footer borders are not in the visual tree, so a missing
                // RowDefinition (footer stacked on the service buttons) slipped
                // through the parse-time tree.
                // WPF clamps out-of-range Grid.Row to the last row silently, which
                // stacks siblings on top of each other (footer borders once covered
                // the service-control buttons). Guard every direct child of every
                // grid in the window.
                foreach (var grid in FindDescendants<System.Windows.Controls.Grid>(content))
                {
                    var rows = grid.RowDefinitions.Count;
                    foreach (var child in grid.Children.OfType<UIElement>())
                    {
                        var row = System.Windows.Controls.Grid.GetRow(child);
                        var rowSpan = System.Windows.Controls.Grid.GetRowSpan(child);
                        Program.Check(row + Math.Max(1, rowSpan) <= Math.Max(rows, 1) || rows == 0,
                            $"{grid.Name ?? "?"} child {child.GetType().Name} row {row}+{rowSpan} outside {rows} rows");
                    }
                }

                // The custom ComboBox must open from a real click anywhere on its
                // surface, including the chevron. A decorative icon over the toggle
                // swallows those clicks, so hit-test a live (offscreen) instance.
                var comboHost = new Window { Width = 320, Height = 220, Opacity = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false };
                var comboProbe = new ComboBox { Style = (Style)app.FindResource("FluentComboBox"), Width = 220, Height = 30 };
                comboProbe.Items.Add("one"); comboProbe.Items.Add("two"); comboProbe.SelectedIndex = 0;
                comboHost.Content = comboProbe;
                comboHost.Show();
                for (var i = 0; i < 10; i++) comboHost.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var comboPopup = (System.Windows.Controls.Primitives.Popup)comboProbe.Template.FindName("PART_Popup", comboProbe)!;
                foreach (var fraction in new[] { 0.15, 0.5, 0.95 })
                {
                    var point = new Point(comboProbe.ActualWidth * fraction, comboProbe.ActualHeight * 0.5);
                    var hit = comboProbe.InputHitTest(point) as DependencyObject;
                    var reachesToggle = false;
                    for (var current = hit; current != null; current = VisualTreeHelper.GetParent(current))
                        if (current is System.Windows.Controls.Primitives.ToggleButton) { reachesToggle = true; break; }
                    Program.Check(reachesToggle, $"combo click at x={fraction:0.00} never reaches the toggle (dead chevron)");
                }
                comboProbe.IsDropDownOpen = true;
                for (var i = 0; i < 10; i++) comboHost.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Check(comboPopup.IsOpen && comboPopup.Child is FrameworkElement, "combo dropdown did not open");
                comboProbe.IsDropDownOpen = false;
                comboHost.Close();

                // Editable ComboBox must provide PART_EditableTextBox in its template.
                var editableProbe = new ComboBox { Style = (Style)app.FindResource("FluentComboBox"), Width = 220, Height = 30, IsEditable = true };
                editableProbe.Items.Add("alpha"); editableProbe.Items.Add("beta");
                var editableHost = new Window { Width = 320, Height = 220, Opacity = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false, Content = editableProbe };
                editableHost.Show();
                for (var i = 0; i < 10; i++) editableHost.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var editableTextBox = editableProbe.Template.FindName("PART_EditableTextBox", editableProbe) as TextBox;
                Program.Check(editableTextBox != null && editableTextBox.Visibility == Visibility.Visible, "PART_EditableTextBox missing or not visible in editable ComboBox");
                editableHost.Close();
                // The top bar is the single control surface: one set of lifecycle buttons.
                // Selecting a non-llama.cpp engine swaps the parameter panels and keeps the
                // same buttons (llama.cpp-only controls hide).
                SetFlag(window, "_loaded", true);
                // The engine list is populated on load; invoke it and pump the dispatcher.
                var applyEngine = typeof(MainWindow).GetMethod("ApplyEngineConfigAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                window.Dispatcher.Invoke(() => applyEngine.Invoke(window, null));
                for (var i = 0; i < 40; i++) window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var engineBox = (ComboBox)window.FindName("EngineBackendBox");
                var llamaParams = (FrameworkElement)window.FindName("LlamaCppParamsScroll");
                var engineParams = (FrameworkElement)window.FindName("EngineParamsScroll");
                var llamaAdv = (FrameworkElement)window.FindName("LlamaCppAdvancedPanel");
                var engineAdv = (FrameworkElement)window.FindName("EngineAdvancedPanel");
                var embeddingPanel = (FrameworkElement)window.FindName("EmbeddingPanel");
                var startButton = (Button)window.FindName("StartButton");
                var stopButton = (Button)window.FindName("StopButton");
                Program.Check(startButton != null && stopButton != null, "top bar lifecycle buttons missing");
                var items = engineBox.ItemsSource!.Cast<string>().ToList();
                var llamaIndex = items.FindIndex(n => n.Contains("llama.cpp"));
                var strataIndex = items.FindIndex(n => n.Contains("Strata"));
                Program.Check(llamaIndex >= 0 && strataIndex >= 0, "engine list missing llamacpp or Strata");

                engineBox.SelectedIndex = llamaIndex;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Check(llamaParams.Visibility == Visibility.Visible, "llamacpp params hidden for llamacpp");
                Program.Check(engineParams.Visibility == Visibility.Collapsed, "schema params shown for llamacpp");
                Program.Check(llamaAdv.Visibility == Visibility.Visible, "llamacpp advanced hidden for llamacpp");
                Program.Check(embeddingPanel.Visibility == Visibility.Visible, "embedding panel hidden for llamacpp");

                engineBox.SelectedIndex = strataIndex;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Check(llamaParams.Visibility == Visibility.Collapsed, "llamacpp params shown for Strata");
                Program.Check(engineParams.Visibility == Visibility.Visible, "schema params hidden for Strata");
                Program.Check(llamaAdv.Visibility == Visibility.Collapsed, "llamacpp advanced shown for Strata");
                Program.Check(engineAdv.Visibility == Visibility.Visible, "engine advanced hidden for Strata");
                Program.Check(embeddingPanel.Visibility == Visibility.Collapsed, "embedding panel shown for Strata (llama.cpp only)");

                // Leave the persisted active backend as llama.cpp so later tests start clean.
                ConfigService.SaveActiveBackend(ConfigService.ReadConfig(), "llamacpp");

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
