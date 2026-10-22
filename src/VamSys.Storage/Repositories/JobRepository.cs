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

    public static void Write(SqliteConnection c, SqliteTransaction tx, FullPayload p)
    {
        Execute(c, tx, "DELETE FROM batch_jobs WHERE workspace_id=$w", ("$w", p.Id.ToString()));
        foreach (var job in p.Jobs)
        {
            Execute(c, tx, "INSERT INTO batch_jobs(workspace_id,id,ordinal,json) VALUES($w,$j,$n,$json)", ("$w", p.Id.ToString()), ("$j", job.Id.ToString()), ("$n", job.Ordinal), ("$json", job.Json));
            foreach (var item in job.Items)
                Execute(c, tx, "INSERT INTO batch_items(workspace_id,job_id,id,ordinal,json) VALUES($w,$j,$i,$n,$json)", ("$w", p.Id.ToString()), ("$j", job.Id.ToString()), ("$i", item.Id.ToString()), ("$n", item.Ordinal), ("$json", item.Json));
        }
    }

    // Both return the number of rows changed; callers require exactly one.
    public static int UpdateHeader(SqliteConnection c, SqliteTransaction tx, string workspace, string job, string json)
        => Execute(c, tx, "UPDATE batch_jobs SET json=$json WHERE workspace_id=$w AND id=$j", ("$json", json), ("$w", workspace), ("$j", job));
    public static int UpdateItem(SqliteConnection c, SqliteTransaction tx, string workspace, string job, string item, string json)
        => Execute(c, tx, "UPDATE batch_items SET json=$json WHERE workspace_id=$w AND job_id=$j AND id=$i", ("$json", json), ("$w", workspace), ("$j", job), ("$i", item));
}
