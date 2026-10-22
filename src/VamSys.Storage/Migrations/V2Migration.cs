using Microsoft.Data.Sqlite;
using VamSys.Core;
using static VamSys.Infrastructure.RowSerializer;

namespace VamSys.Infrastructure;

// Format 2: jobs and revisions already live in their own tables; resources are still inside the workspace JSON.
internal sealed class V2Migration : StorageMigration
{
    public override void MigrateWorkspace(SqliteConnection c, SqliteTransaction tx, Workspace w, string id)
    {
        JobRepository.Read(c, tx, w);
        var payload = Capture(w, false);
        WorkspaceRepository.SetV4Header(c, tx, id, payload.Json);
        ResourceRowRepository.Write(c, tx, payload);
    }
}
