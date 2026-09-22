using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Data.Entities;

/// <summary>
/// SQLite entity representing the persisted workstation policy row in table StationPolicy.
/// </summary>
public sealed class PolicyEntity
{
    public const string DefaultPolicyId = "active";

    public string Id { get; set; } = DefaultPolicyId;

    public double TelemetryCadenceSeconds { get; set; }

    public double HeartbeatIntervalSeconds { get; set; }

    public double DefaultLeaseDurationSeconds { get; set; }

    public double LeaseGracePeriodSeconds { get; set; }

    public double CpuTempAlertThreshold { get; set; }

    public double GpuTempAlertThreshold { get; set; }

    public double CpuLoadAlertThreshold { get; set; }

    public double RamLoadAlertThreshold { get; set; }

    public double HardwareAlertCooldownSeconds { get; set; }

    public double UsbDebounceWindowSeconds { get; set; }

    public bool EnableAntiTheftAlerts { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public StationPolicy ToModel()
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

    public static PolicyEntity FromModel(StationPolicy model, string id = DefaultPolicyId)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new PolicyEntity
        {
            Id = id,
            TelemetryCadenceSeconds = model.TelemetryCadenceSeconds,
            HeartbeatIntervalSeconds = model.HeartbeatIntervalSeconds,
            DefaultLeaseDurationSeconds = model.DefaultLeaseDurationSeconds,
            LeaseGracePeriodSeconds = model.LeaseGracePeriodSeconds,
            CpuTempAlertThreshold = model.CpuTempAlertThreshold,
            GpuTempAlertThreshold = model.GpuTempAlertThreshold,
            CpuLoadAlertThreshold = model.CpuLoadAlertThreshold,
            RamLoadAlertThreshold = model.RamLoadAlertThreshold,
            HardwareAlertCooldownSeconds = model.HardwareAlertCooldownSeconds,
            UsbDebounceWindowSeconds = model.UsbDebounceWindowSeconds,
            EnableAntiTheftAlerts = model.EnableAntiTheftAlerts,
            UpdatedAt = model.UpdatedAt
        };
    }
}
