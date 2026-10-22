using Microsoft.Data.Sqlite;
using VamSys.Core;

namespace VamSys.Infrastructure;

// Converts the workspaces of one older format to v4. The shared frame (MigrationRunner) owns the backup, the single
// transaction, the schema objects and the final user_version write; a migration only knows its own data.
internal abstract class StorageMigration
{
    // Brings an existing workspaces table to the shape v4 expects.
    public virtual void UpgradeWorkspacesTable(SqliteConnection c, SqliteTransaction tx) { }

    // The frame has already parsed the header JSON and checked that it belongs to this row. Anything thrown here
    // rolls back the whole upgrade.
    public abstract void MigrateWorkspace(SqliteConnection c, SqliteTransaction tx, Workspace workspace, string id);

    // Versions below 2 (and an empty database) are the old single-blob format.
    public static StorageMigration For(int version) => version switch
    {
        < 2 => new LegacyBlobMigration(),
        2 => new V2Migration(),
        3 => new V3Migration(),
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };
}
