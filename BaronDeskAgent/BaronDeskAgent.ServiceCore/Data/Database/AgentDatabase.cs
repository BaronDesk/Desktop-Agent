using Microsoft.Data.Sqlite;

namespace BaronDeskAgent.ServiceCore.Data.Database;

public sealed class AgentDatabase
{
    public string ConnectionString { get; }

    public AgentDatabase()
    {
        Directory.CreateDirectory(
            DatabasePaths.DataDirectory);

        ConnectionString =
            new SqliteConnectionStringBuilder
            {
                DataSource =
                    DatabasePaths.DatabaseFile,

                Mode =
                    SqliteOpenMode.ReadWriteCreate,

                Cache =
                    SqliteCacheMode.Shared
            }.ToString();
    }

    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection(
            ConnectionString);
    }
}