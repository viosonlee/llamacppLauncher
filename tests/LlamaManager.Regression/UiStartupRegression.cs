using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using LlamaManager;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;

/// <summary>
/// Runs inside UiRegression's one Application/STA dispatcher, never a second Application.
/// Each exported case owns only generated GGUF/config fixtures. No model is started, no
/// readiness/probe is awaited, and the real shutdown guard suppresses validation MessageBoxes.
/// </summary>
internal static class UiStartupRegression
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    // The central runner can report these independently without creating another WPF context.
    public static IReadOnlyList<(string Name, Action Test)> Cases(MainWindow dispatcherOwner)
    {
        var cases = new List<(string Name, Action Test)>();
        void Add(string name, Action<Fixture> test)
        {
            var numbered = $"ui-startup/{cases.Count + 1:00}: {name}";
            cases.Add((numbered, () =>
            {
                dispatcherOwner.Dispatcher.VerifyAccess();
                Program.Check(Application.Current?.Dispatcher == dispatcherOwner.Dispatcher,
                    "UI cases must run in the existing UiRegression Application");
                using var fixture = new Fixture();
                test(fixture);
            }));
        }

        // Loading/rebuilding used to silently select the first preset for ALL custom choices.
        foreach (var (key, flag, value) in new[]
        {
            ("offloadLayers", "-ngl", "35"),
            ("cpuCores", "-t", (Environment.ProcessorCount + 1).ToString()),
            ("batchSize", "-b", "4096"),
            ("ubatchSize", "-ub", "1024"),
            ("reasoningBudget", "--reasoning-budget", "1536"),
            ("moeOffload", "--n-cpu-moe", "17"),
        })
        {
            Add($"persisted custom {key} survives rebuild and focus commit", f =>
            {
                f.LoadPersisted($"-c 4096 {flag} {value} --custom \"keep  two\"");
                var disk = File.ReadAllText(f.ConfigPath);
                for (var rebuild = 0; rebuild < 2; rebuild++)
                {
                    Program.Equal(value, f.Combo(key).Text);
                    Program.Check(f.Combo(key).SelectedItem == null, "custom value became a preset");
                    f.Call("RebuildEngineParameters");
                }
                Program.Equal(disk, File.ReadAllText(f.ConfigPath)); // Rendering is not a save.
                f.Commit(key);
                Program.Equal(value, new LauncherArguments(f.Args.Text).Get(flag));
                Program.Check(f.Save(), "valid custom value blocked panel save");
                f.AssertSaved(flag, value);
            });
        }

        Add("typed custom edit survives rebuild; detached editor cannot overwrite it", f =>
        {
            var old = f.Combo("batchSize");
            old.Text = "3072";
            f.Commit("batchSize");
            Program.Equal("3072", new LauncherArguments(f.Args.Text).Get("-b"));
            f.Call("RebuildEngineParameters");
            Program.Equal("3072", f.Combo("batchSize").Text);
            old.Text = "8192";
            old.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Program.Equal("3072", new LauncherArguments(f.Args.Text).Get("-b"));
            Program.Check(f.Save(), "typed custom value blocked save");
            f.AssertSaved("-b", "3072");
        });

        Add("preset labels and recommended default commit machine values", f =>
        {
            f.LoadPersisted("-ngl 35 --reasoning-budget 1536 --custom \"keep  two\"");
            f.Pick("offloadLayers", "auto");
            Program.Equal("auto", new LauncherArguments(f.Args.Text).Get("-ngl"));
            f.Pick("reasoningBudget", "-1");
            Program.Equal("-1", new LauncherArguments(f.Args.Text).Get("--reasoning-budget"));
            f.Pick("offloadLayers", "");
            f.Commit("offloadLayers"); // Displayed 默认（推荐） is not a command-line value.
            Program.Check(!new LauncherArguments(f.Args.Text).Has("-ngl"), "default label leaked to args");
            Program.Check(f.Save(), "preset save failed");
            f.AssertSaved("--reasoning-budget", "-1");
        });

        Add("restore default clears custom choice without losing other flags", f =>
        {
            f.LoadPersisted("-b 4096 -ub 1024 --custom \"keep  two\"");
            f.Call("RestoreDefault", f.Backend.Parameters.Single(p => p.Key == "batchSize"));
            Program.Check(f.Combo("batchSize").SelectedItem is ComboBoxItem { Tag: ParamChoice { Value: "" } },
                "restore did not select the declared default");
            Program.Check(!new LauncherArguments(f.Args.Text).Has("-b"), "restore retained custom batch");
            Program.Check(f.Save(), "restored default blocked save");
            f.AssertSaved("-ub", "1024");
        });

        Add("whole-panel save commits valid free text before focus loss", f =>
        {
            // Start unselected. Typing out of a selected preset can itself raise SelectionChanged;
            // changing one custom value to another isolates the genuinely pending focus commit.
            f.LoadPersisted("-b 4096 --custom \"keep  two\"");
            f.Combo("batchSize").Text = "3072";
            Program.Equal("4096", new LauncherArguments(f.Args.Text).Get("-b"));
            Program.Check(f.Save(), "valid pending text blocked save");
            f.AssertSaved("-b", "3072");
            f.Call("RebuildEngineParameters");
            Program.Equal("3072", f.Combo("batchSize").Text);
        });

        Add("invalid pending text leaves config and preset intact; correction saves", f =>
        {
            f.LoadPersisted("-b 4096 --custom \"keep  two\"");
            var disk = File.ReadAllText(f.ConfigPath);
            var raw = f.Args.Text;
            f.Combo("batchSize").Text = "0";
            Program.Check(!f.Save(), "invalid pending batch was accepted");
            Program.Equal(disk, File.ReadAllText(f.ConfigPath));
            Program.Equal(raw, f.Args.Text);
            f.AssertError("batchSize", visible: true);
            f.Combo("batchSize").Text = "3072";
            Program.Check(f.Save(), "corrected batch still rejected");
            f.AssertError("batchSize", visible: false);
            f.AssertSaved("-b", "3072");
        });

        // Invoke the actual wired Start button, not only the validator. Clearing the painted
        // errors first proves the click revalidates; unchanged statusVersion proves it returns
        // before probe/conflict/restart work. Cancellation is a safety net, not the assertion.
        foreach (var (key, flag, value) in new[]
        {
            ("context", "-c", "0"),
            ("batchSize", "-b", "0"),
            ("ubatchSize", "-ub", "65537"),
            ("reasoningBudget", "--reasoning-budget", "not-a-number"),
            ("reasoningBudget", "--reasoning-budget", "-2"),
        })
        {
            Add($"Start rejects {flag} {value} before saving or lifecycle work", f =>
            {
                f.Args.Text = $"{flag} {value} --custom \"keep  two\"";
                if (f.Control(key) is ComboBox combo) Program.Equal(value, combo.Text);
                var disk = File.ReadAllText(f.ConfigPath);
                var inMemoryArgs = f.Config.Args;
                f.Error(key).Text = "";
                f.Error(key).Visibility = Visibility.Collapsed;
                var version = Get<int>(f.Window, "_statusVersion");
                f.Button("StartButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                f.AssertError(key, visible: true);
                Program.Equal(version, Get<int>(f.Window, "_statusVersion"));
                Program.Check(!Get<bool>(f.Window, "_serverStarting"), "invalid start entered the lifecycle");
                Program.Check(Get<CancellationTokenSource?>(f.Window, "_startCancellation") == null,
                    "invalid start created a launch cancellation source");
                Program.Equal(disk, File.ReadAllText(f.ConfigPath));
                Program.Equal(inMemoryArgs, f.Config.Args);
            });
        }

        Add("valid numeric bounds and named offload choice persist without label leakage", f =>
        {
            f.Args.Text = "-c 1 -b 65536 -ub 1 --reasoning-budget -1 -ngl all --custom \"keep  two\"";
            Program.Check(f.Save(), "valid bounds rejected");
            f.AssertSaved("-b", "65536");
            f.AssertSaved("-ub", "1");
            f.AssertSaved("--reasoning-budget", "-1");
            f.AssertSaved("-ngl", "all");
        });

        Add("embedding idle and running states remain actionable", f =>
        {
            f.Call("UpdateEmbeddingToggleState", true, false);
            Program.Check(f.Button("EmbeddingToggleButton").IsEnabled, "idle embedding disabled");
            Program.Equal("启动服务", f.Button("EmbeddingToggleButton").Content);
            f.Call("UpdateEmbeddingToggleState", true, true);
            Program.Check(f.Button("EmbeddingToggleButton").IsEnabled, "running embedding disabled");
            Program.Equal("停止服务", f.Button("EmbeddingToggleButton").Content);
        });

        Add("embedding startup stays enabled across polls and real button invoke cancels", f =>
        {
            using var cancellation = new CancellationTokenSource();
            Set(f.Window, "_embeddingCancellation", cancellation);
            Set(f.Window, "_embeddingStarting", true);
            try
            {
                foreach (var running in new[] { false, true, true })
                {
                    f.Call("UpdateEmbeddingToggleState", true, running);
                    Program.Equal("终止启动", f.Button("EmbeddingToggleButton").Content);
                    Program.Check(f.Button("EmbeddingToggleButton").IsEnabled,
                        "status poll disabled the only embedding cancel action");
                }
                // Real WPF automation respects IsEnabled (unlike blindly raising Click).
                var peer = new ButtonAutomationPeer(f.Button("EmbeddingToggleButton"));
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
                PumpUntil(f.Window, () => cancellation.IsCancellationRequested);
                // No embedding session/PID exists, and no process was launched with this
                // fixture's unique model identity. Recovery may scan, but cannot adopt a user's
                // service: both the isolated executable and this generated model must match.
                Program.Check(Get<bool>(f.Window, "_embeddingStarting"), "cancel falsely completed startup");
                Program.Check(ConfigService.ReadConfig().RunningEmbedding == null, "cancel created a session");
            }
            finally
            {
                Set(f.Window, "_embeddingStarting", false);
                Set(f.Window, "_embeddingCancellation", null);
            }
            f.Call("UpdateEmbeddingToggleState", true, false);
            Program.Equal("启动服务", f.Button("EmbeddingToggleButton").Content);
            Program.Check(f.Button("EmbeddingToggleButton").IsEnabled, "completion left embedding disabled");
        });

        Add("foreign backend cannot enable embedding even while startup is pending", f =>
        {
            foreach (var starting in new[] { false, true })
            {
                Set(f.Window, "_embeddingStarting", starting);
                f.Call("UpdateEmbeddingToggleState", false, true);
                Program.Check(!f.Button("EmbeddingToggleButton").IsEnabled, "foreign backend enabled embedding");
            }
            Set(f.Window, "_embeddingStarting", false);
        });
        Add("v1 numeric partial edits are inline errors, not modal save failures", f =>
        {
            var argsBefore = f.Args.Text;
            var path = ConfigService.GetLogPath();
            var logBefore = File.Exists(path) ? File.ReadAllText(path) : "";
            var share = (TextBox)f.Control("moeCacheCpuShare");
            share.Text = ".";
            f.AssertError("moeCacheCpuShare", true);
            Program.Equal(argsBefore, f.Args.Text);
            var logAfter = File.Exists(path) ? File.ReadAllText(path) : "";
            Program.Check(!logAfter[logBefore.Length..].Contains("参数未保存"), "incomplete numeric keystroke entered modal error path");
            share.Text = ".5";
            Program.Check(f.Save(), "complete decimal failed to save");
            // The argument editor preserves a valid spelling (.5); it need not invent a leading zero.
            f.AssertSaved("--moe-cache-cpu-share", ".5");
            share.Text = ",";
            f.AssertError("moeCacheCpuShare", true);
            Program.Equal(".5", new LauncherArguments(f.Args.Text).Get("--moe-cache-cpu-share"));
            share.Text = ",25";
            Program.Check(f.Save(), "complete comma decimal failed to save");
            f.AssertSaved("--moe-cache-cpu-share", ".25");
        });
        return cases;
    }

    public static void Run(MainWindow dispatcherOwner)
    {
        var failures = new List<Exception>();
        foreach (var (name, test) in Cases(dispatcherOwner))
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + name + "\n" + ex);
                failures.Add(new Exception(name, ex));
            }
        }
        if (failures.Count > 0) throw new AggregateException("UI startup regression cases failed", failures);
    }

    private static T Get<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, Instance)!.GetValue(window)!;

    private static void Set(MainWindow window, string name, object? value) =>
        typeof(MainWindow).GetField(name, Instance)!.SetValue(window, value);

    private static void PumpUntil(MainWindow window, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(1);
        }
        Program.Check(done(), "WPF button invocation did not settle within five seconds");
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ManagerConfig _before;
        private readonly string _model;
        public MainWindow Window { get; }
        public ManagerConfig Config { get; }
        public string ConfigPath { get; }
        public IEngineBackend Backend { get; } = BackendRegistry.ServerById("llamacpp")!;
        public TextBox Args => (TextBox)Window.FindName("ArgsTextBox");

        public Fixture()
        {
            var root = Environment.GetEnvironmentVariable("LLAMA_MANAGER_ROOT");
            Program.Check(!string.IsNullOrWhiteSpace(root) && PathIdentity.Equals(root, ConfigService.GetManagerRoot()),
                "UI startup cases require the central runner's isolated LLAMA_MANAGER_ROOT");
            _before = ConfigService.ReadConfig();
            Program.Check(_before.RunningEmbedding == null && _before.EmbeddingLastPid == 0,
                "UI cancel tests must not touch any recorded embedding service");
            // The cancelled lifetime prevents even --version from running if the start gate
            // regresses; an existing fixture image avoids the missing-engine bootstrap dialog.
            var executable = ConfigService.GetLlamaServerPath();
            Program.Check(File.Exists(executable) && Path.GetFullPath(executable).StartsWith(
                Path.GetFullPath(root!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "expected an isolated regression executable");
            ConfigPath = Path.Combine(root!, "llama-manager.config.json");
            _model = Path.Combine(root!, "ui-startup-" + Guid.NewGuid().ToString("N") + ".gguf");
            // Zero is not a legal persisted port: ConfigStore would reject it and return the
            // pre-test backup, hiding every successful UI save behind unrelated baseline args.
            // Safety comes from a unique, never-launched model identity, not malformed settings.
            Config = new ManagerConfig
            {
                ModelPath = _model, Port = 8080,
                EmbeddingModelPath = _model, EmbeddingPort = Fixtures.FreePort(),
            };
            Window = new MainWindow(); // Not shown: Loaded/timers/probes never run.
            try
            {
                File.WriteAllBytes(_model, Fixtures.Gguf(moe: true));
                Get<CancellationTokenSource>(Window, "_lifetime").Cancel();
                Set(Window, "_config", Config);
                Set(Window, "_engineUpdate", true);
                var picker = (ComboBox)Window.FindName("EngineBackendBox");
                var backends = BackendRegistry.All();
                picker.ItemsSource = backends.Select(b => b.Descriptor.DisplayName).ToArray();
                picker.SelectedIndex = backends.ToList().FindIndex(b => b.Descriptor.Id == "llamacpp");
                Set(Window, "_engineUpdate", false);
                ((TextBox)Window.FindName("PortTextBox")).Text = "8080";
                Set(Window, "_loaded", true);
                LoadPersisted("-c 4096 --custom \"keep  two\"");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void LoadPersisted(string args)
        {
            Config.Args = args;
            ConfigService.SaveModelPreset(Config, _model, args, Config.Port);
            ConfigService.SaveConfig(Config);
            var persisted = ConfigService.ReadConfig();
            Program.Check(ConfigService.RecoveryWarning == null, "fixture setup recovered a backup instead of the saved config");
            Program.Equal(args, persisted.Args);
            Program.Equal(_model, persisted.ModelPath);
            Program.Equal(_model, persisted.EmbeddingModelPath);
            Program.Equal(Config.EmbeddingPort, persisted.EmbeddingPort);
            Program.Equal(args, persisted.ModelPresets[PathIdentity.Normalize(_model)].Args);
            // ApplyConfig is the real loaded-config projection and rebuild entry point.
            Call("ApplyConfig");
            Call("RebuildEngineParameters");
        }

        public object? Call(string name, params object?[] args) =>
            typeof(MainWindow).GetMethod(name, Instance)!.Invoke(Window, args);
        public Control Control(string key) => (Control)Window.FindName("Param_" + key);
        public ComboBox Combo(string key) => (ComboBox)Control(key);
        public Button Button(string name) => (Button)Window.FindName(name);
        public TextBlock Error(string key) => Get<Dictionary<string, TextBlock>>(Window, "_engineParameterErrors")[key];
        public void Commit(string key) => Combo(key).RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        public bool Save() => (bool)Call("SaveEngineParameters", Backend)!;
        public void Pick(string key, string value) => Combo(key).SelectedItem = Combo(key).Items.OfType<ComboBoxItem>()
            .Single(item => item.Tag is ParamChoice choice && choice.Value == value);

        public void AssertError(string key, bool visible)
        {
            Program.Equal(visible ? Visibility.Visible : Visibility.Collapsed, Error(key).Visibility);
            Program.Check(!visible || Error(key).Text.Length > 0, "validator did not explain the error");
        }

        public void AssertSaved(string flag, string value)
        {
            var saved = ConfigService.ReadConfig();
            Program.Check(ConfigService.RecoveryWarning == null, "UI save was rejected and a backup was read instead");
            Program.Equal(_model, saved.ModelPath);
            Program.Equal(_model, saved.EmbeddingModelPath);
            Program.Equal(Config.EmbeddingPort, saved.EmbeddingPort);
            var parsed = new LauncherArguments(saved.Args);
            Program.Equal(value, parsed.Get(flag));
            Program.Equal("keep  two", parsed.Get("--custom"));
            Program.Equal(saved.Args, saved.ModelPresets[PathIdentity.Normalize(_model)].Args);
            Program.Equal(saved.Args, Config.Args);
        }

        public void Dispose()
        {
            Set(Window, "_loaded", false); // Closing must not save the intentionally invalid editor.
            try { Window.Close(); }
            finally
            {
                try { ConfigService.SaveConfig(_before); }
                finally { File.Delete(_model); }
            }
        }
    }
}
