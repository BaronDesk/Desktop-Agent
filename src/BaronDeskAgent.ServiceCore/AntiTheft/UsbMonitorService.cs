using System.Runtime.InteropServices;
using System.Threading.Channels;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Policy;
using BaronDeskAgent.ServiceCore.Session;
using BaronDeskAgent.ServiceCore.Telemetry;

namespace BaronDeskAgent.ServiceCore.AntiTheft;

/// <summary>
/// Best-effort peripheral anti-theft: raises an <c>anti_theft</c> alert when a venue USB HID device is
/// removed and not plugged back within the debounce window, and reports the connection status of every
/// watched device (<c>peripheral_status</c> on each change, and in <c>state_report</c> through <see cref="PeripheralRegistry"/>).
/// </summary>
/// <remarks>
/// Scope and honest limits (state these in the security document): wired USB HID only; wireless dongles,
/// Bluetooth and hubs/KVMs can hide a theft, and VID/PID identifies a model, not a unit.
/// The watched baseline is what was present at startup plus what is plugged in while the station is idle
/// (locked, no session). Devices a gamer plugs in during a session are theirs, so removing them is ignored.
/// The status is reported whether or not anti-theft alerts are enabled; only the alert waits for the debounce.
/// All state below is only touched by the single reader loop; native callbacks and timers just post events.
/// </remarks>
public sealed class UsbMonitorService : BackgroundService
{
    private readonly TelemetryPublisher _publisher;
    private readonly PeripheralRegistry _registry;
    private readonly StationController _station;
    private readonly IPolicyStore _policyStore;
    private readonly ServerClock _serverClock;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UsbMonitorService> _logger;

    private readonly Channel<UsbEvent> _events =
        Channel.CreateUnbounded<UsbEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Dictionary<string, UsbHidDevice> _present = new(StringComparer.OrdinalIgnoreCase);
    // Watched venue equipment by device key, with its last known name and connection state.
    private readonly Dictionary<string, WatchedPeripheral> _watched = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingRemoval> _pendingRemovals = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;

    // Kept in a field so the delegate handed to native code is never garbage collected.
    private readonly CmNotifyCallback _callback;
    private IntPtr _notificationHandle;

    public UsbMonitorService(
        TelemetryPublisher publisher,
        PeripheralRegistry registry,
        StationController station,
        IPolicyStore policyStore,
        ServerClock serverClock,
        TimeProvider timeProvider,
        ILogger<UsbMonitorService> logger)
    {
        _publisher = publisher;
        _registry = registry;
        _station = station;
        _policyStore = policyStore;
        _serverClock = serverClock;
        _timeProvider = timeProvider;
        _logger = logger;
        _callback = OnNativeDeviceEvent;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Register before the inventory, so a device that changes in between is not missed.
            RegisterNotifications();

            var now = _serverClock.UtcNow;
            foreach (var device in UsbDeviceEnumerator.GetPresentWiredHidDevices())
            {
                _present[device.InstanceId] = device;
                _watched.TryAdd(device.DeviceKey, new WatchedPeripheral(device, Connected: true, ChangedAt: now));
            }

            _logger.LogInformation("USB anti-theft watching {Count} HID device(s).", _watched.Count);
            await PublishStatusAsync(stoppingToken);

            await foreach (var usbEvent in _events.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await HandleAsync(usbEvent, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error handling USB event {Event}.", usbEvent);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USB anti-theft monitoring is unavailable.");
        }
        finally
        {
            UnregisterNotifications();
        }
    }

    private async Task HandleAsync(UsbEvent usbEvent, CancellationToken cancellationToken)
    {
        switch (usbEvent)
        {
            case DeviceArrived arrived:
                if (OnDeviceArrived(arrived.InstanceId))
                {
                    await PublishStatusAsync(cancellationToken);
                }

                break;

            case DeviceRemoved removed:
                if (OnDeviceRemoved(removed.InstanceId, cancellationToken))
                {
                    await PublishStatusAsync(cancellationToken);
                }

                break;

            case DebounceElapsed elapsed:
                await OnDebounceElapsedAsync(elapsed, cancellationToken);
                break;
        }
    }

    /// <summary>Returns true when the watched list changed (a watched device came back, or a new one joined).</summary>
    private bool OnDeviceArrived(string instanceId)
    {
        if (UsbDeviceEnumerator.TryGetWiredHidDevice(instanceId) is not { } device)
        {
            return false;
        }

        _present[instanceId] = device;

        if (_pendingRemovals.Remove(device.DeviceKey))
        {
            _logger.LogInformation("{Device} reconnected within the debounce window.", device.Name);
        }

        if (_watched.TryGetValue(device.DeviceKey, out var watched))
        {
            if (watched.Connected)
            {
                return false; // another interface of a device that is already connected
            }

            _watched[device.DeviceKey] = watched with { Connected = true, ChangedAt = _serverClock.UtcNow };
            _logger.LogInformation("{Device} ({Id}) is connected again.", device.Name, device.VendorProductId);
            return true;
        }

        var station = _station.GetSnapshot();
        if (station.Locked && station.SessionId is null)
        {
            _watched[device.DeviceKey] = new WatchedPeripheral(device, Connected: true, ChangedAt: _serverClock.UtcNow);
            _logger.LogInformation("{Device} ({Id}) added to the watched equipment.", device.Name, device.VendorProductId);
            return true;
        }

        return false;
    }

    /// <summary>Returns true when a watched device is now disconnected (all its interfaces are gone).</summary>
    private bool OnDeviceRemoved(string instanceId, CancellationToken cancellationToken)
    {
        if (!_present.Remove(instanceId, out var device) || !_watched.TryGetValue(device.DeviceKey, out var watched))
        {
            return false;
        }

        // A composite device raises one removal per interface; wait until all of them are gone.
        if (_present.Values.Any(other => string.Equals(other.DeviceKey, device.DeviceKey, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _watched[device.DeviceKey] = watched with { Connected = false, ChangedAt = _serverClock.UtcNow };

        var policy = _policyStore.CurrentPolicy;
        if (!policy.EnableAntiTheftAlerts)
        {
            return true;
        }

        var pending = new PendingRemoval(device, ++_generation, _serverClock.UtcNow);
        _pendingRemovals[device.DeviceKey] = pending;
        _ = PostWhenDebounceElapsesAsync(device.DeviceKey, pending.Generation, TimeSpan.FromSeconds(policy.UsbDebounceWindowSeconds), cancellationToken);
        return true;
    }

    /// <summary>Stores the snapshot for <c>state_report</c> and sends it live (dropped offline: the next reconnect carries it).</summary>
    private async Task PublishStatusAsync(CancellationToken cancellationToken)
    {
        var peripherals = _watched
            .Select(pair => new PeripheralState
            {
                DeviceId = pair.Key,
                Name = pair.Value.Device.Name,
                VendorProductId = pair.Value.Device.VendorProductId,
                Connected = pair.Value.Connected,
                ChangedAt = pair.Value.ChangedAt
            })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _registry.Set(peripherals);
        await _publisher.PublishPeripheralStatusAsync(peripherals, cancellationToken);
    }

    private async Task OnDebounceElapsedAsync(DebounceElapsed elapsed, CancellationToken cancellationToken)
    {
        // A reconnect removed the entry, or a newer removal replaced it: nothing to report for this timer.
        if (!_pendingRemovals.TryGetValue(elapsed.DeviceKey, out var pending) || pending.Generation != elapsed.Generation)
        {
            return;
        }

        _pendingRemovals.Remove(elapsed.DeviceKey);

        var device = pending.Device;
        _logger.LogWarning("Anti-theft: {Device} ({Id}) was removed and not reconnected.", device.Name, device.VendorProductId);

        await _publisher.PublishAlertAsync(
            new AlertPayload
            {
                Category = AlertCategories.AntiTheft,
                Type = AlertTypes.DeviceRemoved,
                Severity = AlertSeverities.Critical,
                Detail = $"Peripheral removed and not reconnected: {device.Name} ({device.VendorProductId}).",
                OccurredAt = pending.RemovedAt
            },
            cancellationToken);
    }

    private async Task PostWhenDebounceElapsesAsync(string deviceKey, long generation, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken);
            _events.Writer.TryWrite(new DebounceElapsed(deviceKey, generation));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RegisterNotifications()
    {
        var filter = new CmNotifyFilter
        {
            CbSize = (uint)Marshal.SizeOf<CmNotifyFilter>(),
            Flags = CmNotifyFilterFlagAllDeviceInstances,
            FilterType = CmNotifyFilterTypeDeviceInstance,
            Data = new byte[400]
        };

        var result = CM_Register_Notification(ref filter, IntPtr.Zero, _callback, out _notificationHandle);
        if (result != 0)
        {
            throw new InvalidOperationException($"CM_Register_Notification failed (CONFIGRET 0x{result:X8}).");
        }
    }

    private void UnregisterNotifications()
    {
        if (_notificationHandle != IntPtr.Zero)
        {
            // Blocks until in-flight callbacks have returned, so none can run after this point.
            var result = CM_Unregister_Notification(_notificationHandle);
            if (result != 0)
            {
                _logger.LogWarning("CM_Unregister_Notification failed (CONFIGRET 0x{Result:X8}).", result);
            }

            _notificationHandle = IntPtr.Zero;
        }

        _events.Writer.TryComplete();
    }

    /// <summary>Native callback (CfgMgr32 thread pool): only filters and posts, never blocks or throws.</summary>
    private uint OnNativeDeviceEvent(IntPtr notification, IntPtr context, CmNotifyAction action, IntPtr eventData, uint eventDataSize)
    {
        if (action is not (CmNotifyAction.DeviceInstanceStarted or CmNotifyAction.DeviceInstanceRemoved) ||
            eventData == IntPtr.Zero ||
            eventDataSize <= EventDataInstanceIdOffset)
        {
            return 0;
        }

        // CM_NOTIFY_EVENT_DATA: FilterType (4) + Reserved (4), then the NUL-terminated instance id.
        var instanceId = Marshal.PtrToStringUni(IntPtr.Add(eventData, EventDataInstanceIdOffset));
        if (string.IsNullOrEmpty(instanceId) || !UsbDeviceEnumerator.IsUsbInstance(instanceId))
        {
            return 0;
        }

        _events.Writer.TryWrite(action == CmNotifyAction.DeviceInstanceStarted
            ? new DeviceArrived(instanceId)
            : new DeviceRemoved(instanceId));
        return 0;
    }

    private abstract record UsbEvent;

    private sealed record DeviceArrived(string InstanceId) : UsbEvent;

    private sealed record DeviceRemoved(string InstanceId) : UsbEvent;

    private sealed record DebounceElapsed(string DeviceKey, long Generation) : UsbEvent;

    private sealed record PendingRemoval(UsbHidDevice Device, long Generation, DateTimeOffset RemovedAt);

    private sealed record WatchedPeripheral(UsbHidDevice Device, bool Connected, DateTimeOffset ChangedAt);

    #region Native

    private const int EventDataInstanceIdOffset = 8;
    private const uint CmNotifyFilterFlagAllDeviceInstances = 0x00000002;
    private const int CmNotifyFilterTypeDeviceInstance = 2;

    private enum CmNotifyAction
    {
        DeviceInstanceStarted = 8,
        DeviceInstanceRemoved = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CmNotifyFilter
    {
        public uint CbSize;
        public uint Flags;
        public int FilterType;
        public uint Reserved;

        // Union sized for DeviceInstance.InstanceId[MAX_DEVICE_ID_LEN] (200 WCHARs).
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 400)]
        public byte[] Data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CmNotifyCallback(IntPtr notification, IntPtr context, CmNotifyAction action, IntPtr eventData, uint eventDataSize);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Register_Notification(ref CmNotifyFilter filter, IntPtr context, CmNotifyCallback callback, out IntPtr notification);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Unregister_Notification(IntPtr notification);

    #endregion
}
