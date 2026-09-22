using System.Text.Json;
using System.Text.Json.Serialization;
using BaronDesk.Shared.Models;

namespace BaronDesk.Shared.Contracts;

/// <summary>
/// Wire DTO for the POLICY_UPDATE command payload.
/// Supports partial updates: only non-null properties are applied to the active policy.
/// </summary>
public sealed class PolicyUpdatePayload
{
    [JsonPropertyName("telemetryCadenceSeconds")]
    public double? TelemetryCadenceSeconds { get; init; }

    [JsonPropertyName("heartbeatIntervalSeconds")]
    public double? HeartbeatIntervalSeconds { get; init; }

    [JsonPropertyName("defaultLeaseDurationSeconds")]
    public double? DefaultLeaseDurationSeconds { get; init; }

    [JsonPropertyName("leaseGracePeriodSeconds")]
    public double? LeaseGracePeriodSeconds { get; init; }

    [JsonPropertyName("cpuTempAlertThreshold")]
    public double? CpuTempAlertThreshold { get; init; }

    [JsonPropertyName("gpuTempAlertThreshold")]
    public double? GpuTempAlertThreshold { get; init; }

    [JsonPropertyName("cpuLoadAlertThreshold")]
    public double? CpuLoadAlertThreshold { get; init; }

    [JsonPropertyName("ramLoadAlertThreshold")]
    public double? RamLoadAlertThreshold { get; init; }

    [JsonPropertyName("hardwareAlertCooldownSeconds")]
    public double? HardwareAlertCooldownSeconds { get; init; }

    [JsonPropertyName("usbDebounceWindowSeconds")]
    public double? UsbDebounceWindowSeconds { get; init; }

    [JsonPropertyName("enableAntiTheftAlerts")]
    public bool? EnableAntiTheftAlerts { get; init; }

    /// <summary>
    /// Applies this payload's non-null updates onto an existing <see cref="StationPolicy"/>.
    /// Returns a new updated copy.
    /// </summary>
    public StationPolicy ApplyTo(StationPolicy current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var updated = current.Clone();

        if (TelemetryCadenceSeconds.HasValue)
        {
            updated.TelemetryCadenceSeconds = TelemetryCadenceSeconds.Value;
        }

        if (HeartbeatIntervalSeconds.HasValue)
        {
            updated.HeartbeatIntervalSeconds = HeartbeatIntervalSeconds.Value;
        }

        if (DefaultLeaseDurationSeconds.HasValue)
        {
            updated.DefaultLeaseDurationSeconds = DefaultLeaseDurationSeconds.Value;
        }

        if (LeaseGracePeriodSeconds.HasValue)
        {
            updated.LeaseGracePeriodSeconds = LeaseGracePeriodSeconds.Value;
        }

        if (CpuTempAlertThreshold.HasValue)
        {
            updated.CpuTempAlertThreshold = CpuTempAlertThreshold.Value;
        }

        if (GpuTempAlertThreshold.HasValue)
        {
            updated.GpuTempAlertThreshold = GpuTempAlertThreshold.Value;
        }

        if (CpuLoadAlertThreshold.HasValue)
        {
            updated.CpuLoadAlertThreshold = CpuLoadAlertThreshold.Value;
        }

        if (RamLoadAlertThreshold.HasValue)
        {
            updated.RamLoadAlertThreshold = RamLoadAlertThreshold.Value;
        }

        if (HardwareAlertCooldownSeconds.HasValue)
        {
            updated.HardwareAlertCooldownSeconds = HardwareAlertCooldownSeconds.Value;
        }

        if (UsbDebounceWindowSeconds.HasValue)
        {
            updated.UsbDebounceWindowSeconds = UsbDebounceWindowSeconds.Value;
        }

        if (EnableAntiTheftAlerts.HasValue)
        {
            updated.EnableAntiTheftAlerts = EnableAntiTheftAlerts.Value;
        }

        updated.UpdatedAt = DateTimeOffset.UtcNow;
        return updated;
    }

    /// <summary>
    /// Extracts a <see cref="PolicyUpdatePayload"/> from a polymorphic command payload object,
    /// robustly handling <see cref="JsonElement"/> with camelCase or snake_case property aliases.
    /// </summary>
    public static PolicyUpdatePayload FromPayload(object? payload)
    {
        if (payload is null)
        {
            return new PolicyUpdatePayload();
        }

        if (payload is PolicyUpdatePayload typed)
        {
            return typed;
        }

        if (payload is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            return new PolicyUpdatePayload
            {
                TelemetryCadenceSeconds = ReadDouble(je, "telemetryCadenceSeconds", "telemetry_cadence_seconds", "cadenceSeconds", "cadence_seconds"),
                HeartbeatIntervalSeconds = ReadDouble(je, "heartbeatIntervalSeconds", "heartbeat_interval_seconds", "heartbeatSeconds", "heartbeat_seconds"),
                DefaultLeaseDurationSeconds = ReadDouble(je, "defaultLeaseDurationSeconds", "default_lease_duration_seconds", "leaseDurationSeconds", "lease_duration_seconds"),
                LeaseGracePeriodSeconds = ReadDouble(je, "leaseGracePeriodSeconds", "lease_grace_period_seconds", "gracePeriodSeconds", "grace_period_seconds"),
                CpuTempAlertThreshold = ReadDouble(je, "cpuTempAlertThreshold", "cpu_temp_alert_threshold", "cpuTempThreshold", "cpu_temp_threshold"),
                GpuTempAlertThreshold = ReadDouble(je, "gpuTempAlertThreshold", "gpu_temp_alert_threshold", "gpuTempThreshold", "gpu_temp_threshold"),
                CpuLoadAlertThreshold = ReadDouble(je, "cpuLoadAlertThreshold", "cpu_load_alert_threshold", "cpuLoadThreshold", "cpu_load_threshold"),
                RamLoadAlertThreshold = ReadDouble(je, "ramLoadAlertThreshold", "ram_load_alert_threshold", "ramLoadThreshold", "ram_load_threshold"),
                HardwareAlertCooldownSeconds = ReadDouble(je, "hardwareAlertCooldownSeconds", "hardware_alert_cooldown_seconds", "alertCooldownSeconds", "alert_cooldown_seconds"),
                UsbDebounceWindowSeconds = ReadDouble(je, "usbDebounceWindowSeconds", "usb_debounce_window_seconds", "debounceSeconds", "debounce_seconds"),
                EnableAntiTheftAlerts = ReadBool(je, "enableAntiTheftAlerts", "enable_anti_theft_alerts", "enableAntiTheft", "enable_anti_theft")
            };
        }

        return new PolicyUpdatePayload();
    }

    private static double? ReadDouble(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var val))
                {
                    return val;
                }

                if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static bool? ReadBool(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return prop.GetBoolean();
                }

                if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }
}
