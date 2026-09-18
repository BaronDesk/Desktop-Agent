using System.Runtime.InteropServices;
using System.Threading.Channels;

using BaronDesk.Shared.Models;

using BaronDeskAgent.ServiceCore.Services;

namespace BaronDeskAgent.ServiceCore.Hardware;

public sealed class WindowsDeviceMonitorService : BackgroundService
{
    private readonly Channel<DeviceNotification> _channel;

    private readonly TelemetryService _telemetryService;

    private readonly ILogger<WindowsDeviceMonitorService> _logger;

    private readonly CMNotifyCallback _callback;

    private readonly Dictionary<
        string,
        WindowsDeviceEnumerator.WindowsDeviceInfo>
        _presentDevices =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lifecycleLock = new();

    private readonly ManualResetEventSlim _callbacksCompleted =
        new(initialState: true);

    private IntPtr _notificationHandle;

    private int _shutdownStarted;

    private int _activeCallbacks;

    public WindowsDeviceMonitorService(
        TelemetryService telemetryService,
        ILogger<WindowsDeviceMonitorService> logger)
    {
        _telemetryService = telemetryService;
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

        if (_presentDevices.ContainsKey(key))
        {
            return;
        }

        _presentDevices[key] = device;

        var telemetry =
            new DeviceTelemetry
            {
                Timestamp = DateTime.UtcNow,
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
                Timestamp = DateTime.UtcNow,
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