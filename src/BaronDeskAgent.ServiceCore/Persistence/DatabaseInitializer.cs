namespace BaronDeskAgent.ServiceCore.Persistence;

/// <summary>
/// Creates and upgrades the SQLite schema. Each migration runs once, in a transaction, and bumps
/// <c>PRAGMA user_version</c>; append new migrations, never edit shipped ones.
/// </summary>
public sealed class DatabaseInitializer
{
    private static readonly string[] Migrations =
    [
        // v1: the original schema. IF NOT EXISTS lets databases created before versioning adopt it.
        """
        CREATE TABLE IF NOT EXISTS OutboxMessages
        (
            Id TEXT PRIMARY KEY,
            Type TEXT NOT NULL,
            Payload TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            Attempts INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS IX_OutboxMessages_CreatedAt ON OutboxMessages (CreatedAt);

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
        CREATE INDEX IF NOT EXISTS IX_GameCatalog_Name ON GameCatalog (Name);

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
        """,

        // v2: review fixes.
        """
        ALTER TABLE StationPolicy ADD COLUMN StopGameOnLock INTEGER NOT NULL DEFAULT 1;

        -- Outbox rows used to hold whole envelopes with a seq/ts from an old connection, which the backend's
        -- anti-replay rejects. Rows now hold only the payload; the old ones cannot be replayed safely.
        DELETE FROM OutboxMessages;

        -- Persisted ids of successfully executed commands, so a redelivery is re-acknowledged, not re-executed.
        CREATE TABLE IF NOT EXISTS HandledCommands
        (
            Seq INTEGER PRIMARY KEY AUTOINCREMENT,
            CommandId TEXT NOT NULL UNIQUE,
            HandledAt TEXT NOT NULL
        );

        -- The Notepad test entry used to be seeded into every database; development builds seed it explicitly.
        DELETE FROM GameCatalog WHERE GameId = 'notepad';
        """,

        // v3: launcher-aware catalog. Existing rows are plain executables, which is the LaunchType default.
        """
        ALTER TABLE GameCatalog RENAME COLUMN ExecutablePath TO Target;
        ALTER TABLE GameCatalog ADD COLUMN LaunchType TEXT NOT NULL DEFAULT 'exe';
        ALTER TABLE GameCatalog ADD COLUMN ProcessName TEXT;
        """
    ];

    private readonly AgentDatabase _database;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(AgentDatabase database, ILogger<DatabaseInitializer> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);

        await using (var pragma = connection.CreateCommand())
        {
            // journal_mode cannot change inside a transaction.
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        long version;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "PRAGMA user_version;";
            version = (long)(await query.ExecuteScalarAsync(cancellationToken))!;
        }

        for (var index = (int)version; index < Migrations.Length; index++)
        {
            await using var transaction = connection.BeginTransaction();
            await using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = $"{Migrations[index]}\nPRAGMA user_version = {index + 1};";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation("Applied database migration v{Version}.", index + 1);
        }

        _logger.LogInformation("SQLite database ready (schema v{Version}).", Migrations.Length);
    }
}
