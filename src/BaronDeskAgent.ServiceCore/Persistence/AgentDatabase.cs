using Microsoft.Data.Sqlite;

namespace BaronDeskAgent.ServiceCore.Persistence;

/// <summary>
/// Connection factory for the local SQLite store (outbox, policy, game catalog, handled command ids).
/// Never holds secrets or passwords. Connections are pooled by Microsoft.Data.Sqlite.
/// </summary>
public sealed class AgentDatabase
{
    public AgentDatabase(ILogger<AgentDatabase> logger)
        : this(AgentPaths.DatabaseFile)
    {
        SecureDataDirectory.Prepare(AgentPaths.RootDirectory, AgentPaths.DataDirectory, logger);
    }

    /// <summary>Opens an arbitrary database file (tests).</summary>
    internal AgentDatabase(string databaseFile)
    {
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public string ConnectionString { get; }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
