using Microsoft.Data.Sqlite;
using VamSys.Core;
using static VamSys.Infrastructure.RowSerializer;
using static VamSys.Infrastructure.Sql;

namespace VamSys.Infrastructure;

// Format 0/1: the whole workspace, jobs and resources included, lives in workspaces.json. Also used for a database that
// does not exist yet (nothing to convert).
internal sealed class LegacyBlobMigration : StorageMigration
{
    public override void UpgradeWorkspacesTable(SqliteConnection c, SqliteTransaction tx)
        => Execute(c, tx, "ALTER TABLE workspaces ADD COLUMN format_version INTEGER NOT NULL DEFAULT 4");

    public override void MigrateWorkspace(SqliteConnection c, SqliteTransaction tx, Workspace w, string id)
    {
        var payload = Capture(w, false);
        WorkspaceRepository.SetV4Header(c, tx, id, payload.Json);
        WorkspaceRepository.InsertInitialRevision(c, tx, id);
        JobRepository.Write(c, tx, payload);
        ResourceRowRepository.Write(c, tx, payload);
    }
}
