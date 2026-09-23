using System.Globalization;
using BaronDeskAgent.ServiceCore.Persistence;
using Microsoft.Data.Sqlite;

namespace BaronDeskAgent.ServiceCore.Policy;

/// <summary>
/// Persists the single active <see cref="StationPolicy"/> row (<c>Id = 'active'</c>).
/// </summary>
public sealed class PolicyRepository
{
    private const string ActivePolicyId = "active";

    private readonly AgentDatabase _database;

    public PolicyRepository(AgentDatabase database)
    {
        _database = database;
    }

    public async Task<StationPolicy?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TelemetryCadenceSeconds, HeartbeatIntervalSeconds, DefaultLeaseDurationSeconds,
                   LeaseGracePeriodSeconds, CpuTempAlertThreshold, GpuTempAlertThreshold,
                   CpuLoadAlertThreshold, RamLoadAlertThreshold, HardwareAlertCooldownSeconds,
                   UsbDebounceWindowSeconds, EnableAntiTheftAlerts, StopGameOnLock, UpdatedAt
            FROM StationPolicy
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", ActivePolicyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StationPolicy
        {
            TelemetryCadenceSeconds = reader.GetDouble(0),
            HeartbeatIntervalSeconds = reader.GetDouble(1),
            DefaultLeaseDurationSeconds = reader.GetDouble(2),
            LeaseGracePeriodSeconds = reader.GetDouble(3),
            CpuTempAlertThreshold = reader.GetDouble(4),
            GpuTempAlertThreshold = reader.GetDouble(5),
            CpuLoadAlertThreshold = reader.GetDouble(6),
            RamLoadAlertThreshold = reader.GetDouble(7),
            HardwareAlertCooldownSeconds = reader.GetDouble(8),
            UsbDebounceWindowSeconds = reader.GetDouble(9),
            EnableAntiTheftAlerts = reader.GetInt64(10) != 0,
            StopGameOnLock = reader.GetInt64(11) != 0,
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    public async Task UpsertAsync(StationPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO StationPolicy
            (
                Id, TelemetryCadenceSeconds, HeartbeatIntervalSeconds, DefaultLeaseDurationSeconds,
                LeaseGracePeriodSeconds, CpuTempAlertThreshold, GpuTempAlertThreshold,
                CpuLoadAlertThreshold, RamLoadAlertThreshold, HardwareAlertCooldownSeconds,
                UsbDebounceWindowSeconds, EnableAntiTheftAlerts, StopGameOnLock, UpdatedAt
            )
            VALUES
            (
                $id, $telemetryCadence, $heartbeatInterval, $leaseDuration,
                $leaseGrace, $cpuTemp, $gpuTemp,
                $cpuLoad, $ramLoad, $alertCooldown,
                $usbDebounce, $antiTheft, $stopGameOnLock, $updatedAt
            )
            ON CONFLICT(Id) DO UPDATE SET
                TelemetryCadenceSeconds = excluded.TelemetryCadenceSeconds,
                HeartbeatIntervalSeconds = excluded.HeartbeatIntervalSeconds,
                DefaultLeaseDurationSeconds = excluded.DefaultLeaseDurationSeconds,
                LeaseGracePeriodSeconds = excluded.LeaseGracePeriodSeconds,
                CpuTempAlertThreshold = excluded.CpuTempAlertThreshold,
                GpuTempAlertThreshold = excluded.GpuTempAlertThreshold,
                CpuLoadAlertThreshold = excluded.CpuLoadAlertThreshold,
                RamLoadAlertThreshold = excluded.RamLoadAlertThreshold,
                HardwareAlertCooldownSeconds = excluded.HardwareAlertCooldownSeconds,
                UsbDebounceWindowSeconds = excluded.UsbDebounceWindowSeconds,
                EnableAntiTheftAlerts = excluded.EnableAntiTheftAlerts,
                StopGameOnLock = excluded.StopGameOnLock,
                UpdatedAt = excluded.UpdatedAt;
            """;

        AddParameters(command, policy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(SqliteCommand command, StationPolicy policy)
    {
        command.Parameters.AddWithValue("$id", ActivePolicyId);
        command.Parameters.AddWithValue("$telemetryCadence", policy.TelemetryCadenceSeconds);
        command.Parameters.AddWithValue("$heartbeatInterval", policy.HeartbeatIntervalSeconds);
        command.Parameters.AddWithValue("$leaseDuration", policy.DefaultLeaseDurationSeconds);
        command.Parameters.AddWithValue("$leaseGrace", policy.LeaseGracePeriodSeconds);
        command.Parameters.AddWithValue("$cpuTemp", policy.CpuTempAlertThreshold);
        command.Parameters.AddWithValue("$gpuTemp", policy.GpuTempAlertThreshold);
        command.Parameters.AddWithValue("$cpuLoad", policy.CpuLoadAlertThreshold);
        command.Parameters.AddWithValue("$ramLoad", policy.RamLoadAlertThreshold);
        command.Parameters.AddWithValue("$alertCooldown", policy.HardwareAlertCooldownSeconds);
        command.Parameters.AddWithValue("$usbDebounce", policy.UsbDebounceWindowSeconds);
        command.Parameters.AddWithValue("$antiTheft", policy.EnableAntiTheftAlerts ? 1 : 0);
        command.Parameters.AddWithValue("$stopGameOnLock", policy.StopGameOnLock ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", policy.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
    }
}
