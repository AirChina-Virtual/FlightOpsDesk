namespace VamSys.Core;

public enum CheckpointKind { Task, Step, Rebase }
// Items saves several task items in one commit; it is only valid for a Task checkpoint without Item.
public sealed record BatchCheckpoint(CheckpointKind Kind, BatchJob Job, ChangeItem? Item = null, Workspace? RebasedWorkspace = null, ResourceRebaseDelta? Delta = null, IReadOnlyList<ChangeItem>? Items = null);
public interface IBatchCheckpointWriter
{
    Task WriteAsync(BatchCheckpoint checkpoint, CancellationToken ct);
}

public sealed record BatchProgress(Guid WorkspaceId, Guid JobId, Guid? ItemId, bool Immediate = false);
