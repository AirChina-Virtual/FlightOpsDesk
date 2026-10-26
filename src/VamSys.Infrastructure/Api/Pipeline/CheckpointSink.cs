using VamSys.Core;

namespace VamSys.Infrastructure;

// The last stage of every item: make what just happened durable, then tell the listeners. Either through the
// checkpoint writer (one atomic commit per checkpoint, the way the app runs) or through the plain persist callback
// (tests and the legacy overload). A failed save is never an API failure: it stops the run as a PersistenceException.
internal sealed class CheckpointSink(Workspace workspace, BatchJob job, Func<Task>? persist, IBatchCheckpointWriter? writer, Action? committed, Action<BatchProgress>? progress)
{
    // The item being executed, so progress can say which one a checkpoint belongs to.
    public ChangeItem? Active { get; set; }
    public bool HasWriter => writer != null;

    public async Task SaveAsync(CheckpointKind kind = CheckpointKind.Task)
    {
        try
        {
            if (writer == null)
                await persist!();
            else
                await writer.WriteAsync(new(kind, job, Active), CancellationToken.None);
            committed?.Invoke();
            progress?.Invoke(new(workspace.Id, job.Id, Active?.Id, job.StatusCode != JobStatus.Running || Active?.State is ItemState.Failed or ItemState.Unknown));
        }
        catch (Exception e) { throw new PersistenceException(e); }
    }

    // The checkpoint that rebases one verified row into the workspace together with the item's final state.
    public async Task WriteRebaseAsync(ChangeItem item, ResourceRebaseDelta delta)
    {
        try
        { await writer!.WriteAsync(new(CheckpointKind.Rebase, job, item, Delta: delta), CancellationToken.None); }
        catch (Exception e) { throw new PersistenceException(e); }
    }

    // After the in-memory workspace follows a rebase checkpoint.
    public void NotifyRebased(ChangeItem item)
    {
        try
        { committed?.Invoke(); progress?.Invoke(new(workspace.Id, job.Id, item.Id)); }
        catch (Exception e) { throw new PersistenceException(e); }
    }
}
