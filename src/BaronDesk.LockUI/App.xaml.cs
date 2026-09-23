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
        MainWindow = new MainWindow(IsWindowedTestMode(e.Args));
        MainWindow.Show();
    }

    /// <summary>
    /// <c>--windowed</c>: run as a normal window, without the keyboard hook or the Task Manager policy, so the
    /// IPC and login flows can be tested while the terminals stay usable.
    /// </summary>
    /// <remarks>
    /// Debug builds only. In a Release build a gamer could otherwise start a windowed instance first, take the
    /// single-instance slot, and receive the service's "lock" commands in a window they can simply move away.
    /// </remarks>
    private static bool IsWindowedTestMode(string[] args)
    {
#if DEBUG
        return args.Contains("--windowed", StringComparer.OrdinalIgnoreCase);
#else
        return false;
#endif
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
