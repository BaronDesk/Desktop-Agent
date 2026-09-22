using System.Text.Json.Serialization;

namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>POLICY_UPDATE</c> payload: a partial update, only non-null fields change.
/// Unknown fields are rejected so a typo in a key is NACKed instead of silently ignored.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PolicyUpdatePayload
{
    public double? TelemetryCadenceSeconds { get; init; }

    public double? HeartbeatIntervalSeconds { get; init; }

    public double? DefaultLeaseDurationSeconds { get; init; }

    public double? LeaseGracePeriodSeconds { get; init; }

    public double? CpuTempAlertThreshold { get; init; }

    public double? GpuTempAlertThreshold { get; init; }

    public double? CpuLoadAlertThreshold { get; init; }

    public double? RamLoadAlertThreshold { get; init; }

    public double? HardwareAlertCooldownSeconds { get; init; }

    public double? UsbDebounceWindowSeconds { get; init; }

    public bool? EnableAntiTheftAlerts { get; init; }

    public bool? StopGameOnLock { get; init; }

    [JsonIgnore]
    public bool HasChanges =>
        TelemetryCadenceSeconds.HasValue || HeartbeatIntervalSeconds.HasValue ||
        DefaultLeaseDurationSeconds.HasValue || LeaseGracePeriodSeconds.HasValue ||
        CpuTempAlertThreshold.HasValue || GpuTempAlertThreshold.HasValue ||
        CpuLoadAlertThreshold.HasValue || RamLoadAlertThreshold.HasValue ||
        HardwareAlertCooldownSeconds.HasValue || UsbDebounceWindowSeconds.HasValue ||
        EnableAntiTheftAlerts.HasValue || StopGameOnLock.HasValue;
}
