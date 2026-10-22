using Microsoft.Data.Sqlite;
using System.Globalization;
using VamSys.Core;
using static VamSys.Infrastructure.RowSerializer;
using static VamSys.Infrastructure.Sql;

namespace VamSys.Infrastructure;

// resource_metadata / resource_rows: column definitions, conflicts, snapshot and draft rows (history is HistoryRepository).
internal static class ResourceRowRepository
{
    public static void Write(SqliteConnection c, SqliteTransaction tx, FullPayload p)
    {
        Execute(c, tx, "DELETE FROM resource_metadata WHERE workspace_id=$w", ("$w", p.Id.ToString()));
        foreach (var r in p.Resources)
        {
            Execute(c, tx, "INSERT INTO resource_metadata VALUES($w,$r,$cols,$conflicts,$at)", ("$w", p.Id.ToString()), ("$r", (int)r.Kind), ("$cols", r.Columns), ("$conflicts", r.Conflicts), ("$at", r.SnapshotAt));
            HistoryRepository.Write(c, tx, p.Id, r.Kind, r.History, full: true);
            using var cmd = Command(c, tx, "INSERT INTO resource_rows VALUES($w,$r,$collection,$id,$n,$json)", ("$w", p.Id.ToString()), ("$r", (int)r.Kind), ("$collection", 0), ("$id", ""), ("$n", 0), ("$json", ""));
            cmd.Prepare();
            foreach (var row in r.Rows)
            {
                cmd.Parameters["$collection"].Value = row.Collection;
                cmd.Parameters["$id"].Value = row.Id.ToString();
                cmd.Parameters["$n"].Value = row.Ordinal;
                cmd.Parameters["$json"].Value = row.Json;
                cmd.ExecuteNonQuery();
            }
        }
    }

    public static void Read(SqliteConnection c, SqliteTransaction tx, Workspace w)
    {
        w.Resources = Enum.GetValues<ResourceKind>().ToDictionary(k => k, _ => new ResourceData());
        using (var cmd = Command(c, tx, "SELECT resource,columns_json,conflicts_json,snapshot_at FROM resource_metadata WHERE workspace_id=$w", ("$w", w.Id.ToString())))
        using (var r = cmd.ExecuteReader())
        while (r.Read())
        {
            var data = w.Resources[(ResourceKind)r.GetInt32(0)];
            data.Columns = Decode<List<string>>(r.GetString(1))!;
            data.Conflicts = Decode<List<MergeConflict>>(r.GetString(2))!;
            data.SnapshotAt = r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture);
        }
        using (var cmd = Command(c, tx, "SELECT resource,collection,json FROM resource_rows WHERE workspace_id=$w ORDER BY resource,collection,ordinal", ("$w", w.Id.ToString())))
        using (var r = cmd.ExecuteReader())
        while (r.Read())
        {
            var data = w.Resources[(ResourceKind)r.GetInt32(0)];
            var rows = r.GetInt32(1) == 0 ? data.Snapshot : data.Draft;
            rows.Add(Decode<DataRow>(r.GetString(2))!);
        }
        foreach (var (kind, data) in w.Resources)
            HistoryRepository.Read(c, tx, w.Id, kind, data);
    }

    // Rewrites the JSON of draft rows that keep their position; every row must exist exactly once.
    public static void UpdateDraftRows(SqliteConnection c, SqliteTransaction tx, string workspace, IEnumerable<(int Kind, string Id, string Json)> rows)
    {
        using var update = Command(c, tx, "UPDATE resource_rows SET json=$json WHERE workspace_id=$w AND resource=$r AND collection=1 AND local_id=$id", ("$w", workspace), ("$r", 0), ("$id", ""), ("$json", ""));
        update.Prepare();
        foreach (var row in rows)
        {
            update.Parameters["$r"].Value = row.Kind;
            update.Parameters["$id"].Value = row.Id;
            update.Parameters["$json"].Value = row.Json;
            if (update.ExecuteNonQuery() != 1)
                throw StorageErrors.Block("StorageInvalid");
        }
    }

    // One rebase: snapshot/draft row mutations, snapshot time, conflicts and optional history clearing for a single record.
    public static void WriteDelta(SqliteConnection c, SqliteTransaction tx, Guid workspaceId, ResourceRebaseDelta delta, string? snapshot, string? draft)
    {
        var w = workspaceId.ToString();
        var kind = (int)delta.Resource;
        if (Execute(c, tx, "UPDATE resource_metadata SET snapshot_at=$at WHERE workspace_id=$w AND resource=$r", ("$at", delta.SnapshotAt.ToString("O")), ("$w", w), ("$r", kind)) != 1)
            throw StorageErrors.Block("StorageInvalid");
        if (delta.Conflicts != null)
            Execute(c, tx, "UPDATE resource_metadata SET conflicts_json=$conflicts WHERE workspace_id=$w AND resource=$r", ("$conflicts", Encode(delta.Conflicts.ToList())), ("$w", w), ("$r", kind));
        void Row(int collection, RowMutation mutation, string? json)
        {
            if (mutation.Action == RowMutationKind.Keep)
                return;
            var args = new (string, object?)[] { ("$w", w), ("$r", kind), ("$c", collection), ("$id", delta.LocalId.ToString()) };
            var ordinal = Scalar(c, tx, "SELECT ordinal FROM resource_rows WHERE workspace_id=$w AND resource=$r AND collection=$c AND local_id=$id", args);
            if (mutation.Action == RowMutationKind.Delete || collection == 0)
                Execute(c, tx, "DELETE FROM resource_rows WHERE workspace_id=$w AND resource=$r AND collection=$c AND local_id=$id", args);
            if (mutation.Action == RowMutationKind.Delete)
                return;
            var position = collection == 1 && ordinal != null ? Convert.ToInt64(ordinal) : Convert.ToInt64(Scalar(c, tx, "SELECT COALESCE(MAX(ordinal),-1)+1 FROM resource_rows WHERE workspace_id=$w AND resource=$r AND collection=$c", args));
            Execute(c, tx, "INSERT INTO resource_rows VALUES($w,$r,$c,$id,$n,$json) ON CONFLICT(workspace_id,resource,collection,local_id) DO UPDATE SET json=$json", [.. args, ("$n", position), ("$json", json)]);
        }
        Row(0, delta.Snapshot, snapshot);
        Row(1, delta.Draft, draft);
        if (delta.ClearHistory)
            HistoryRepository.ClearSteps(c, tx, w, kind);
    }
}
