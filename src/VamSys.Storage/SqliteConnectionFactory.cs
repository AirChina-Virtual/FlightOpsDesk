using Microsoft.Data.Sqlite;

namespace VamSys.Infrastructure;

// The only place that knows how a connection to workspaces.db is configured.
internal sealed class SqliteConnectionFactory
{
    readonly string connectionString;
    public SqliteConnectionFactory(string directory)
    {
        DataDirectory = directory;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspaces.db") }.ToString();
    }
    public string DataDirectory { get; }
    public SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        Sql.Execute(c, null, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
        return c;
    }
    public static void EnableWal(SqliteConnection c) => Sql.Execute(c, null, "PRAGMA journal_mode=WAL");
    // A standalone, unpooled database used as the target of BackupDatabase.
    public static SqliteConnection OpenBackupTarget(string path)
    {
        var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        backup.Open();
        return backup;
    }
}
