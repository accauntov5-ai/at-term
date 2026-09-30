using System.Windows;
using System.Windows.Threading;
using MuxTerminal.App.Services;

namespace MuxTerminal.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
