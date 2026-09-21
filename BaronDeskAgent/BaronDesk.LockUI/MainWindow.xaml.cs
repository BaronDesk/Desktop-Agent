using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BaronDesk.LockUI.Hooks;
using BaronDesk.LockUI.Ipc;
using BaronDesk.Shared.Contracts;

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
        private readonly PipeClient _client;
        private bool _isLocked = true;
        private bool _hotKeyRegistered = false;

        public MainWindow()
        {
            InitializeComponent();

            _client = new PipeClient(
                onShowLock: id => Dispatcher.Invoke(() => ShowLockScreen(id)),
                onHideLock: id => Dispatcher.Invoke(() => HideLockScreen(id)),
                onPinResult: OnPinResult);

            _ = _client.ConnectAndListenAsync(_cts.Token);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyVirtualScreenBounds();
            ShowLockScreen(null);
        }

        private void ApplyVirtualScreenBounds()
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
        }

        private async void ShowLockScreen(Guid? commandId)
        {
            ApplyVirtualScreenBounds();
            Show();
            Activate();
            Topmost = true;

            _keyboardHook.Install();
            _isLocked = true;

            var helper = new WindowInteropHelper(this);
            if (!_hotKeyRegistered)
            {
                _hotKeyRegistered = RegisterHotKey(helper.Handle, HOTKEY_ID, MOD_WIN, 0);
            }

            StatusText.Text = string.Empty;
            PinBox.Password = string.Empty;
            PinBox.Focus();

            try
            {
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

            if (_hotKeyRegistered)
            {
                var helper = new WindowInteropHelper(this);
                UnregisterHotKey(helper.Handle, HOTKEY_ID);
                _hotKeyRegistered = false;
            }

            StatusText.Text = string.Empty;
            PinBox.Password = string.Empty;
            Hide();

            try
            {
                await _client.SendAsync(PipeMessageKind.LockHidden, commandId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SendAsync failed: {ex.Message}");
            }
        }

        private void OnUnlockClick(object sender, RoutedEventArgs e)
        {
            SubmitPin();
        }

        private void OnPinKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SubmitPin();
            }
        }

        private async void SubmitPin()
        {
            var pin = PinBox.Password?.Trim();
            if (string.IsNullOrWhiteSpace(pin))
            {
                StatusText.Text = "Please enter your booking PIN.";
                StatusText.Foreground = Brushes.OrangeRed;
                return;
            }

            StatusText.Text = "Verifying PIN...";
            StatusText.Foreground = Brushes.DeepSkyBlue;

            try
            {
                await _client.SubmitPinAsync(pin);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed to communicate with service.";
                StatusText.Foreground = Brushes.OrangeRed;
                System.Diagnostics.Debug.WriteLine($"SubmitPin failed: {ex.Message}");
            }
        }

        private void OnPinResult(bool success, string? payload)
        {
            Dispatcher.Invoke(() =>
            {
                if (success)
                {
                    StatusText.Text = "PIN verified! Unlocking...";
                    StatusText.Foreground = Brushes.LightGreen;
                    PinBox.Password = string.Empty;
                }
                else
                {
                    StatusText.Text = "Invalid PIN. Please try again.";
                    StatusText.Foreground = Brushes.OrangeRed;
                    PinBox.Password = string.Empty;
                    PinBox.Focus();
                }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts.Cancel();
            _keyboardHook.Dispose();
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
    }
}