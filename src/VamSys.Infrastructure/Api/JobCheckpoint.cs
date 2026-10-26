using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

// The pieces every manual resolution of a task item shares (confirm a delete, accept the server's value, mark an
// unknown write as not applied, discard unsent items). Each one works on a copy of the item, saves the copy as one
// checkpoint, and only after that save succeeded replaces the item in the live job and moves the workspace.
internal static class JobCheckpoint
{
    // Items are cloned through their saved form, so the copy is exactly what a restart would read back.
    public static ChangeItem Clone(ChangeItem item) => JsonSerializer.Deserialize<ChangeItem>(JsonSerializer.Serialize(item))!;

    // The job as a checkpoint sees it: same identity, mode and items, a new status. The live job is not touched yet.
    public static BatchJob Header(BatchJob job, JobStatus status, MessageDescriptor description)
    {
        var header = new BatchJob { Id = job.Id, Created = job.Created, Mode = job.Mode, Items = job.Items };
        header.SetStatus(status, description);
        return header;
    }

    // A save that fails is reported as such and changes nothing in memory.
    public static async Task WriteAsync(IBatchCheckpointWriter writer, BatchCheckpoint checkpoint)
    {
        try
        { await writer.WriteAsync(checkpoint, CancellationToken.None); }
        catch (Exception error) { throw new PersistenceException(error); }
    }

    // After the checkpoint was saved: the copy takes the item's place and the job takes the header's status.
    public static void Replace(BatchJob job, ChangeItem item, ChangeItem candidate, BatchJob header)
    {
        job.Items[job.Items.IndexOf(item)] = candidate;
        job.StatusCode = header.StatusCode;
        job.Status = header.Status;
        job.Description = header.Description;
    }

    // A record that is gone from the server leaves the snapshot, and its deletion draft leaves with it.
    public static void ApplyRemoval(ResourceData data, ResourceRebaseDelta delta)
    {
        data.Snapshot.RemoveAll(r => r.LocalId == delta.LocalId);
        if (delta.Draft.Action == RowMutationKind.Delete)
            data.Draft.RemoveAll(r => r.LocalId == delta.LocalId);
        data.ClearHistory(invalidateIndex: delta.Draft.Action == RowMutationKind.Delete);
        data.SnapshotAt = delta.SnapshotAt;
    }
}
