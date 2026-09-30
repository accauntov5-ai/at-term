using System.Windows;
using System.Windows.Threading;
using MuxTerminal.App.Services;

namespace MuxTerminal.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            // Проверка без окна (используется в CI на Windows): код выхода 0 — успех.
            // В фоновом потоке: ожидание асинхронного кода в UI-потоке WPF привело бы к взаимоблокировке.
            Environment.Exit(Task.Run(SelfTest.Run).GetAwaiter().GetResult());
            return;
        }
        // Все необработанные ошибки — в crash.log: у клиента не будет отладчика, а журнал можно прислать.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                CrashLog.Write(ex, args.IsTerminating ? "AppDomain (fatal)" : "AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "Task");
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception, "UI");
        MessageBox.Show(
            e.Exception.Message + "\n\nПодробности записаны в журнал ошибок:\n" + AppPaths.CrashLog,
            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
