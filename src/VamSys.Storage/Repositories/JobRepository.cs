using Microsoft.Data.Sqlite;
using VamSys.Core;
using static VamSys.Infrastructure.RowSerializer;
using static VamSys.Infrastructure.Sql;

namespace VamSys.Infrastructure;

// batch_jobs / batch_items: task headers and their records.
internal static class JobRepository
{
    public static void Read(SqliteConnection c, SqliteTransaction tx, Workspace w)
    {
        w.Jobs.Clear();
        using (var cmd = Command(c, tx, "SELECT json FROM batch_jobs WHERE workspace_id=$id ORDER BY ordinal", ("$id", w.Id.ToString())))
        using (var reader = cmd.ExecuteReader())
        while (reader.Read())
            w.Jobs.Add(Decode<BatchJob>(reader.GetString(0))!);
        var jobs = w.Jobs.ToDictionary(j => j.Id);
        using (var cmd = Command(c, tx, "SELECT job_id,json FROM batch_items WHERE workspace_id=$id ORDER BY job_id,ordinal", ("$id", w.Id.ToString())))
        using (var reader = cmd.ExecuteReader())
        while (reader.Read())
            jobs[Guid.Parse(reader.GetString(0))].Items.Add(Decode<ChangeItem>(reader.GetString(1))!);
    }

    public static void Write(SqliteConnection c, SqliteTransaction tx, FullPayload p) => Write(c, tx, p.Id, p.Jobs);

    // Replaces all jobs of the workspace.
    public static void Write(SqliteConnection c, SqliteTransaction tx, Guid workspace, IEnumerable<StoredJob> jobs)
    {
        Execute(c, tx, "DELETE FROM batch_jobs WHERE workspace_id=$w", ("$w", workspace.ToString()));
        foreach (var job in jobs)
            Insert(c, tx, workspace.ToString(), job);
    }

    public static void Insert(SqliteConnection c, SqliteTransaction tx, string workspace, StoredJob job)
    {
        Execute(c, tx, "INSERT INTO batch_jobs(workspace_id,id,ordinal,json) VALUES($w,$j,$n,$json)", ("$w", workspace), ("$j", job.Id.ToString()), ("$n", job.Ordinal), ("$json", job.Json));
        InsertItems(c, tx, workspace, job);
    }

    // An existing job written again: header and all items (items are replaced, not merged).
    public static void Rewrite(SqliteConnection c, SqliteTransaction tx, string workspace, StoredJob job)
    {
        if (Execute(c, tx, "UPDATE batch_jobs SET ordinal=$n,json=$json WHERE workspace_id=$w AND id=$j", ("$n", job.Ordinal), ("$json", job.Json), ("$w", workspace), ("$j", job.Id.ToString())) != 1)
            throw StorageErrors.Block("StorageInvalid");
        Execute(c, tx, "DELETE FROM batch_items WHERE workspace_id=$w AND job_id=$j", ("$w", workspace), ("$j", job.Id.ToString()));
        InsertItems(c, tx, workspace, job);
    }

    static void InsertItems(SqliteConnection c, SqliteTransaction tx, string workspace, StoredJob job)
    {
        using var insert = Command(c, tx, "INSERT INTO batch_items(workspace_id,job_id,id,ordinal,json) VALUES($w,$j,$i,$n,$json)", ("$w", workspace), ("$j", job.Id.ToString()), ("$i", ""), ("$n", 0), ("$json", ""));
        insert.Prepare();
        foreach (var item in job.Items)
        {
            insert.Parameters["$i"].Value = item.Id.ToString();
            insert.Parameters["$n"].Value = item.Ordinal;
            insert.Parameters["$json"].Value = item.Json;
            insert.ExecuteNonQuery();
        }
    }

    // Targeted reads: one job, or only the jobs, without the resource rows.
    public static bool Exists(SqliteConnection c, Guid workspace, Guid job)
        => Scalar(c, null, "SELECT 1 FROM batch_jobs WHERE workspace_id=$w AND id=$j", ("$w", workspace.ToString()), ("$j", job.ToString())) != null;

    public static BatchJob? ReadOne(SqliteConnection c, Guid workspace, Guid job)
    {
        using var tx = c.BeginTransaction(deferred: true);
        var json = Scalar(c, tx, "SELECT json FROM batch_jobs WHERE workspace_id=$w AND id=$j", ("$w", workspace.ToString()), ("$j", job.ToString()));
        if (json == null)
            return null;
        var result = Decode<BatchJob>((string)json)!;
        using (var cmd = Command(c, tx, "SELECT json FROM batch_items WHERE workspace_id=$w AND job_id=$j ORDER BY ordinal", ("$w", workspace.ToString()), ("$j", job.ToString())))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                result.Items.Add(Decode<ChangeItem>(reader.GetString(0))!);
        tx.Commit();
        return result;
    }

    public static int Count(SqliteConnection c, SqliteTransaction? tx, Guid workspace)
        => Convert.ToInt32(Scalar(c, tx, "SELECT count(*) FROM batch_jobs WHERE workspace_id=$w", ("$w", workspace.ToString())));

    // Both return the number of rows changed; callers require exactly one.
    public static int UpdateHeader(SqliteConnection c, SqliteTransaction tx, string workspace, string job, string json)
        => Execute(c, tx, "UPDATE batch_jobs SET json=$json WHERE workspace_id=$w AND id=$j", ("$json", json), ("$w", workspace), ("$j", job));
    public static int UpdateItem(SqliteConnection c, SqliteTransaction tx, string workspace, string job, string item, string json)
        => Execute(c, tx, "UPDATE batch_items SET json=$json WHERE workspace_id=$w AND job_id=$j AND id=$i", ("$json", json), ("$w", workspace), ("$j", job), ("$i", item));
}
