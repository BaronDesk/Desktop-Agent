using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Policy;

/// <summary>
/// Runtime limits tuned by the backend through <c>POLICY_UPDATE</c> and persisted in SQLite.
/// The property initializers are the single source of defaults (used before the first update).
/// OPEN (skill §15 item 10): the values below are the documented defaults, still being tuned.
/// </summary>
public sealed record StationPolicy
{
    public double TelemetryCadenceSeconds { get; init; } = 5.0;

    public double HeartbeatIntervalSeconds { get; init; } = 15.0;

    /// <summary>Lease granted when the backend does not specify one, and the upper bound of any lease.</summary>
    public double DefaultLeaseDurationSeconds { get; init; } = 60.0;

    /// <summary>How long past lease expiry the station stays unlocked before failing closed.</summary>
    public double LeaseGracePeriodSeconds { get; init; } = 10.0;

    public double CpuTempAlertThreshold { get; init; } = 85.0;

    public double GpuTempAlertThreshold { get; init; } = 85.0;

    public double CpuLoadAlertThreshold { get; init; } = 95.0;

    public double RamLoadAlertThreshold { get; init; } = 95.0;

    public double HardwareAlertCooldownSeconds { get; init; } = 60.0;

    public double UsbDebounceWindowSeconds { get; init; } = 5.0;

    public bool EnableAntiTheftAlerts { get; init; } = true;

    /// <summary>
    /// Terminate the running game on <c>LOCK</c>. The overlay cannot cover a game running in exclusive
    /// fullscreen, so without this a billing run-out lock would not actually stop play.
    /// </summary>
    public bool StopGameOnLock { get; init; } = true;

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Returns a copy with every non-null field of <paramref name="update"/> applied.</summary>
    public StationPolicy Apply(PolicyUpdatePayload update, DateTimeOffset updatedAt) => this with
    {
        TelemetryCadenceSeconds = update.TelemetryCadenceSeconds ?? TelemetryCadenceSeconds,
        HeartbeatIntervalSeconds = update.HeartbeatIntervalSeconds ?? HeartbeatIntervalSeconds,
        DefaultLeaseDurationSeconds = update.DefaultLeaseDurationSeconds ?? DefaultLeaseDurationSeconds,
        LeaseGracePeriodSeconds = update.LeaseGracePeriodSeconds ?? LeaseGracePeriodSeconds,
        CpuTempAlertThreshold = update.CpuTempAlertThreshold ?? CpuTempAlertThreshold,
        GpuTempAlertThreshold = update.GpuTempAlertThreshold ?? GpuTempAlertThreshold,
        CpuLoadAlertThreshold = update.CpuLoadAlertThreshold ?? CpuLoadAlertThreshold,
        RamLoadAlertThreshold = update.RamLoadAlertThreshold ?? RamLoadAlertThreshold,
        HardwareAlertCooldownSeconds = update.HardwareAlertCooldownSeconds ?? HardwareAlertCooldownSeconds,
        UsbDebounceWindowSeconds = update.UsbDebounceWindowSeconds ?? UsbDebounceWindowSeconds,
        EnableAntiTheftAlerts = update.EnableAntiTheftAlerts ?? EnableAntiTheftAlerts,
        StopGameOnLock = update.StopGameOnLock ?? StopGameOnLock,
        UpdatedAt = updatedAt
    };

    /// <summary>Checks every value against safe operating bounds; returns the violations (empty when valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        RequireRange(errors, nameof(TelemetryCadenceSeconds), TelemetryCadenceSeconds, 1, 300);
        RequireRange(errors, nameof(HeartbeatIntervalSeconds), HeartbeatIntervalSeconds, 1, 120);
        RequireRange(errors, nameof(DefaultLeaseDurationSeconds), DefaultLeaseDurationSeconds, 5, 3600);
        RequireRange(errors, nameof(LeaseGracePeriodSeconds), LeaseGracePeriodSeconds, 1, 300);
        RequireRange(errors, nameof(CpuTempAlertThreshold), CpuTempAlertThreshold, 30, 120);
        RequireRange(errors, nameof(GpuTempAlertThreshold), GpuTempAlertThreshold, 30, 120);
        RequireRange(errors, nameof(CpuLoadAlertThreshold), CpuLoadAlertThreshold, 10, 100);
        RequireRange(errors, nameof(RamLoadAlertThreshold), RamLoadAlertThreshold, 10, 100);
        RequireRange(errors, nameof(HardwareAlertCooldownSeconds), HardwareAlertCooldownSeconds, 1, 600);
        RequireRange(errors, nameof(UsbDebounceWindowSeconds), UsbDebounceWindowSeconds, 0.5, 60);

        // A heartbeat must land well inside the lease, or every session would lock between two heartbeats.
        if (HeartbeatIntervalSeconds * 2 > DefaultLeaseDurationSeconds)
        {
            errors.Add(
                $"{nameof(HeartbeatIntervalSeconds)} ({HeartbeatIntervalSeconds}) must be at most half of " +
                $"{nameof(DefaultLeaseDurationSeconds)} ({DefaultLeaseDurationSeconds}).");
        }

        return errors;
    }

    private static void RequireRange(List<string> errors, string name, double value, double min, double max)
    {
        // Written so NaN fails too: every comparison with NaN is false.
        if (!(double.IsFinite(value) && value >= min && value <= max))
        {
            errors.Add($"{name} ({value}) must be between {min} and {max}.");
        }
    }
}
