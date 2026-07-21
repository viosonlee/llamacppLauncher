using System.Windows;
using LlamaManager.Services;

namespace LlamaManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            var msg = $"未处理异常：{ex?.Message}\n\n堆栈：{ex?.StackTrace}";
            LogService.Write($"致命错误：{ex}");
            MessageBox.Show(msg, "LlamaManager 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogService.Write($"UI 异常：{args.Exception}");
            args.Handled = true;
            MessageBox.Show($"UI 错误：{args.Exception.Message}", "LlamaManager 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };

        Dispatcher.UnhandledException += (s, args) =>
        {
            LogService.Write($"调度器异常：{args.Exception}");
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
