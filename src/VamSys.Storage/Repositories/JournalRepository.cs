using Microsoft.Data.Sqlite;
using VamSys.Core;
using static VamSys.Infrastructure.Sql;

namespace VamSys.Infrastructure;

// Applies one journal save inside the caller's transaction. Every statement that must hit exactly one row checks it;
// a mismatch means the database is not what the journal assumed, and the save is abandoned (the caller then repeats it as a full save).
internal static class JournalRepository
{
    public static SaveReport Write(SqliteConnection c, SqliteTransaction tx, Guid id, StoredChanges changes)
    {
        var workspace = id.ToString();
        int rows = 0, deletes = 0, moves = 0, metadata = 0, steps = 0, jobs = 0, items = 0;
        if (changes.HeaderJson != null)
            if (WorkspaceRepository.UpdateNameAndJson(c, tx, id, changes.Name!, changes.HeaderJson) != 1)
                throw StorageErrors.Block("StorageInvalid");
        switch (changes.JobsMode)
        {
            case ChangeMode.Rewrite:
                JobRepository.Write(c, tx, id, changes.AllJobs);
                jobs += changes.AllJobs.Count;
                items += changes.AllJobs.Sum(j => j.Items.Count);
                break;
            case ChangeMode.Delta:
                foreach (var job in changes.RewrittenJobs)
                { JobRepository.Rewrite(c, tx, workspace, job); jobs++; items += job.Items.Count; }
                foreach (var job in changes.InsertedJobs)
                { JobRepository.Insert(c, tx, workspace, job); jobs++; items += job.Items.Count; }
                break;
        }
        foreach (var resource in changes.Resources)
        {
            var kind = (int)resource.Kind;
            if (resource.Metadata is { } m)
            {
                if (Execute(c, tx, "UPDATE resource_metadata SET columns_json=$cols,conflicts_json=$conflicts,snapshot_at=$at WHERE workspace_id=$w AND resource=$r",
                    ("$cols", m.Columns), ("$conflicts", m.Conflicts), ("$at", m.SnapshotAt), ("$w", workspace), ("$r", kind)) != 1)
                    throw StorageErrors.Block("StorageInvalid");
                metadata++;
            }
            foreach (var list in resource.Lists)
            {
                var count = ResourceRowRepository.Apply(c, tx, workspace, kind, list);
                rows += count.Writes; deletes += count.Deletes; moves += count.Moves;
            }
            if (resource.History is { } history)
            {
                HistoryRepository.Write(c, tx, id, resource.Kind, history);
                steps += history.Steps.Count + history.Deletes.Length;
            }
        }
        return new("Journal", rows, deletes, moves, metadata, steps, jobs, items, changes.HeaderJson != null, 0);
    }
}
