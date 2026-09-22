using BaronDeskAgent.ServiceCore.Data.Database;
using BaronDeskAgent.ServiceCore.Data.Entities;
using Microsoft.Data.Sqlite;

namespace BaronDeskAgent.ServiceCore.Data.Repositories;

public sealed class PolicyRepository
{
    private readonly AgentDatabase _database;

    public PolicyRepository(AgentDatabase database)
    {
        _database = database;
    }

    public async Task UpsertPolicyAsync(
        PolicyEntity entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO StationPolicy
            (
                Id,
                TelemetryCadenceSeconds,
                HeartbeatIntervalSeconds,
                DefaultLeaseDurationSeconds,
                LeaseGracePeriodSeconds,
                CpuTempAlertThreshold,
                GpuTempAlertThreshold,
                CpuLoadAlertThreshold,
                RamLoadAlertThreshold,
                HardwareAlertCooldownSeconds,
                UsbDebounceWindowSeconds,
                EnableAntiTheftAlerts,
                UpdatedAt
            )
            VALUES
            (
                $id,
                $telemetryCadence,
                $heartbeatInterval,
                $defaultLeaseDuration,
                $leaseGracePeriod,
                $cpuTempAlert,
                $gpuTempAlert,
                $cpuLoadAlert,
                $ramLoadAlert,
                $hardwareAlertCooldown,
                $usbDebounceWindow,
                $enableAntiTheft,
                $updatedAt
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
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.Parameters.AddWithValue("$id", entity.Id);
        command.Parameters.AddWithValue("$telemetryCadence", entity.TelemetryCadenceSeconds);
        command.Parameters.AddWithValue("$heartbeatInterval", entity.HeartbeatIntervalSeconds);
        command.Parameters.AddWithValue("$defaultLeaseDuration", entity.DefaultLeaseDurationSeconds);
        command.Parameters.AddWithValue("$leaseGracePeriod", entity.LeaseGracePeriodSeconds);
        command.Parameters.AddWithValue("$cpuTempAlert", entity.CpuTempAlertThreshold);
        command.Parameters.AddWithValue("$gpuTempAlert", entity.GpuTempAlertThreshold);
        command.Parameters.AddWithValue("$cpuLoadAlert", entity.CpuLoadAlertThreshold);
        command.Parameters.AddWithValue("$ramLoadAlert", entity.RamLoadAlertThreshold);
        command.Parameters.AddWithValue("$hardwareAlertCooldown", entity.HardwareAlertCooldownSeconds);
        command.Parameters.AddWithValue("$usbDebounceWindow", entity.UsbDebounceWindowSeconds);
        command.Parameters.AddWithValue("$enableAntiTheft", entity.EnableAntiTheftAlerts ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", entity.UpdatedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PolicyEntity?> GetActivePolicyAsync(
        string id = PolicyEntity.DefaultPolicyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                Id,
                TelemetryCadenceSeconds,
                HeartbeatIntervalSeconds,
                DefaultLeaseDurationSeconds,
                LeaseGracePeriodSeconds,
                CpuTempAlertThreshold,
                GpuTempAlertThreshold,
                CpuLoadAlertThreshold,
                RamLoadAlertThreshold,
                HardwareAlertCooldownSeconds,
                UsbDebounceWindowSeconds,
                EnableAntiTheftAlerts,
                UpdatedAt
            FROM StationPolicy
            WHERE Id = $id
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadEntity(reader);
    }

    private static PolicyEntity ReadEntity(SqliteDataReader reader)
    {
        return new PolicyEntity
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            TelemetryCadenceSeconds = reader.GetDouble(reader.GetOrdinal("TelemetryCadenceSeconds")),
            HeartbeatIntervalSeconds = reader.GetDouble(reader.GetOrdinal("HeartbeatIntervalSeconds")),
            DefaultLeaseDurationSeconds = reader.GetDouble(reader.GetOrdinal("DefaultLeaseDurationSeconds")),
            LeaseGracePeriodSeconds = reader.GetDouble(reader.GetOrdinal("LeaseGracePeriodSeconds")),
            CpuTempAlertThreshold = reader.GetDouble(reader.GetOrdinal("CpuTempAlertThreshold")),
            GpuTempAlertThreshold = reader.GetDouble(reader.GetOrdinal("GpuTempAlertThreshold")),
            CpuLoadAlertThreshold = reader.GetDouble(reader.GetOrdinal("CpuLoadAlertThreshold")),
            RamLoadAlertThreshold = reader.GetDouble(reader.GetOrdinal("RamLoadAlertThreshold")),
            HardwareAlertCooldownSeconds = reader.GetDouble(reader.GetOrdinal("HardwareAlertCooldownSeconds")),
            UsbDebounceWindowSeconds = reader.GetDouble(reader.GetOrdinal("UsbDebounceWindowSeconds")),
            EnableAntiTheftAlerts = reader.GetInt32(reader.GetOrdinal("EnableAntiTheftAlerts")) == 1,
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")))
        };
    }
}
