using Microsoft.Data.Sqlite;
using VamSys.Core;
using static VamSys.Infrastructure.Sql;
namespace VamSys.Infrastructure;

// workspaces (header JSON without jobs and resources) and workspace_revisions (the save version).
internal static class WorkspaceRepository
{
    public static List<(Guid Id, string Name)> List(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name FROM workspaces ORDER BY name";
        using var r = cmd.ExecuteReader();
        var result = new List<(Guid, string)>();
        while (r.Read())
            result.Add((Guid.Parse(r.GetString(0)), r.GetString(1)));
        return result;
    }

    // One read transaction: header, revision, jobs and resources always belong to the same committed state.
    public static Workspace Load(SqliteConnection c, Guid id)
    {
        using var tx = c.BeginTransaction(deferred: true);
        var w = Workspace.Deserialize((string)(Scalar(c, tx, "SELECT json FROM workspaces WHERE id=$id", ("$id", id.ToString())) ?? throw new KeyNotFoundException()));
        w.StorageRevision = Convert.ToInt64(Scalar(c, tx, "SELECT revision FROM workspace_revisions WHERE workspace_id=$id", ("$id", id.ToString())));
        JobRepository.Read(c, tx, w);
        ResourceRowRepository.Read(c, tx, w);
        tx.Commit();
        // What was just read is exactly what the database holds, so later saves can write only the difference.
        w.Journal().Adopt(w.Journal().Capture(w, RowSerializer.HeaderKey(w)), w.StorageRevision);
        return w;
    }

    // Name, revision, jobs and row counts of a stored workspace without loading rows or history.
    public static WorkspaceCounts Counts(SqliteConnection c, Guid id)
    {
        using var tx = c.BeginTransaction(deferred: true);
        var w = id.ToString();
        var name = (string?)Scalar(c, tx, "SELECT name FROM workspaces WHERE id=$id", ("$id", w)) ?? throw new KeyNotFoundException();
        var revision = Convert.ToInt64(Scalar(c, tx, "SELECT revision FROM workspace_revisions WHERE workspace_id=$id", ("$id", w)));
        var resources = new Dictionary<ResourceKind, ResourceCounts>();
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var args = new (string, object?)[] { ("$w", w), ("$r", (int)kind) };
            int Count(string sql) => Convert.ToInt32(Scalar(c, tx, sql, args));
            var conflicts = Scalar(c, tx, "SELECT conflicts_json FROM resource_metadata WHERE workspace_id=$w AND resource=$r", args);
            resources[kind] = new(Count("SELECT count(*) FROM resource_rows WHERE workspace_id=$w AND resource=$r AND collection=0"),
                Count("SELECT count(*) FROM resource_rows WHERE workspace_id=$w AND resource=$r AND collection=1"),
                conflicts is string text ? RowSerializer.Decode<List<MergeConflict>>(text)!.Count : 0,
                Count("SELECT count(*) FROM resource_history_steps WHERE workspace_id=$w AND resource=$r"));
        }
        var jobs = JobRepository.Count(c, tx, id);
        tx.Commit();
        return new(id, name, revision, jobs, resources);
    }

    // Jobs and their items only, for checks that do not need the resource rows.
    public static List<BatchJob> LoadJobs(SqliteConnection c, Guid id)
    {
        using var tx = c.BeginTransaction(deferred: true);
        var w = new Workspace { Id = id };
        JobRepository.Read(c, tx, w);
        tx.Commit();
        return w.Jobs;
    }

    public static void Upsert(SqliteConnection c, SqliteTransaction tx, FullPayload p)
        => Execute(c, tx, "INSERT INTO workspaces(id,name,json,format_version) VALUES($id,$name,$json,4) ON CONFLICT(id) DO UPDATE SET name=$name,json=$json,format_version=4",
            ("$id", p.Id.ToString()), ("$name", p.Name), ("$json", p.Json));
    public static int UpdateJson(SqliteConnection c, SqliteTransaction tx, Guid id, string json)
        => Execute(c, tx, "UPDATE workspaces SET json=$json WHERE id=$id", ("$json", json), ("$id", id.ToString()));
    public static int UpdateNameAndJson(SqliteConnection c, SqliteTransaction tx, Guid id, string name, string json)
        => Execute(c, tx, "UPDATE workspaces SET name=$name,json=$json WHERE id=$w", ("$name", name), ("$json", json), ("$w", id.ToString()));

    // Migration steps: the header rewritten in the v4 shape, only the format marker, and the first save version.
    public static void SetV4Header(SqliteConnection c, SqliteTransaction tx, string id, string json)
        => Execute(c, tx, "UPDATE workspaces SET json=$json,format_version=4 WHERE id=$id", ("$json", json), ("$id", id));
    public static void MarkFormat4(SqliteConnection c, SqliteTransaction tx, string id)
        => Execute(c, tx, "UPDATE workspaces SET format_version=4 WHERE id=$id", ("$id", id));
    public static void InsertInitialRevision(SqliteConnection c, SqliteTransaction tx, string id)
        => Execute(c, tx, "INSERT INTO workspace_revisions VALUES($id,0)", ("$id", id));

    public static object? ReadRevision(SqliteConnection c, SqliteTransaction tx, Guid id)
        => Scalar(c, tx, "SELECT revision FROM workspace_revisions WHERE workspace_id=$w", ("$w", id.ToString()));
    public static void WriteRevision(SqliteConnection c, SqliteTransaction tx, Guid id, long revision)
        => Execute(c, tx, "INSERT INTO workspace_revisions(workspace_id,revision) VALUES($w,$r) ON CONFLICT(workspace_id) DO UPDATE SET revision=$r", ("$w", id.ToString()), ("$r", revision));
}
