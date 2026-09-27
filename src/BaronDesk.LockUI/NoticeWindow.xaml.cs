using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BaronDesk.Shared.Ipc;

namespace BaronDesk.LockUI;

/// <summary>
/// Small always-on-top box in the bottom-right corner while the gamer plays: low balance, booking ending.
/// It never takes focus (no-activate tool window), so typing and the game keep working.
/// </summary>
/// <remarks>
/// Honest limit: a game in exclusive fullscreen draws over every window, so the notice only shows in windowed
/// or borderless mode. The station still locks on time: the notice is informational, the backend decides.
/// </remarks>
public partial class NoticeWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const double ScreenMargin = 16;

    private static readonly Brush LowBalanceBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush TimeLeftBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xD2, 0xFF));

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _deadline; // Stopwatch timestamp; 0 = no countdown

    public NoticeWindow()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => UpdateCountdown();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => PlaceInCorner();
    }

    public void ShowNotice(SessionNoticeKind kind, int? remainingSeconds, string? message)
    {
        if (kind == SessionNoticeKind.Clear)
        {
            Clear();
            return;
        }

        var lowBalance = kind == SessionNoticeKind.LowBalance;
        var accent = lowBalance ? LowBalanceBrush : TimeLeftBrush;
        BorderBrush = accent;
        TitleText.Foreground = accent;
        TitleText.Text = lowBalance ? "Low balance" : "Session ending soon";
        BodyText.Text = message ?? (lowBalance
            ? "Top up at the desk or in the BaronDesk app, or this station locks when the time below runs out."
            : "Your booked time is almost over. Ask at the desk to extend it.");

        _deadline = remainingSeconds is { } seconds
            ? Stopwatch.GetTimestamp() + (long)(seconds * (double)Stopwatch.Frequency)
            : 0;
        CountdownText.Visibility = _deadline == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateCountdown();

        if (_deadline != 0)
        {
            _tick.Start();
        }
        else
        {
            _tick.Stop();
        }

        Show();
        PlaceInCorner();
    }

    public void Clear()
    {
        _tick.Stop();
        _deadline = 0;
        Hide();
    }

    private void UpdateCountdown()
    {
        if (_deadline == 0)
        {
            return;
        }

        var left = Math.Max(0, (_deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
        var span = TimeSpan.FromSeconds(Math.Ceiling(left));
        CountdownText.Text = left <= 0
            ? "Time is up"
            : span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : span.ToString(@"m\:ss", CultureInfo.InvariantCulture) + " left";

        if (left <= 0)
        {
            _tick.Stop(); // stays up until the service locks the station
        }
    }

    private void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - ScreenMargin;
        Top = area.Bottom - ActualHeight - ScreenMargin;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
