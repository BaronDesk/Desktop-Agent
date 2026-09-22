using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace BaronDesk.LockUI;

public partial class App : Application
{
    // One helper per user session: a relaunch by the service must not stack a second overlay.
    private const string SingleInstanceMutexName = @"Local\BaronDesk.LockUI";

    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        // A kiosk overlay must never crash to the desktop: log and keep running.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Trace.TraceError($"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Trace.TraceError($"Unhandled UI exception: {args.Exception}");
        args.Handled = true;
    }
}
