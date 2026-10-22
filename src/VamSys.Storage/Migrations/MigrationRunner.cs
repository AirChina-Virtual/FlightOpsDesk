using Microsoft.Data.Sqlite;
using VamSys.Core;
using static VamSys.Infrastructure.Sql;

namespace VamSys.Infrastructure;

// The frame every upgrade runs in: refuse newer files; back up the existing database (BackupDatabase, so committed WAL
// content is included) and check it; then convert everything inside ONE transaction; write user_version last.
// Any failure before the commit rolls back to the untouched old format.
internal sealed class MigrationRunner(StorageContext context)
{
    // Path of the pre-upgrade backup, or null when nothing was upgraded.
    public string? Backup { get; private set; }

    public void Run(SqliteConnection c)
    {
        var version = Convert.ToInt32(Scalar(c, null, "PRAGMA user_version"));
        if (version > SchemaV4.Version)
            throw StorageErrors.Block("StorageNewer");
        if (version == SchemaV4.Version)
        { SqliteConnectionFactory.EnableWal(c); return; }
        var exists = Convert.ToInt32(Scalar(c, null, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='workspaces'")) > 0;
        if (exists)
            Backup = BackUp(c);
        context.Fault("Migration:BeforeTransaction");
        var migration = StorageMigration.For(version);
        using var tx = c.BeginTransaction();
        SchemaV4.DropTriggers(c, tx);
        if (exists)
            migration.UpgradeWorkspacesTable(c, tx);
        else
            SchemaV4.CreateWorkspacesTable(c, tx);
        SchemaV4.CreateCoreTables(c, tx);
        SchemaV4.CreateResourceTables(c, tx);
        var ids = new List<string>();
        using (var cmd = Command(c, tx, "SELECT id FROM workspaces"))
        using (var reader = cmd.ExecuteReader())
        while (reader.Read())
            ids.Add(reader.GetString(0));
        foreach (var id in ids)
        {
            var json = (string)Scalar(c, tx, "SELECT json FROM workspaces WHERE id=$id", ("$id", id))!;
            var w = Workspace.Deserialize(json);
            if (w.Id.ToString() != id)
                throw StorageErrors.Block("StorageInvalid");
            migration.MigrateWorkspace(c, tx, w, id);
        }
        SchemaV4.DropLegacyHistory(c, tx);
        SchemaV4.CreateTriggers(c, tx);
        context.Fault("Migration:BeforeCommit");
        Execute(c, tx, $"PRAGMA user_version={SchemaV4.Version}");
        tx.Commit();
        context.Fault("Migration:AfterCommit");
        SqliteConnectionFactory.EnableWal(c);
    }

    string BackUp(SqliteConnection source)
    {
        var path = Path.Combine(context.Connections.DataDirectory, "workspaces-before-v4-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".db");
        Backup = path; // Visible even if the integrity check below fails.
        using var backup = SqliteConnectionFactory.OpenBackupTarget(path);
        source.BackupDatabase(backup);
        if (!Equals(Scalar(backup, null, "PRAGMA integrity_check"), "ok"))
            throw StorageErrors.Block("StorageBackupFailed");
        return path;
    }
}
