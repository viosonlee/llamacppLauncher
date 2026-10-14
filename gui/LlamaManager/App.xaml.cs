using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using LlamaManager.Services;

namespace LlamaManager;

public partial class App : Application
{
    private FileStream? _instanceLock;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.Length > 0)
        {
            AttachConsole(unchecked((uint)-1));
            using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            try { Shutdown(await CommandLineService.RunAsync(e.Args, stdout)); }
            catch (Exception ex)
            {
                LogService.Write("命令失败：" + ex.Message);
                await stdout.WriteLineAsync("错误：" + ex.Message);
                Shutdown(1);
            }
            return;
        }

        try
        {
            var path = Path.Combine(ConfigService.GetManagerRoot(), "logs", "gui.lock");
            _instanceLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            MessageBox.Show("此目录的启动器已打开，请使用已有窗口。", "LlamaManager");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            LogService.Write("UI 异常：" + args.Exception);
            Diagnostics.WriteCrash(args.Exception);
            args.Handled = true;
            MessageBox.Show(args.Exception.Message, "LlamaManager 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Diagnostics.WriteCrash(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) => { Diagnostics.WriteCrash(args.Exception); args.SetObserved(); };
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceLock?.Dispose();
        base.OnExit(e);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
