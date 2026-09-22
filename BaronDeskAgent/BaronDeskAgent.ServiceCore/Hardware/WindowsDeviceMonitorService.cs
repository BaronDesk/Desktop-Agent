using System.Runtime.InteropServices;
using System.Threading.Channels;

using BaronDesk.Shared.Contracts;
using BaronDesk.Shared.Models;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Services.Telemetry;

using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Hardware;

public sealed class WindowsDeviceMonitorService : BackgroundService
{
    private readonly Channel<DeviceNotification> _channel;

    private readonly TelemetryService _telemetryService;

    private readonly AgentOptions _options;

    private readonly ILogger<WindowsDeviceMonitorService> _logger;

    private readonly CMNotifyCallback _callback;

    private readonly Dictionary<
        string,
        WindowsDeviceEnumerator.WindowsDeviceInfo>
        _presentDevices =
            new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pending disconnect debounce timers keyed by canonical device key.
    /// If a device reconnects within the debounce window, the pending
    /// CTS is cancelled and the anti-theft alert is suppressed.
    /// </summary>
    private readonly Dictionary<
        string,
        PendingDisconnect>
        _pendingDisconnects =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lifecycleLock = new();

    private readonly ManualResetEventSlim _callbacksCompleted =
        new(initialState: true);

    private IntPtr _notificationHandle;

    private int _shutdownStarted;

    private int _activeCallbacks;

    public WindowsDeviceMonitorService(
        TelemetryService telemetryService,
        IOptions<AgentOptions> options,
        ILogger<WindowsDeviceMonitorService> logger)
    {
        _telemetryService = telemetryService;
        _options = options.Value;
        _logger = logger;

        _channel =
            Channel.CreateUnbounded<DeviceNotification>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });

        _callback = OnNativeDeviceEvent;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Starting device monitoring...");

        try
        {
            RegisterForDeviceNotifications();

            _logger.LogInformation(
                "Device monitoring started successfully.");

            InitializeCurrentDevices();

            await foreach (
                DeviceNotification notification
                in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessNotificationAsync(
                        notification,
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error processing device notification. " +
                        "Action={Action}, InstanceId={InstanceId}",
                        notification.Action,
                        notification.InstanceId);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Device monitoring failed.");
        }
        finally
        {
            CancelAllPendingDisconnects();

            ShutdownNativeNotifications();

            _logger.LogInformation(
                "Device monitoring stopped.");
        }
    }

    private void InitializeCurrentDevices()
    {
        IReadOnlyList<
            WindowsDeviceEnumerator.WindowsDeviceInfo>
            devices =
                WindowsDeviceEnumerator
                    .GetPresentUsbDevices();

        foreach (
            WindowsDeviceEnumerator.WindowsDeviceInfo device
            in devices)
        {
            if (!ShouldMonitor(device))
            {
                continue;
            }

            AddPresentDevice(device);
        }

        _logger.LogInformation(
            "Initial device inventory: {Count} devices.",
            _presentDevices.Count);

        foreach (
            WindowsDeviceEnumerator.WindowsDeviceInfo device
            in _presentDevices.Values)
        {
            _logger.LogInformation(
                "Device present | {DeviceName} | PID: {ProductId}",
                device.DeviceName,
                string.IsNullOrWhiteSpace(device.ProductId)
                    ? "N/A"
                    : device.ProductId);
        }
    }

    private async Task ProcessNotificationAsync(
        DeviceNotification notification,
        CancellationToken cancellationToken)
    {
        switch (notification.Action)
        {
            case CMNotifyAction.DeviceInstanceStarted:
                await HandleConnectedAsync(
                    notification.InstanceId,
                    cancellationToken);
                break;

            case CMNotifyAction.DeviceInstanceRemoved:
                await HandleDisconnectedAsync(
                    notification.InstanceId,
                    cancellationToken);
                break;
        }
    }

    private async Task HandleConnectedAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        if (!IsUsbInstance(instanceId))
        {
            return;
        }

        WindowsDeviceEnumerator.WindowsDeviceInfo? device =
            WindowsDeviceEnumerator.GetDeviceInfo(
                instanceId);

        if (device is null)
        {
            return;
        }

        if (!ShouldMonitor(device))
        {
            return;
        }

        string key =
            WindowsDeviceEnumerator.GetCanonicalDeviceKey(
                instanceId);

        // ── Cancel pending debounce if device reconnected ────
        if (_pendingDisconnects.TryGetValue(
                key,
                out PendingDisconnect? pending))
        {
            _pendingDisconnects.Remove(key);

            try
            {
                await pending.Cts.CancelAsync();
            }
            catch
            {
                // Already cancelled or disposed.
            }
            finally
            {
                pending.Cts.Dispose();
            }

            _logger.LogInformation(
                "USB flap resolved (reconnected within " +
                "debounce window) | {DeviceName} | " +
                "PID: {ProductId}",
                device.DeviceName,
                string.IsNullOrWhiteSpace(device.ProductId)
                    ? "N/A"
                    : device.ProductId);
        }

        if (_presentDevices.ContainsKey(key))
        {
            return;
        }

        _presentDevices[key] = device;

        var telemetry =
            new DeviceTelemetry
            {
                Timestamp = DateTimeOffset.UtcNow,
                DeviceType = "Device",
                DeviceName = device.DeviceName,
                ProductId = device.ProductId,
                EventType = "Connected"
            };

        await _telemetryService.PublishDeviceAsync(
            telemetry,
            cancellationToken);

        _logger.LogInformation(
            "Device connected | {DeviceName} | PID: {ProductId}",
            device.DeviceName,
            string.IsNullOrWhiteSpace(device.ProductId)
                ? "N/A"
                : device.ProductId);
    }

    private async Task HandleDisconnectedAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        if (!IsUsbInstance(instanceId))
        {
            return;
        }

        string key =
            WindowsDeviceEnumerator.GetCanonicalDeviceKey(
                instanceId);

        if (!_presentDevices.TryGetValue(
                key,
                out WindowsDeviceEnumerator.WindowsDeviceInfo? device))
        {
            return;
        }

        _presentDevices.Remove(key);

        var telemetry =
            new DeviceTelemetry
            {
                Timestamp = DateTimeOffset.UtcNow,
                DeviceType = "Device",
                DeviceName = device.DeviceName,
                ProductId = device.ProductId,
                EventType = "Disconnected"
            };

        await _telemetryService.PublishDeviceAsync(
            telemetry,
            cancellationToken);

        _logger.LogInformation(
            "Device disconnected | {DeviceName} | PID: {ProductId}",
            device.DeviceName,
            string.IsNullOrWhiteSpace(device.ProductId)
                ? "N/A"
                : device.ProductId);

        // ── Start debounce timer for anti-theft alert ────────
        if (!_options.EnableAntiTheftAlerts)
        {
            return;
        }

        // Cancel any pre-existing pending disconnect for
        // this key (shouldn't happen, but defensive).
        if (_pendingDisconnects.TryGetValue(
                key,
                out PendingDisconnect? existing))
        {
            _pendingDisconnects.Remove(key);

            try
            {
                await existing.Cts.CancelAsync();
            }
            catch
            {
            }
            finally
            {
                existing.Cts.Dispose();
            }
        }

        var cts = new CancellationTokenSource();

        var pendingDisconnect = new PendingDisconnect
        {
            DeviceName = device.DeviceName,
            ProductId = device.ProductId,
            DisconnectedAt = DateTimeOffset.UtcNow,
            Cts = cts
        };

        _pendingDisconnects[key] = pendingDisconnect;

        // Fire-and-forget the debounce timer.
        // The CTS lets HandleConnectedAsync cancel it.
        _ = RunDebounceTimerAsync(
            key,
            pendingDisconnect,
            cancellationToken);
    }

    private async Task RunDebounceTimerAsync(
        string deviceKey,
        PendingDisconnect pending,
        CancellationToken serviceStopping)
    {
        try
        {
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    pending.Cts.Token,
                    serviceStopping);

            await Task.Delay(
                TimeSpan.FromSeconds(
                    _options.UsbDebounceWindowSeconds),
                linked.Token);

            // Timer expired → device did NOT reconnect.
            // Remove from pending and emit anti-theft alert.
            _pendingDisconnects.Remove(deviceKey);

            var alert = new AlertPayload
            {
                Category = "anti_theft",
                Type = "HARDWARE_FAILURE",
                Severity = "CRITICAL",
                Detail =
                    $"Peripheral disconnected and not " +
                    $"restored: {pending.DeviceName} " +
                    $"(PID: {(string.IsNullOrWhiteSpace(pending.ProductId) ? "N/A" : pending.ProductId)}).",
                OccurredAt = pending.DisconnectedAt
            };

            await _telemetryService.PublishAlertAsync(
                alert,
                serviceStopping);

            _logger.LogWarning(
                "Anti-theft alert: Peripheral not restored " +
                "after {Window}s debounce | {DeviceName} | " +
                "PID: {ProductId}",
                _options.UsbDebounceWindowSeconds,
                pending.DeviceName,
                string.IsNullOrWhiteSpace(pending.ProductId)
                    ? "N/A"
                    : pending.ProductId);
        }
        catch (OperationCanceledException)
        {
            // Either the device reconnected (CTS cancelled)
            // or the service is shutting down. Both are normal.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error in USB debounce timer for " +
                "device key {DeviceKey}.",
                deviceKey);
        }
    }

    private void CancelAllPendingDisconnects()
    {
        foreach (PendingDisconnect pending
                 in _pendingDisconnects.Values)
        {
            try
            {
                pending.Cts.Cancel();
                pending.Cts.Dispose();
            }
            catch
            {
            }
        }

        _pendingDisconnects.Clear();
    }

    private void AddPresentDevice(
        WindowsDeviceEnumerator.WindowsDeviceInfo device)
    {
        string key =
            WindowsDeviceEnumerator.GetCanonicalDeviceKey(
                device.InstanceId);

        _presentDevices[key] = device;
    }

    private static bool IsUsbInstance(
        string instanceId)
    {
        return
            instanceId.StartsWith(
                "USB\\",
                StringComparison.OrdinalIgnoreCase)
            ||
            instanceId.StartsWith(
                "USBSTOR\\",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldMonitor(
        WindowsDeviceEnumerator.WindowsDeviceInfo device)
    {
        if (!IsUsbInstance(device.InstanceId))
        {
            return false;
        }

        if (device.InstanceId.Contains(
                "&MI_",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (device.DeviceName.Contains(
                "Hub USB racine",
                StringComparison.OrdinalIgnoreCase)
            ||
            device.DeviceName.Contains(
                "USB Root Hub",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void RegisterForDeviceNotifications()
    {
        lock (_lifecycleLock)
        {
            if (_notificationHandle != IntPtr.Zero)
            {
                return;
            }

            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                throw new InvalidOperationException(
                    "Cannot register device notifications after shutdown has started.");
            }

            var filter =
                new CMNotifyFilter
                {
                    CbSize =
                        (uint)Marshal.SizeOf<CMNotifyFilter>(),

                    Flags =
                        CMNotifyFilterFlags.AllDeviceInstances,

                    FilterType =
                        CMNotifyFilterType.DeviceInstance,

                    Reserved = 0,

                    Data = new byte[400]
                };

            uint result =
                CM_Register_Notification(
                    ref filter,
                    IntPtr.Zero,
                    _callback,
                    out IntPtr handle);

            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"CM_Register_Notification failed. " +
                    $"CONFIGRET: 0x{result:X8}");
            }

            _notificationHandle = handle;
        }
    }

    private void ShutdownNativeNotifications()
    {
        Interlocked.Exchange(
            ref _shutdownStarted,
            1);

        lock (_lifecycleLock)
        {
            if (_notificationHandle != IntPtr.Zero)
            {
                IntPtr handle =
                    _notificationHandle;

                _notificationHandle =
                    IntPtr.Zero;

                try
                {
                    uint result =
                        CM_Unregister_Notification(
                            handle);

                    if (result != 0)
                    {
                        _logger.LogWarning(
                            "CM_Unregister_Notification returned " +
                            "CONFIGRET: 0x{Result:X8}.",
                            result);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error unregistering device notifications.");
                }
            }
        }

        try
        {
            _callbacksCompleted.Wait(
                TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error waiting for native device callbacks to complete.");
        }

        _channel.Writer.TryComplete();
    }

    private uint OnNativeDeviceEvent(
        IntPtr notificationHandle,
        IntPtr context,
        CMNotifyAction action,
        IntPtr eventData,
        uint eventDataSize)
    {
        Interlocked.Increment(
            ref _activeCallbacks);

        _callbacksCompleted.Reset();

        try
        {
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return 0;
            }

            if (action !=
                    CMNotifyAction.DeviceInstanceStarted
                &&
                action !=
                    CMNotifyAction.DeviceInstanceRemoved)
            {
                return 0;
            }

            if (eventData == IntPtr.Zero ||
                eventDataSize < 8)
            {
                return 0;
            }

            string? instanceId =
                Marshal.PtrToStringUni(
                    IntPtr.Add(
                        eventData,
                        8));

            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return 0;
            }

            if (!IsUsbInstance(instanceId))
            {
                return 0;
            }

            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return 0;
            }

            _channel.Writer.TryWrite(
                new DeviceNotification
                {
                    Action = action,
                    InstanceId = instanceId
                });
        }
        catch (Exception ex)
        {
            try
            {
                _logger.LogError(
                    ex,
                    "Error processing native device notification.");
            }
            catch
            {
            }
        }
        finally
        {
            if (Interlocked.Decrement(
                    ref _activeCallbacks) == 0)
            {
                _callbacksCompleted.Set();
            }
        }

        return 0;
    }

    private sealed class DeviceNotification
    {
        public CMNotifyAction Action { get; init; }

        public string InstanceId { get; init; } =
            string.Empty;
    }

    private sealed class PendingDisconnect
    {
        public required string DeviceName { get; init; }

        public required string ProductId { get; init; }

        public required DateTimeOffset DisconnectedAt { get; init; }

        public required CancellationTokenSource Cts { get; init; }
    }

    private enum CMNotifyFilterType
    {
        DeviceInterface = 0,
        DeviceHandle = 1,
        DeviceInstance = 2
    }

    private enum CMNotifyAction
    {
        DeviceInterfaceArrival = 0,
        DeviceInterfaceRemoval = 1,
        DeviceQueryRemove = 2,
        DeviceQueryRemoveFailed = 3,
        DeviceRemovePending = 4,
        DeviceRemoveComplete = 5,
        DeviceCustomEvent = 6,
        DeviceInstanceEnumerated = 7,
        DeviceInstanceStarted = 8,
        DeviceInstanceRemoved = 9
    }

    private static class CMNotifyFilterFlags
    {
        public const uint AllDeviceInstances =
            0x00000002;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMNotifyFilter
    {
        public uint CbSize;

        public uint Flags;

        public CMNotifyFilterType FilterType;

        public uint Reserved;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 400,
            ArraySubType = UnmanagedType.U1)]
        public byte[] Data;
    }

    [UnmanagedFunctionPointer(
        CallingConvention.StdCall)]
    private delegate uint CMNotifyCallback(
        IntPtr notificationHandle,
        IntPtr context,
        CMNotifyAction action,
        IntPtr eventData,
        uint eventDataSize);

    [DllImport(
        "CfgMgr32.dll",
        CallingConvention = CallingConvention.Winapi)]
    private static extern uint CM_Register_Notification(
        ref CMNotifyFilter filter,
        IntPtr context,
        CMNotifyCallback callback,
        out IntPtr notificationHandle);

    [DllImport(
        "CfgMgr32.dll",
        CallingConvention = CallingConvention.Winapi)]
    private static extern uint CM_Unregister_Notification(
        IntPtr notificationHandle);
}