using Microsoft.Data.Sqlite;
using VamSys.Core;
using static VamSys.Infrastructure.RowSerializer;
using static VamSys.Infrastructure.Sql;

namespace VamSys.Infrastructure;

// Format 3: rows are per resource already; only the undo/redo history is one whole-draft snapshot per entry
// (resource_history). It becomes per-step diffs, and each conversion replays and validates the result.
internal sealed class V3Migration : StorageMigration
{
    public override void MigrateWorkspace(SqliteConnection c, SqliteTransaction tx, Workspace w, string id)
    {
        MigrateHistory(c, tx, w.Id);
        WorkspaceRepository.MarkFormat4(c, tx, id);
    }

    static void MigrateHistory(SqliteConnection c, SqliteTransaction tx, Guid workspace)
    {
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var args = new (string, object?)[] { ("$w", workspace.ToString()), ("$r", (int)kind) };
            var data = new ResourceData();
            using (var cmd = Command(c, tx, "SELECT local_id,json FROM resource_rows WHERE workspace_id=$w AND resource=$r AND collection=1 ORDER BY ordinal", args))
            using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var row = Decode<DataRow>(reader.GetString(1)) ?? throw StorageErrors.Block("StorageInvalid");
                if (row.LocalId.ToString() != reader.GetString(0))
                    throw StorageErrors.Block("StorageInvalid");
                data.Draft.Add(row);
            }
            using (var cmd = Command(c, tx, "SELECT undo_json,redo_json FROM resource_history WHERE workspace_id=$w AND resource=$r", args))
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read())
                    throw StorageErrors.Block("StorageInvalid");
                data.History = EditHistory.ConvertLegacy(data.Draft, Decode<List<List<DataRow>>>(reader.GetString(0)) ?? throw StorageErrors.Block("StorageInvalid"), Decode<List<List<DataRow>>>(reader.GetString(1)) ?? throw StorageErrors.Block("StorageInvalid"));
            }
            HistoryRepository.Write(c, tx, workspace, kind, CaptureHistory(data.History, full: true), full: true);
        }
    }
}
