using Microsoft.Data.Sqlite;
using System.Globalization;
using VamSys.Core;
using static VamSys.Infrastructure.RowSerializer;
using static VamSys.Infrastructure.Sql;

namespace VamSys.Infrastructure;

// resource_metadata / resource_rows: column definitions, conflicts, snapshot and draft rows (history is HistoryRepository).
internal static class ResourceRowRepository
{
    // How often a read reports its progress: often enough for a smooth count, rarely enough to cost nothing.
    const int ProgressRows = 2000;

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

    // Returns the resources whose stored positions are not 0, 1, 2, ... in either list (a task checkpoint or an older
    // program left gaps); the journal assumes positions equal list indexes, so those resources are rewritten once.
    public static HashSet<ResourceKind> Read(SqliteConnection c, SqliteTransaction tx, Workspace w, IWorkspaceLoadObserver? observer = null)
    {
        var gaps = new HashSet<ResourceKind>();
        var expected = new Dictionary<(int Resource, int Collection), int>();
        observer?.Stage(WorkspaceLoadStage.Metadata);
        w.Resources = Enum.GetValues<ResourceKind>().ToDictionary(k => k, _ => new ResourceData());
        // Only for the progress shown while reading: how many rows there are in all.
        var total = observer == null ? 0 : Convert.ToInt32(Scalar(c, tx, "SELECT count(*) FROM resource_rows WHERE workspace_id=$w", ("$w", w.Id.ToString())));
        using (var cmd = Command(c, tx, "SELECT resource,columns_json,conflicts_json,snapshot_at FROM resource_metadata WHERE workspace_id=$w", ("$w", w.Id.ToString())))
        using (var r = cmd.ExecuteReader())
        while (r.Read())
        {
            var data = w.Resources[(ResourceKind)r.GetInt32(0)];
            data.Columns = Decode<List<string>>(r.GetString(1))!;
            data.Conflicts = Decode<List<MergeConflict>>(r.GetString(2))!;
            data.SnapshotAt = r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture);
        }
        // Unknown (imported) field names are shared among the rows of this read only.
        using var names = FieldNames.BeginScope();
        observer?.Stage(WorkspaceLoadStage.Rows);
        var read = 0;
        using (var cmd = Command(c, tx, "SELECT resource,collection,json,ordinal FROM resource_rows WHERE workspace_id=$w ORDER BY resource,collection,ordinal", ("$w", w.Id.ToString())))
        using (var r = cmd.ExecuteReader())
        while (r.Read())
        {
            var data = w.Resources[(ResourceKind)r.GetInt32(0)];
            var rows = r.GetInt32(1) == 0 ? data.Snapshot : data.Draft;
            var list = (r.GetInt32(0), r.GetInt32(1));
            var position = expected.GetValueOrDefault(list);
            if (r.GetInt32(3) != position)
                gaps.Add((ResourceKind)r.GetInt32(0));
            expected[list] = position + 1;
            rows.Add(Decode<DataRow>(r.GetString(2))!);
            if (observer != null && ++read % ProgressRows == 0)
                observer.Rows(read, total);
        }
        observer?.Rows(read, total);
        // Before history validation and before the caller records the journal baseline, which keeps these row objects.
        observer?.Stage(WorkspaceLoadStage.Share);
        foreach (var data in w.Resources.Values)
            data.ShareUnchangedDraftRows();
        foreach (var (kind, data) in w.Resources)
            HistoryRepository.Read(c, tx, w.Id, kind, data, observer);
        return gaps;
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

    // Applies the journal's change of one row list. Writes are counted so the caller can report them.
    public static (int Writes, int Deletes, int Moves) Apply(SqliteConnection c, SqliteTransaction tx, string workspace, int kind, StoredRowList list)
    {
        const string where = "workspace_id=$w AND resource=$r AND collection=$c";
        var key = new (string, object?)[] { ("$w", workspace), ("$r", kind), ("$c", list.Collection) };
        if (list.Mode == ChangeMode.Rewrite)
        {
            Execute(c, tx, "DELETE FROM resource_rows WHERE " + where, key);
            Insert(c, tx, workspace, kind, list.Collection, list.All);
            return (list.All.Count, 0, 0);
        }
        using (var delete = Command(c, tx, "DELETE FROM resource_rows WHERE " + where + " AND local_id=$id", [.. key, ("$id", "")]))
        {
            delete.Prepare();
            foreach (var id in list.Deleted)
            {
                delete.Parameters["$id"].Value = id;
                if (delete.ExecuteNonQuery() != 1)
                    throw StorageErrors.Block("StorageInvalid");
            }
        }
        // Positions are unique per list: park the moving rows on negative ordinals first, then give them their final ones.
        foreach (var phase in new[] { 1, 2 })
        {
            using var move = Command(c, tx, "UPDATE resource_rows SET ordinal=$n WHERE " + where + " AND local_id=$id", [.. key, ("$id", ""), ("$n", 0)]);
            move.Prepare();
            foreach (var (id, ordinal) in list.Moved)
            {
                move.Parameters["$id"].Value = id;
                move.Parameters["$n"].Value = phase == 1 ? -1 - ordinal : ordinal;
                if (move.ExecuteNonQuery() != 1)
                    throw StorageErrors.Block("StorageInvalid");
            }
        }
        using (var update = Command(c, tx, "UPDATE resource_rows SET json=$json WHERE " + where + " AND local_id=$id", [.. key, ("$id", ""), ("$json", "")]))
        {
            update.Prepare();
            foreach (var row in list.Replaced)
            {
                update.Parameters["$id"].Value = row.Id;
                update.Parameters["$json"].Value = row.Json;
                if (update.ExecuteNonQuery() != 1)
                    throw StorageErrors.Block("StorageInvalid");
            }
        }
        Insert(c, tx, workspace, kind, list.Collection, list.Added);
        return (list.Replaced.Count + list.Added.Count, list.Deleted.Count, list.Moved.Count);
    }

    static void Insert(SqliteConnection c, SqliteTransaction tx, string workspace, int kind, int collection, List<StoredPlacement> rows)
    {
        using var insert = Command(c, tx, "INSERT INTO resource_rows VALUES($w,$r,$c,$id,$n,$json)", ("$w", workspace), ("$r", kind), ("$c", collection), ("$id", ""), ("$n", 0), ("$json", ""));
        insert.Prepare();
        foreach (var row in rows)
        {
            insert.Parameters["$id"].Value = row.Id;
            insert.Parameters["$n"].Value = row.Ordinal;
            insert.Parameters["$json"].Value = row.Json;
            insert.ExecuteNonQuery();
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
