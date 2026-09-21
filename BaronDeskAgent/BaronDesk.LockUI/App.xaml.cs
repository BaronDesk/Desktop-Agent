using System.Windows;

namespace BaronDesk.LockUI
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, args) =>
            {
                System.Diagnostics.Debug.WriteLine($"Unhandled: {args.Exception}");
                args.Handled = true;
            };
        }
    }
}