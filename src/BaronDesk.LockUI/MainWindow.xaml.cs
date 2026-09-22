using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BaronDesk.LockUI.Ipc;
using BaronDesk.LockUI.Kiosk;
using BaronDesk.Shared.Ipc;
using Microsoft.Win32;

namespace BaronDesk.LockUI;

/// <summary>
/// Fullscreen lock overlay. It has no authority: it shows what the service asks for and forwards the typed PIN,
/// which the service relays to the backend. It only acts on its own to fail closed.
/// </summary>
public partial class MainWindow : Window
{
    private const string UnavailableMessage = "Service unavailable — please ask the staff for help.";

    /// <summary>Without the service nothing enforces the lease, so an unlocked helper locks itself after this.</summary>
    private static readonly TimeSpan FailClosedAfterDisconnect = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan LoginResponseTimeout = TimeSpan.FromSeconds(20);

    private readonly KeyboardHook _keyboardHook = new();
    private readonly PipeClient _client = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _failClosedTimer;
    private readonly DispatcherTimer _loginTimeoutTimer;
    private readonly DispatcherTimer _rateLimitTimer;

    private bool _isLocked = true;
    private bool _pipeConnected;
    private bool _serverOnline;
    private bool _loginPending;
    private bool _rateLimited;

    public MainWindow()
    {
        InitializeComponent();

        _failClosedTimer = new DispatcherTimer { Interval = FailClosedAfterDisconnect };
        _failClosedTimer.Tick += OnFailClosedTimerTick;
        _loginTimeoutTimer = new DispatcherTimer { Interval = LoginResponseTimeout };
        _loginTimeoutTimer.Tick += OnLoginTimeoutTick;
        _rateLimitTimer = new DispatcherTimer();
        _rateLimitTimer.Tick += OnRateLimitElapsed;

        // Pipe events arrive on pool threads; BeginInvoke never blocks the pipe reader on the UI thread.
        _client.MessageReceived += message => Dispatcher.BeginInvoke(() => OnMessage(message));
        _client.ConnectionChanged += connected => Dispatcher.BeginInvoke(() => OnConnectionChanged(connected));

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Deactivated += OnDeactivated;

        _ = _client.RunAsync(_shutdown.Token);
    }

    private bool IsServiceAvailable => _pipeConnected && _serverOnline;

    private void OnLoaded(object sender, RoutedEventArgs e) => ShowLockScreen(correlationId: null);

    private void OnMessage(PipeMessage message)
    {
        switch (message.Kind)
        {
            case PipeMessageKind.ShowLock:
                ShowLockScreen(message.CorrelationId);
                break;
            case PipeMessageKind.HideLock:
                HideLockScreen(message.CorrelationId);
                break;
            case PipeMessageKind.LoginResult:
                OnLoginResult(message.LoginOutcome, message.Detail);
                break;
            case PipeMessageKind.ServerStatus:
                _serverOnline = message.ServerOnline == true;
                UpdateAvailability();
                break;
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        _pipeConnected = connected;

        if (connected)
        {
            _failClosedTimer.Stop();
        }
        else
        {
            _serverOnline = false;
            EndPendingLogin();
            if (!_isLocked)
            {
                _failClosedTimer.Start();
            }
        }

        UpdateAvailability();
    }

    private void OnFailClosedTimerTick(object? sender, EventArgs e)
    {
        _failClosedTimer.Stop();
        if (!_pipeConnected)
        {
            ShowLockScreen(correlationId: null);
        }
    }

    private void ShowLockScreen(Guid? correlationId)
    {
        ApplyVirtualScreenBounds();
        Show();
        Topmost = true;
        Activate();

        _keyboardHook.Install();
        TaskManagerPolicy.SetDisabled(true);
        _isLocked = true;

        PinBox.Password = string.Empty;
        PinBox.Focus();
        UpdateAvailability();

        _ = _client.SendAsync(new PipeMessage { Kind = PipeMessageKind.LockShown, CorrelationId = correlationId });
    }

    private void HideLockScreen(Guid? correlationId)
    {
        _keyboardHook.Uninstall();
        TaskManagerPolicy.SetDisabled(false);
        _isLocked = false;

        EndPendingLogin();
        PinBox.Password = string.Empty;
        SetStatus(string.Empty, Brushes.Gray);
        Hide();

        _ = _client.SendAsync(new PipeMessage { Kind = PipeMessageKind.LockHidden, CorrelationId = correlationId });
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e) => _ = SubmitPinAsync();

    private void OnPinKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = SubmitPinAsync();
        }
    }

    private async Task SubmitPinAsync()
    {
        if (_loginPending || _rateLimited)
        {
            return;
        }

        if (!IsServiceAvailable)
        {
            SetStatus(UnavailableMessage, Brushes.OrangeRed);
            return;
        }

        var pin = PinBox.Password.Trim();
        PinBox.Password = string.Empty;
        if (pin.Length == 0)
        {
            SetStatus("Please enter your booking PIN.", Brushes.OrangeRed);
            return;
        }

        _loginPending = true;
        _loginTimeoutTimer.Start();
        SetStatus("Checking your PIN…", Brushes.DeepSkyBlue);
        UpdateAvailability();

        if (!await _client.SendAsync(new PipeMessage { Kind = PipeMessageKind.SubmitCredential, Credential = pin }))
        {
            EndPendingLogin();
            SetStatus(UnavailableMessage, Brushes.OrangeRed);
            UpdateAvailability();
        }
    }

    private void OnLoginResult(LoginOutcome? outcome, string? detail)
    {
        EndPendingLogin();

        switch (outcome)
        {
            case LoginOutcome.Accepted:
                SetStatus("PIN accepted — unlocking…", Brushes.LightGreen);
                break;
            case LoginOutcome.Rejected:
                SetStatus(string.IsNullOrWhiteSpace(detail) ? "Incorrect PIN. Please try again." : detail, Brushes.OrangeRed);
                break;
            case LoginOutcome.RateLimited:
                var seconds = int.TryParse(detail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 30;
                _rateLimited = true;
                _rateLimitTimer.Interval = TimeSpan.FromSeconds(seconds);
                _rateLimitTimer.Start();
                SetStatus($"Too many attempts. Try again in {seconds}s.", Brushes.OrangeRed);
                break;
            case LoginOutcome.Timeout:
                SetStatus("The server did not answer. Please try again.", Brushes.OrangeRed);
                break;
            case LoginOutcome.Busy:
                SetStatus("Please wait…", Brushes.DeepSkyBlue);
                break;
            default:
                SetStatus(UnavailableMessage, Brushes.OrangeRed);
                break;
        }

        UpdateAvailability();
        PinBox.Focus();
    }

    private void OnLoginTimeoutTick(object? sender, EventArgs e)
    {
        EndPendingLogin();
        SetStatus("No answer from the service. Please try again.", Brushes.OrangeRed);
        UpdateAvailability();
    }

    private void OnRateLimitElapsed(object? sender, EventArgs e)
    {
        _rateLimitTimer.Stop();
        _rateLimited = false;
        SetStatus("You can try again.", Brushes.DeepSkyBlue);
        UpdateAvailability();
    }

    private void EndPendingLogin()
    {
        _loginPending = false;
        _loginTimeoutTimer.Stop();
    }

    private void UpdateAvailability()
    {
        PinBox.IsEnabled = IsServiceAvailable && !_rateLimited;
        UnlockButton.IsEnabled = IsServiceAvailable && !_rateLimited && !_loginPending;

        if (!IsServiceAvailable)
        {
            SetStatus(UnavailableMessage, Brushes.OrangeRed);
        }
        else if (StatusText.Text == UnavailableMessage)
        {
            SetStatus(string.Empty, Brushes.Gray);
        }
    }

    private void SetStatus(string text, Brush color)
    {
        StatusText.Text = text;
        StatusText.Foreground = color;
    }

    private void ApplyVirtualScreenBounds()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    // A monitor plugged in while locked must be covered too.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_isLocked)
            {
                ApplyVirtualScreenBounds();
            }
        });

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_isLocked)
        {
            Topmost = true;
            Activate();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isLocked)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _shutdown.Cancel();
        _keyboardHook.Dispose();
        TaskManagerPolicy.SetDisabled(false);
        base.OnClosed(e);
    }
}
