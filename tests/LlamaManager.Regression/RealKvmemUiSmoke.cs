using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LlamaManager;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class RealKvmemUiSmoke
{
    // Explicit local acceptance: real GGUFs/hardware, isolated configuration, no model service start.
    public static async Task<int> RunAsync(string modelB)
    {
        var workspace = Directory.GetCurrentDirectory();
        var configPath = Path.Combine(workspace, "llama-manager.config.json");
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath)));
        var real = new ConfigStore(configPath).Read();
        var modelA = real.Engines["kvmem"].ModelPath;
        if (!File.Exists(modelA) || !File.Exists(modelB)) throw new FileNotFoundException("Both local acceptance models must exist.");
        var root = Path.Combine(workspace, "logs", "kvmem-ui-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = JsonSerializer.Deserialize<ManagerConfig>(JsonSerializer.Serialize(real))!;
        config.Running.Clear();
        config.ActiveBackendId = "kvmem";
        // Unique unused ports prevent the isolated window from adopting any real model service.
        config.Port = Fixtures.FreePort();
        foreach (var settings in config.Engines.Values)
        {
            settings.Port = Fixtures.FreePort();
            settings.Parameters["servePort"] = settings.Port.ToString();
            settings.Parameters["host"] = "127.0.0.1";
        }
        var acceptancePort = Fixtures.FreePort().ToString();
        // Do not initialize ConfigService before the isolated root environment is set.
        var package = real.Engines["kvmem"].Parameters.GetValueOrDefault("packageRoot");
        if (string.IsNullOrWhiteSpace(package)) package = KvmemInstallation.DefaultPackageRoot(workspace);
        if (package == null) throw new IOException("No installed KVMem package for UI acceptance.");
        var a = config.Engines["kvmem"];
        a.ModelPath = modelA;
        a.Parameters = new() { ["packageRoot"] = package, ["kvmem"] = "1", ["context"] = "131072",
            ["kvmemBudget"] = "8192", ["kvmemGenReserve"] = "512", ["kvmemDiskMib"] = "0", ["host"] = "127.0.0.1", ["servePort"] = acceptancePort };
        a.ModelPresets = new()
        {
            [PathIdentity.Normalize(modelA)] = new(a.Parameters),
            [PathIdentity.Normalize(modelB)] = new() { ["packageRoot"] = package, ["kvmem"] = "1", ["context"] = "65536",
                ["kvmemBudget"] = "4096", ["kvmemGenReserve"] = "256", ["host"] = "127.0.0.1", ["servePort"] = acceptancePort, ["kvmemDiskMib"] = "0" }
        };
        new ConfigStore(Path.Combine(root, "llama-manager.config.json")).Save(config);
        Environment.SetEnvironmentVariable("LLAMA_MANAGER_ROOT", root);
        var complete = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/LlamaManager;component/Brushes.xaml", UriKind.Relative) });
            app.Startup += async (_, _) =>
            {
                MainWindow? window = null;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                    window = new MainWindow { ShowActivated = false, Width = 960, Height = 880 };
                    window.Show();
                    await Wait(() => Field<bool>(window, "_loaded"), TimeSpan.FromSeconds(40));
                    ((TabControl)window.FindName("ConfigurationTabs")).SelectedIndex = 1;
                    await Task.Delay(150);
                    Check(((FrameworkElement)window.FindName("KvmemRecommendationPanel")).Visibility == Visibility.Visible, "recommendation card is not visible for KVMem");
                    Check(window.FindName("KvmemTuneButton") == null && window.FindName("KvmemApplyMeasuredButton") == null, "removed benchmark buttons are still present");
                    Check(typeof(MainWindow).GetMethod("KvmemTuneButton_Click", BindingFlags.NonPublic | BindingFlags.Instance) == null, "removed benchmark handler is still present");
                    Check(!Directory.Exists(Path.Combine(root, "data", "kvmem-recommendations")), "UI created a recommendation cache");
                    TextBox Box(string key) => (TextBox)window.FindName("Param_" + key);
                    void Commit(TextBox box) => box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                    var outputLimit = Box("maxOutputTokens");
                    Equal("8192", outputLimit.Text);
                    outputLimit.Text = "16384";
                    Equal("16384", ConfigService.ReadConfig().Engines["kvmem"].Parameters["maxOutputTokens"]);
                    var budget = Box("kvmemBudget");
                    budget.Text = "8193";
                    Equal("8192", ConfigService.ReadConfig().Engines["kvmem"].Parameters["kvmemBudget"]);
                    Commit(budget); Equal("8192", budget.Text);
                    budget.Text = "8210"; Commit(budget); Equal("8224", budget.Text);
                    var notes = Field<Dictionary<string, TextBlock>>(window, "_parameterAdjustmentNotes");
                    Check(notes["kvmemBudget"].Visibility == Visibility.Visible && notes["kvmemBudget"].Text.Contains("8210") && notes["kvmemBudget"].Text.Contains("8224"), "visible adjustment note missing");
                    var reserve = Box("kvmemGenReserve"); reserve.Text = "511"; Commit(reserve); Equal("512", reserve.Text);
                    var aKey = PathIdentity.Normalize(modelA); var bKey = PathIdentity.Normalize(modelB);
                    Equal("8224", ConfigService.ReadConfig().Engines["kvmem"].ModelPresets[aKey]["kvmemBudget"]);
                    await (Task)Method("SelectModelAsync").Invoke(window, [modelB])!;
                    Equal("4096", Box("kvmemBudget").Text);
                    Equal("8192", Box("maxOutputTokens").Text);
                    Box("maxOutputTokens").Text = "4096";
                    budget = Box("kvmemBudget"); budget.Text = "4101"; Commit(budget); Equal("4096", budget.Text);
                    Equal("8224", ConfigService.ReadConfig().Engines["kvmem"].ModelPresets[aKey]["kvmemBudget"]);
                    var engine = (ComboBox)window.FindName("EngineBackendBox");
                    engine.SelectedIndex = BackendRegistry.All().ToList().FindIndex(x => x.Descriptor.Id == "polystrata");
                    await Task.Delay(300);
                    // Real GGUF: the compatibility tooltip must expose the notes and must not
                    // repeat the old unconditional "6GB is below the 8GB minimum" claim.
                    var compat = (TextBlock)window.FindName("EngineCompatText");
                    await Wait(() => (compat.ToolTip as string)?.Contains("专家缓存") == true, TimeSpan.FromSeconds(30));
                    var compatTip = (string)compat.ToolTip!;
                    Check(!compatTip.Contains("8GB 最低要求"), "the compatibility note still claims an unconditional VRAM minimum");
                    Check(compatTip.Contains("预检通过"), "the tooltip drops the compatibility summary");
                    Box("threads").Text = "10";
                    Equal("10", ConfigService.ReadConfig().Engines["polystrata"].Parameters["threads"]);
                    Equal("4096", ConfigService.ReadConfig().Engines["kvmem"].ModelPresets[bKey]["kvmemBudget"]);
                    engine.SelectedIndex = BackendRegistry.All().ToList().FindIndex(x => x.Descriptor.Id == "kvmem");
                    await Task.Delay(300);
                    await (Task)Method("SelectModelAsync").Invoke(window, [modelA])!;
                    Equal("8224", Box("kvmemBudget").Text);
                    Equal("16384", Box("maxOutputTokens").Text);
                    Method("KvmemRecommendButton_Click").Invoke(window, [window.FindName("KvmemRecommendButton"), new RoutedEventArgs()]);
                    await Wait(() => Field<CancellationTokenSource?>(window, "_engineMaintenanceCancellation") == null, TimeSpan.FromSeconds(35));
                    var applied = ConfigService.ReadConfig();
                    Equal("8192", applied.Engines["kvmem"].Parameters["kvmemBudget"]);
                    Equal("16384", applied.Engines["kvmem"].Parameters["maxOutputTokens"]);
                    Equal("4096", applied.Engines["kvmem"].ModelPresets[bKey]["maxOutputTokens"]);
                    Equal("8", applied.Engines["kvmem"].Parameters["threads"]);
                    Equal("256", applied.Engines["kvmem"].Parameters["kvmemHostMib"]);
                    Equal("8192", applied.Engines["kvmem"].Parameters["kvmemDiskMib"]);
                    Equal("4096", applied.Engines["kvmem"].ModelPresets[bKey]["kvmemBudget"]);
                    Equal("10", applied.Engines["polystrata"].Parameters["threads"]);
                    Check(((TextBlock)window.FindName("KvmemHardwareText")).Text.Contains("5800H"), "real CPU was not shown");
                    Check(((TextBlock)window.FindName("KvmemRecommendationText")).Text.Contains("未实测"), "hardware estimate falsely shown as measured");
                    await File.WriteAllTextAsync(Path.Combine(root, "configuration-after-apply.json"), JsonSerializer.Serialize(applied, new JsonSerializerOptions { WriteIndented = true }));
                    Check(((TextBlock)window.FindName("KvmemHardwareText")).Foreground is SolidColorBrush { Color: var foreground } &&
                        (foreground.R + foreground.G + foreground.B) / 3 < 128, "recommendation card text is unreadable on the light card");
                    // RenderTargetBitmap does not capture the desktop Mica backdrop. Use the same
                    // application background brush for the acceptance image instead of transparent black.
                    window.Background = (Brush)app.FindResource("ApplicationBackgroundBrush");
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var image = File.Create(Path.Combine(root, "window.png"))) encoder.Save(image);
                    budget = Box("kvmemBudget"); budget.Text = "8210";
                    window.Close();
                    Equal("8224", ConfigService.ReadConfig().Engines["kvmem"].Parameters["kvmemBudget"]);
                    Equal("4096", ConfigService.ReadConfig().Engines["kvmem"].ModelPresets[bKey]["kvmemBudget"]);
                    await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), "PASS: actual WPF controls auto-align only on commit/close with visible note; real model A/B and engine switching preserve separate domains; hardware button reads the actual CPU/RAM/GPU and saves only current model. No model service started.\n");
                    complete.SetResult(0);
                }
                catch (Exception ex) { complete.SetException(ex); if (window != null) typeof(MainWindow).GetField("_loaded", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false); window?.Close(); }
                finally { app.Shutdown(); }
            };
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var result = await complete.Task;
        Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath))));
        Console.WriteLine("PASS local WPF/hardware/model-domain acceptance. Evidence: " + root);
        return result;
    }
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Wait(Func<bool> done, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done()) { if (clock.Elapsed > timeout) throw new TimeoutException("UI acceptance did not complete."); await Task.Delay(25); }
    }
}
