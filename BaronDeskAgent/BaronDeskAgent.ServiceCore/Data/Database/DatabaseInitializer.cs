using Microsoft.Data.Sqlite;

namespace BaronDeskAgent.ServiceCore.Data.Database;

public sealed class DatabaseInitializer
{
    private readonly AgentDatabase _database;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        AgentDatabase database,
        ILogger<DatabaseInitializer> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            _database.CreateConnection();

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            PRAGMA journal_mode = WAL;

            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS OutboxMessages
            (
                Id TEXT PRIMARY KEY,

                Type TEXT NOT NULL,

                Payload TEXT NOT NULL,

                CreatedAt TEXT NOT NULL,

                Attempts INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS
                IX_OutboxMessages_CreatedAt
            ON OutboxMessages (CreatedAt);

            CREATE TABLE IF NOT EXISTS GameCatalog
            (
                GameId TEXT PRIMARY KEY,

                Name TEXT NOT NULL,

                ExecutablePath TEXT NOT NULL,

                LaunchArguments TEXT,

                WorkingDirectory TEXT,

                CreatedAt TEXT NOT NULL,

                UpdatedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS
                IX_GameCatalog_Name
            ON GameCatalog (Name);

            CREATE TABLE IF NOT EXISTS StationPolicy
            (
                Id TEXT PRIMARY KEY,
                TelemetryCadenceSeconds REAL NOT NULL,
                HeartbeatIntervalSeconds REAL NOT NULL,
                DefaultLeaseDurationSeconds REAL NOT NULL,
                LeaseGracePeriodSeconds REAL NOT NULL,
                CpuTempAlertThreshold REAL NOT NULL,
                GpuTempAlertThreshold REAL NOT NULL,
                CpuLoadAlertThreshold REAL NOT NULL,
                RamLoadAlertThreshold REAL NOT NULL,
                HardwareAlertCooldownSeconds REAL NOT NULL,
                UsbDebounceWindowSeconds REAL NOT NULL,
                EnableAntiTheftAlerts INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(
            cancellationToken);

        // Seed default test game if catalog is empty
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(1) FROM GameCatalog;";
        var count = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(cancellationToken));

        if (count == 0)
        {
            var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var notepadPath = System.IO.Path.Combine(systemRoot, "notepad.exe");
            var nowIso = DateTimeOffset.UtcNow.ToString("O");

            await using var seedCmd = connection.CreateCommand();
            seedCmd.CommandText =
                """
                INSERT INTO GameCatalog (GameId, Name, ExecutablePath, LaunchArguments, WorkingDirectory, CreatedAt, UpdatedAt)
                VALUES ('notepad', 'Notepad (Test Game)', $path, NULL, $workDir, $now, $now);
                """;
            seedCmd.Parameters.AddWithValue("$path", notepadPath);
            seedCmd.Parameters.AddWithValue("$workDir", systemRoot);
            seedCmd.Parameters.AddWithValue("$now", nowIso);
            await seedCmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Seeded default test game entry ('notepad') into GameCatalog.");
        }

        // Seed default active station policy if table is empty
        await using var checkPolicyCmd = connection.CreateCommand();
        checkPolicyCmd.CommandText = "SELECT COUNT(1) FROM StationPolicy WHERE Id = 'active';";
        var policyCount = Convert.ToInt64(await checkPolicyCmd.ExecuteScalarAsync(cancellationToken));

        if (policyCount == 0)
        {
            var nowIso = DateTimeOffset.UtcNow.ToString("O");
            await using var seedPolicyCmd = connection.CreateCommand();
            seedPolicyCmd.CommandText =
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
                    'active',
                    5.0,
                    15.0,
                    60.0,
                    10.0,
                    85.0,
                    85.0,
                    95.0,
                    95.0,
                    60.0,
                    5.0,
                    1,
                    $now
                );
                """;
            seedPolicyCmd.Parameters.AddWithValue("$now", nowIso);
            await seedPolicyCmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Seeded active default policy into StationPolicy table.");
        }

        _logger.LogInformation(
            "SQLite database initialized at {DatabasePath}.",
            DatabasePaths.DatabaseFile);
    }
}