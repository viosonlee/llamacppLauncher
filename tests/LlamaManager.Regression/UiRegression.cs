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

    /// <summary>The schema panel's control for a llama.cpp parameter, e.g. "flashAttention".</summary>
    private static ComboBox ParamCombo(MainWindow window, string key) =>
        (ComboBox)window.FindName("Param_" + key)!;

    private static TextBox ParamText(MainWindow window, string key) =>
        (TextBox)window.FindName("Param_" + key)!;

    /// <summary>Machine value a schema Choice control currently holds (ParamChoice.Value).</summary>
    private static string ParamValue(MainWindow window, string key) =>
        ParamCombo(window, key).SelectedItem is ComboBoxItem { Tag: LlamaManager.Engines.ParamChoice choice }
            ? choice.Value
            : "";

    /// <summary>Selects the Choice item whose machine value matches.</summary>
    private static void SetParam(MainWindow window, string key, string value)
    {
        var combo = ParamCombo(window, key);
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .First(item => item.Tag is LlamaManager.Engines.ParamChoice choice && choice.Value == value);
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
                // The schema panel only exists once a backend is selected, so initialise the engine
                // list before touching generated controls (the old hand-written XAML needed no such step).
                var initEngine = typeof(MainWindow).GetMethod("ApplyEngineConfigAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                window.Dispatcher.Invoke(() => initEngine.Invoke(window, null));
                for (var i = 0; i < 40; i++) window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var llamaBackendIndex = ((ComboBox)window.FindName("EngineBackendBox")).ItemsSource!.Cast<string>()
                    .ToList().FindIndex(n => n.Contains("llama.cpp"));
                ((ComboBox)window.FindName("EngineBackendBox")).SelectedIndex = llamaBackendIndex;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Check(window.FindName("Param_flashAttention") != null, "llama.cpp schema controls not registered");
                // The llama.cpp panel is now generated from LlamaCppParameters.Specs. Each control
                // is registered under "Param_<key>", so the test addresses parameters, not XAML names.
                args.Text = "--flash-attn off --reasoning-budget 0 --threads 4 --threads-batch 2 --prompt \"a  b\"";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("off", ParamValue(window, "flashAttention"));
                Program.Equal("0", ParamValue(window, "reasoningBudget"));
                SetParam(window, "cpuCores", "8");
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Check(args.Text.Contains("-t 8") || args.Text.Contains("--threads 8"),
                    $"cpuCores edit did not reach the raw args: [{args.Text}]");
                var parsed = new LauncherArguments(args.Text);
                Program.Equal("8", parsed.Get("-t")); Program.Equal("2", parsed.Get("-tb"));
                Program.Equal("off", parsed.Get("-fa")); Program.Equal("0", parsed.Get("--reasoning-budget"));
                Program.Equal("a  b", parsed.Get("--prompt"));
                args.Text = "-fa auto --reasoning-budget -1 -ctk q8_0";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("-1", ParamValue(window, "reasoningBudget"));
                Program.Equal("", ParamValue(window, "contextQuantV"));
                SetParam(window, "reasoningBudget", "1024");
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("1024", new LauncherArguments(args.Text).Get("--reasoning-budget"));
                args.Text = "--spec-type draft-mtp -md \"C:/models/mtp-head-q4_0.gguf\"";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("draft-mtp", ParamValue(window, "specType"));
                Program.Equal("C:/models/mtp-head-q4_0.gguf", ParamText(window, "draftModel").Text);
                ParamText(window, "draftModel").Text = "C:/draft/head.gguf";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var specArgs = new LauncherArguments(args.Text);
                Program.Equal("C:/draft/head.gguf", specArgs.Get("-md"));
                Program.Equal("draft-mtp", specArgs.Get("--spec-type"));
                Program.Check(IsChecked(window, "Param_mtp") == true, "mtp toggle reflects draft-mtp");
                args.Text = "--spec-type ngram-mod";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Check(IsChecked(window, "Param_mtp") == false, "mtp toggle ignores other spec types");
                args.Text = "";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("", ParamValue(window, "reasoningBudget"));
                Program.Equal("", ParamText(window, "draftModel").Text);
                Program.Equal("", ParamValue(window, "flashAttention"));

                // Free text in an editable Choice must reach the arguments, and picking a preset
                // must overwrite it — the behaviour the old hand-written combos had.
                ParamCombo(window, "offloadLayers").Text = "35";
                ParamCombo(window, "offloadLayers").RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("35", new LauncherArguments(args.Text).Get("-ngl"));
                SetParam(window, "offloadLayers", "auto");
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("auto", new LauncherArguments(args.Text).Get("-ngl"));

                ParamCombo(window, "batchSize").Text = "4096";
                ParamCombo(window, "batchSize").RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("4096", new LauncherArguments(args.Text).Get("-b"));
                SetParam(window, "batchSize", "512");
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Equal("512", new LauncherArguments(args.Text).Get("-b"));
                args.Text = "";

                // Render both changed panels without opening a desktop window.
                typeof(MainWindow).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
                var tabs = (TabControl)window.FindName("ConfigurationTabs");
                var content = (FrameworkElement)window.Content;
                var expectedNav = new[] { "模型", "参数", "高级", "嵌入", "日志" };
                Program.Check(tabs.Items.Count == expectedNav.Length, "navigation labels changed page count");
                for (var i = 0; i < expectedNav.Length; i++)
                {
                    var header = (tabs.Items[i] as TabItem)?.Header as Panel;
                    Program.Check(header?.Children.OfType<TextBlock>().Any(text => text.Text == expectedNav[i]) == true,
                        $"navigation label missing: {expectedNav[i]}");
                }
                ((TextBlock)window.FindName("SelectedModelText")).Text = "Sakura-Micro-Bonsai-2-GSQ-RCO.gguf";
                ((TextBlock)window.FindName("LiveSpeedText")).Text = "生成中（思考中）：45.90 tok/s · 115/300 tokens · 4.8 s";
                ((TextBlock)window.FindName("ActionStatusText")).Text = "ornith-1.5-35b-a3b-apex-mtp-i-mini.gguf · PID 8848";
                ((TextBlock)window.FindName("ModelInfoText")).Text = "qwen3 · 64 层 · 原生上下文 32768\n含 TQ1_0 张量；部分引擎的 CUDA 后端不支持此类型，可能回退 CPU。请以加载日志中的 GPU 权重为准。";
                // The log page renders file content; seed it the way a real session would.
                LogService.Write("启动 PolyStrata (strata-poly) 服务：strata-poly.exe --serve --model ornith.gguf");
                LogService.Write("[polystrata] 请求完成：300 tokens · 45.90 tok/s · 8.1 s · 草稿接受 157/180（87%）");
                var logMethod = typeof(MainWindow).GetMethod("RefreshLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
                window.Dispatcher.Invoke(() => logMethod.Invoke(window, null));
                for (var i = 0; i < 10; i++) window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                // The speed badge has its own grid column, so it must stay inside the window even
                // when the model name is long: the old single-row top bar clipped it away entirely.
                content.Measure(new Size(690, 775)); content.Arrange(new Rect(0, 0, 690, 775)); content.UpdateLayout();
                var speedText = (TextBlock)window.FindName("LiveSpeedText");
                var speedBadge = (FrameworkElement)window.FindName("LiveSpeedBadge")!;
                var card = (FrameworkElement)window.FindName("TopBarCard")!;
                var speedTopLeft = speedBadge.TransformToAncestor(content).Transform(new Point(0, 0));
                var cardRight = card.TransformToAncestor(content).Transform(new Point(card.ActualWidth, 0)).X;
                Program.Check(speedBadge.Visibility == Visibility.Visible && speedBadge.ActualWidth > 60,
                    $"speed badge collapsed or too narrow ({speedBadge.ActualWidth:0}px)");
                Program.Check(speedTopLeft.X >= card.TransformToAncestor(content).Transform(new Point(0, 0)).X &&
                    speedTopLeft.X + speedBadge.ActualWidth <= cardRight + 1,
                    $"speed badge rendered outside the card (x={speedTopLeft.X:0}, width={speedBadge.ActualWidth:0}, card right={cardRight:0})");
                Program.Check(speedText.ActualWidth >= 40 && speedText.ActualWidth < speedBadge.ActualWidth + 2,
                    $"speed text did not get its measured width ({speedText.ActualWidth:0}px)");
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
                        Program.Check(viewer.ScrollableWidth <= 1,
                            $"long log lines still scroll horizontally instead of wrapping: {viewer.ScrollableWidth:0}px");
                        // A rendered log page must actually contain the lines it was given: an
                        // empty box is exactly the "log shows nothing" defect users report.
                        var logBox = (TextBox)window.FindName("LogTextBox")!;
                        Program.Check(logBox.Text.Length > 0 && logBox.Text.Contains("请求完成"), "log page rendered empty");
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
                // llama.cpp now renders through the same schema panel as every other backend, so
                // the parameter surface no longer swaps; only the raw-args/advanced tab and the
                // embedding page remain llama.cpp-specific.
                Program.Check(engineParams.Visibility == Visibility.Visible, "schema params hidden for llamacpp");
                Program.Check(llamaAdv.Visibility == Visibility.Visible, "llamacpp advanced hidden for llamacpp");
                Program.Check(embeddingPanel.Visibility == Visibility.Visible, "embedding panel hidden for llamacpp");
                Program.Check(engineAdv.Visibility == Visibility.Collapsed, "foreign advanced panel shown for llamacpp");
                var llamaParamsPanel = (StackPanel)window.FindName("EngineParametersPanel");
                Program.Check(llamaParamsPanel.Children.Count > 0, "schema panel rendered nothing for llamacpp");
                Program.Check(window.FindName("Param_flashAttention") != null,
                    "llama.cpp schema controls missing for llamacpp");

                engineBox.SelectedIndex = strataIndex;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Check(engineParams.Visibility == Visibility.Visible, "schema params hidden for Strata");
                Program.Check(llamaAdv.Visibility == Visibility.Collapsed, "llamacpp advanced shown for Strata");
                Program.Check(engineAdv.Visibility == Visibility.Visible, "engine advanced hidden for Strata");
                Program.Check(embeddingPanel.Visibility == Visibility.Collapsed, "embedding panel shown for Strata (llama.cpp only)");
                // A backend swap must rebuild the panel, not leave llama.cpp's controls behind.
                Program.Check(window.FindName("Param_flashAttention") == null,
                    "llama.cpp controls survived the switch to Strata");
                Program.Check(window.FindName("Param_context") != null, "Strata controls not rebuilt on switch");

                // The schema panel must actually build controls, carry the search box and the
                // "advanced" fold, and keep every declared field reachable either directly or
                // folded — a silently empty panel is the failure this guards against.
                var paramsPanel = (StackPanel)window.FindName("EngineParametersPanel");
                Program.Check(paramsPanel.Children.Count > 0, "schema parameter panel rendered nothing for Strata");
                Program.Check(window.FindName("EngineParamSearchBox") is Wpf.Ui.Controls.TextBox,
                    "parameter search box missing");
                Program.Check(window.FindName("EngineResetAllButton") != null, "restore-all button missing");
                var strataBackend = LlamaManager.Engines.BackendRegistry.ServerById("polystrata")!;
                Program.Check(strataBackend.Parameters.Count >= 8,
                    $"strata panel declares only {strataBackend.Parameters.Count} parameters");
                Program.Check(strataBackend.Parameters.Any(p => p.Advanced), "no advanced parameters to fold");
                Program.Check(strataBackend.Parameters.All(p => !string.IsNullOrWhiteSpace(p.EffectiveHelp)),
                    "a parameter has no help text for its '?' button");

                // Decimal input, whole-panel saving and model presets must all use the same
                // normalization; a SaveEngineParameters call used to overwrite the per-edit dot.
                var validate = typeof(MainWindow).GetMethod("ValidateValue", BindingFlags.Static | BindingFlags.NonPublic)!;
                var savePanel = typeof(MainWindow).GetMethod("SaveEngineParameters", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var biasSpec = strataBackend.Parameters.Single(p => p.Key == "cacheBias");
                var before = ConfigService.ReadConfig().Engines.GetValueOrDefault("polystrata") ?? new EngineSettings();
                var culture = System.Globalization.CultureInfo.CurrentCulture;
                try
                {
                    foreach (var name in new[] { "zh-CN", "de-DE", "en-US" })
                    {
                        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);
                        var biasBox = ParamText(window, "cacheBias");
                        biasBox.Text = "0,5";
                        Program.Check(validate.Invoke(null, [biasSpec, biasBox.Text]) == null, "comma decimal was rejected in " + name);
                        Program.Equal("0.5", ConfigService.ReadConfig().Engines["polystrata"].Parameters["cacheBias"]);
                        Program.Check((bool)savePanel.Invoke(window, [strataBackend])!, "valid decimal blocked panel save");
                        Program.Equal("0.5", ConfigService.ReadConfig().Engines["polystrata"].Parameters["cacheBias"]);
                        biasBox.Text = "0.3";
                        Program.Equal("0.3", ConfigService.ReadConfig().Engines["polystrata"].Parameters["cacheBias"]);
                    }
                    foreach (var invalid in new[] { "NaN", "Infinity", "auto", "2.5", "0,1,2" })
                        Program.Check(validate.Invoke(null, [biasSpec, invalid]) != null, "invalid decimal accepted: " + invalid);
                    ParamText(window, "cacheBias").Text = "2.5";
                    Program.Check(!(bool)savePanel.Invoke(window, [strataBackend])!, "out-of-range bias did not block panel save");
                    ParamText(window, "cacheBias").Text = "0";
                }
                finally
                {
                    System.Globalization.CultureInfo.CurrentCulture = culture;
                    ConfigService.SaveEngineSettings(ConfigService.ReadConfig(), "polystrata", before);
                }
                // Model-dependent fields live on llama.cpp's panel (MoE offload, MTP, draft model);
                // that is where the VisibleWhen vocabulary has to stay wired to real specs.
                var llamaBackend = LlamaManager.Engines.BackendRegistry.ServerById("llamacpp")!;
                Program.Check(llamaBackend.Parameters.Any(p => p.VisibleWhen != null),
                    "no parameter uses VisibleWhen, so model-dependent fields would always show");
                // Filtering must hide rows without destroying them.
                var search = (Wpf.Ui.Controls.TextBox)window.FindName("EngineParamSearchBox")!;
                search.Text = "上下文";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var visibleRows = paramsPanel.Children.OfType<FrameworkElement>()
                    .Count(c => c.Visibility == Visibility.Visible);
                Program.Check(visibleRows > 0 && visibleRows < paramsPanel.Children.Count,
                    $"filter did not narrow the panel ({visibleRows}/{paramsPanel.Children.Count})");
                search.Text = "";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                // Model search filters without rescanning; helper weights stay out of the primary
                // list, and an empty result explains itself instead of looking like a broken scan.
                var uiModelsDir = Path.Combine(ConfigService.GetManagerRoot(), "ui-models");
                Directory.CreateDirectory(uiModelsDir);
                foreach (var name in new[] { "Zeta.gguf", "Alpha.gguf", "mmproj-x.gguf", "draft-x.gguf", "mtp-x.gguf" })
                    File.WriteAllText(Path.Combine(uiModelsDir, name), "fixture");
                var configField = typeof(MainWindow).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var windowConfig = (ManagerConfig)configField.GetValue(window)!;
                windowConfig.ModelsDirectory = uiModelsDir;
                var refreshModels = typeof(MainWindow).GetMethod("RefreshModelsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var scanTask = (Task)refreshModels.Invoke(window, null)!;
                var scanStartButton = (Button)window.FindName("StartButton");
                Program.Check(!scanStartButton.IsEnabled, "start button stayed clickable while models were scanning");
                for (var i = 0; i < 400 && !scanTask.IsCompleted; i++)
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                scanTask.GetAwaiter().GetResult();
                Program.Check(scanStartButton.IsEnabled, "start button was not restored after model scanning");

                var modelList = (ListBox)window.FindName("ModelListBox");
                var modelSearch = (Wpf.Ui.Controls.TextBox)window.FindName("ModelSearchBox");
                var modelSort = (ComboBox)window.FindName("ModelSortBox");
                var modelCount = (TextBlock)window.FindName("ModelCountText");
                var emptyHint = (FrameworkElement)window.FindName("EmptyModelsHint");
                Program.Check(modelList.Items.Count == 2, $"auxiliary weights leaked into the model list: {modelList.Items.Count}");
                Program.Check(modelCount.Text.Contains("共 2"), "model count did not show the scanned total");
                modelSearch.Text = "zeta";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Check(modelList.Items.Count == 1 && modelList.Items[0] is ModelInfo { FileName: "Zeta.gguf" },
                    "name search did not narrow the model list");
                Program.Check(modelCount.Text.Contains("筛选 1 / 2"), "model filter count missing");
                modelSearch.Text = "no-such-model";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Check(modelList.Items.Count == 0 && emptyHint.Visibility == Visibility.Visible,
                    "empty search result did not show the explanation");
                modelSort.SelectedIndex = 2;
                modelSearch.Clear();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Program.Check(modelList.Items.Count == 2, "clearing search did not restore all models");

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
