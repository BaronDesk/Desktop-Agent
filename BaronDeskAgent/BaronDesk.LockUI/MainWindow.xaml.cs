using System.Windows;
using BaronDesk.LockUI.Hooks;
using BaronDesk.LockUI.Ipc;
using BaronDesk.Shared.Contracts;

namespace BaronDesk.LockUI
{
    public partial class MainWindow : Window
    {
        private readonly KeyboardHook _keyboardHook = new();
        private readonly CancellationTokenSource _cts = new();
        private PipeClient? _client;

        public MainWindow()
        {
            InitializeComponent();
            Hide();

            _client = new PipeClient(
                onShowLock: id => Dispatcher.Invoke(() => ShowLockScreen(id)),
                onHideLock: id => Dispatcher.Invoke(() => HideLockScreen(id)));

            _ = _client.ConnectAndListenAsync(_cts.Token);
        }

        private async void ShowLockScreen(Guid? commandId)
        {
            Show();
            Activate();
            _keyboardHook.Install();
            TaskManagerLock.Disable();

            if (_client is not null)
                await _client.SendAsync(PipeMessageKind.LockShown, commandId);
        }

        private async void HideLockScreen(Guid? commandId)
        {
            _keyboardHook.Dispose();
            TaskManagerLock.Enable();
            Hide();

            if (_client is not null)
                await _client.SendAsync(PipeMessageKind.LockHidden, commandId);
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts.Cancel();
            base.OnClosed(e);
        }
    }
}