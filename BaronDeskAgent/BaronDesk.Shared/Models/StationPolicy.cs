namespace BaronDesk.Shared.Models;

/// <summary>
/// Domain model representing active workstation policy and runtime limits.
/// Stored locally in SQLite and adjustable live via POLICY_UPDATE commands.
/// </summary>
public sealed class StationPolicy
{
    /// <summary>
    /// Cadence in seconds at which the agent samples and streams hardware telemetry.
    /// </summary>
    public double TelemetryCadenceSeconds { get; set; } = 5.0;

    /// <summary>
    /// Cadence in seconds at which the agent sends presence heartbeats to the server.
    /// </summary>
    public double HeartbeatIntervalSeconds { get; set; } = 15.0;

    /// <summary>
    /// Default lease duration in seconds granted on unlock or heartbeat acknowledgement.
    /// </summary>
    public double DefaultLeaseDurationSeconds { get; set; } = 60.0;

    /// <summary>
    /// Grace window in seconds after lease expiration before enforcing fail-closed station lockout.
    /// </summary>
    public double LeaseGracePeriodSeconds { get; set; } = 10.0;

    /// <summary>
    /// CPU temperature threshold in Celsius for emitting hardware warning alerts.
    /// </summary>
    public double CpuTempAlertThreshold { get; set; } = 85.0;

    /// <summary>
    /// GPU temperature threshold in Celsius for emitting hardware warning alerts.
    /// </summary>
    public double GpuTempAlertThreshold { get; set; } = 85.0;

    /// <summary>
    /// CPU load percentage threshold for emitting hardware warning alerts.
    /// </summary>
    public double CpuLoadAlertThreshold { get; set; } = 95.0;

    /// <summary>
    /// RAM usage percentage threshold for emitting hardware warning alerts.
    /// </summary>
    public double RamLoadAlertThreshold { get; set; } = 95.0;

    /// <summary>
    /// Cooldown window in seconds before repeating an alert for the same hardware metric.
    /// </summary>
    public double HardwareAlertCooldownSeconds { get; set; } = 60.0;

    /// <summary>
    /// Debounce window in seconds for USB disconnects to distinguish temporary flaps from true removal.
    /// </summary>
    public double UsbDebounceWindowSeconds { get; set; } = 5.0;

    /// <summary>
    /// Whether anti-theft peripheral monitoring alerts are enabled.
    /// </summary>
    public bool EnableAntiTheftAlerts { get; set; } = true;

    /// <summary>
    /// Timestamp when this policy was last updated or loaded.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Creates a deep copy of the policy.
    /// </summary>
    public StationPolicy Clone()
    {
        return new StationPolicy
        {
            TelemetryCadenceSeconds = TelemetryCadenceSeconds,
            HeartbeatIntervalSeconds = HeartbeatIntervalSeconds,
            DefaultLeaseDurationSeconds = DefaultLeaseDurationSeconds,
            LeaseGracePeriodSeconds = LeaseGracePeriodSeconds,
            CpuTempAlertThreshold = CpuTempAlertThreshold,
            GpuTempAlertThreshold = GpuTempAlertThreshold,
            CpuLoadAlertThreshold = CpuLoadAlertThreshold,
            RamLoadAlertThreshold = RamLoadAlertThreshold,
            HardwareAlertCooldownSeconds = HardwareAlertCooldownSeconds,
            UsbDebounceWindowSeconds = UsbDebounceWindowSeconds,
            EnableAntiTheftAlerts = EnableAntiTheftAlerts,
            UpdatedAt = UpdatedAt
        };
    }

    /// <summary>
    /// Validates all policy settings against safe operational bounds.
    /// Returns a list of validation failure descriptions, or an empty list if valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (TelemetryCadenceSeconds is < 1.0 or > 300.0)
        {
            errors.Add($"TelemetryCadenceSeconds ({TelemetryCadenceSeconds}) must be between 1.0 and 300.0 seconds.");
        }

        if (HeartbeatIntervalSeconds is < 1.0 or > 120.0)
        {
            errors.Add($"HeartbeatIntervalSeconds ({HeartbeatIntervalSeconds}) must be between 1.0 and 120.0 seconds.");
        }

        if (DefaultLeaseDurationSeconds is < 5.0 or > 3600.0)
        {
            errors.Add($"DefaultLeaseDurationSeconds ({DefaultLeaseDurationSeconds}) must be between 5.0 and 3600.0 seconds.");
        }

        if (HeartbeatIntervalSeconds >= DefaultLeaseDurationSeconds)
        {
            errors.Add($"HeartbeatIntervalSeconds ({HeartbeatIntervalSeconds}) must be strictly less than DefaultLeaseDurationSeconds ({DefaultLeaseDurationSeconds}) to prevent spurious lease expirations.");
        }

        if (LeaseGracePeriodSeconds is < 1.0 or > 300.0)
        {
            errors.Add($"LeaseGracePeriodSeconds ({LeaseGracePeriodSeconds}) must be between 1.0 and 300.0 seconds.");
        }

        if (CpuTempAlertThreshold is < 30.0 or > 120.0)
        {
            errors.Add($"CpuTempAlertThreshold ({CpuTempAlertThreshold}) must be between 30.0°C and 120.0°C.");
        }

        if (GpuTempAlertThreshold is < 30.0 or > 120.0)
        {
            errors.Add($"GpuTempAlertThreshold ({GpuTempAlertThreshold}) must be between 30.0°C and 120.0°C.");
        }

        if (CpuLoadAlertThreshold is < 10.0 or > 100.0)
        {
            errors.Add($"CpuLoadAlertThreshold ({CpuLoadAlertThreshold}) must be between 10.0% and 100.0%.");
        }

        if (RamLoadAlertThreshold is < 10.0 or > 100.0)
        {
            errors.Add($"RamLoadAlertThreshold ({RamLoadAlertThreshold}) must be between 10.0% and 100.0%.");
        }

        if (HardwareAlertCooldownSeconds is < 1.0 or > 600.0)
        {
            errors.Add($"HardwareAlertCooldownSeconds ({HardwareAlertCooldownSeconds}) must be between 1.0 and 600.0 seconds.");
        }

        if (UsbDebounceWindowSeconds is < 0.5 or > 60.0)
        {
            errors.Add($"UsbDebounceWindowSeconds ({UsbDebounceWindowSeconds}) must be between 0.5 and 60.0 seconds.");
        }

        return errors;
    }
}
