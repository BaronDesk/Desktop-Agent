using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using BaronDesk.LockUI.Hooks;
using BaronDesk.LockUI.Ipc;
using BaronDesk.Shared.Contracts;
using System.Windows.Input;
namespace BaronDesk.LockUI
{
    public partial class MainWindow : Window
    {
        private const int MOD_WIN = 0x0008;
        private const int HOTKEY_ID = 9000;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        private readonly KeyboardHook _keyboardHook = new();
        private readonly CancellationTokenSource _cts = new();
        private PipeClient? _client;
        private bool _isLocked = false;
        public MainWindow()
        {
            InitializeComponent();
            this.KeyDown += MainWindow_keyDown;
            //Hide();

            _client = new PipeClient(
                onShowLock: id => Dispatcher.Invoke(() => ShowLockScreen(id)),
                onHideLock: id => Dispatcher.Invoke(() => HideLockScreen(id)));

            _ = _client.ConnectAndListenAsync(_cts.Token);
        }
        private void MainWindow_keyDown(object sender,KeyEventArgs e)
        {
            if (e.Key == Key.Q)
            {
                HideLockScreen(null);
            }
        }

        private async void ShowLockScreen(Guid? commandId)
        {
            Show();
            Activate();
            _keyboardHook.Install();
            System.Diagnostics.Debug.WriteLine("Hook installed.");
            _isLocked = true; 
            var helper = new WindowInteropHelper(this);
            RegisterHotKey(helper.Handle, HOTKEY_ID, MOD_WIN, 0);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                Dispatcher.Invoke(() => HideLockScreen(null));
            });

            try
            {
                if (_client is not null)
                await _client.SendAsync(PipeMessageKind.LockShown, commandId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SendAsync failed: {ex.Message}");
            }
        }

        private async void HideLockScreen(Guid? commandId)
        {
            _keyboardHook.Dispose();
            _isLocked = false;
            Hide();

            try
            {
                if (_client is not null)
                await _client.SendAsync(PipeMessageKind.LockHidden, commandId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SendAsync failed: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts.Cancel();
            base.OnClosed(e);
        }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isLocked)
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }
        private void TestShow_Click(object sender, RoutedEventArgs e) => ShowLockScreen(null);
        private void TestForceHide_Click(object sender, RoutedEventArgs e) => HideLockScreen(null);
    }
}