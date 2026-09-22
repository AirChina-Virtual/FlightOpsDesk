namespace VamSys.Core;

public enum CheckpointKind { Task, Step, Rebase }
public enum BatchStepState { Prepared, InFlight, ResponseReceived }
public sealed class BatchStep
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public Dictionary<string,System.Text.Json.JsonElement> Body { get; set; } = [];
    public BatchStepState State { get; set; }
    public string? RemoteId { get; set; }
}
public sealed record BatchCheckpoint(CheckpointKind Kind,BatchJob Job,ChangeItem? Item=null,Workspace? RebasedWorkspace=null,ResourceRebaseDelta? Delta=null);
public interface IBatchCheckpointWriter
{
    Task WriteAsync(BatchCheckpoint checkpoint,CancellationToken ct);
}

public enum RowMutationKind { Keep=0, Upsert=1, Delete=2 }
public sealed record RowMutation(RowMutationKind Action,DataRow? Row=null);
public sealed record ResourceRebaseDelta(ResourceKind Resource,Guid LocalId,RowMutation Snapshot,RowMutation Draft,DateTimeOffset SnapshotAt,bool ClearHistory,string? VerifiedOperation);
public sealed record BatchProgress(Guid WorkspaceId,Guid JobId,Guid? ItemId, bool Immediate=false);