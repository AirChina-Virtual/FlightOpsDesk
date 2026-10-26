using VamSys.Core;

namespace VamSys.Infrastructure;

// Everything the stages of one job run share. Created when the run starts, gone when it ends.
internal sealed class JobRun
{
    public JobRun(OperationsAdapter api, Workspace workspace, BatchJob job, CheckpointSink sink, CancellationToken ct)
    {
        Api = api;
        Workspace = workspace;
        Job = job;
        Sink = sink;
        Ct = ct;
        Items = new ItemIndex(job.Items);
        Completion = new ItemCompletion(this);
    }

    public OperationsAdapter Api { get; }
    public Workspace Workspace { get; }
    public BatchJob Job { get; }
    public CheckpointSink Sink { get; }
    public CancellationToken Ct { get; }
    public ItemIndex Items { get; }
    public ItemCompletion Completion { get; }
    // Identities reserved by uncertain writes and the duplicate candidates read so far, for this run only.
    public CandidateQuerySession Queries { get; } = new();
}

// The item being executed and where in its life it is; the failure handler reads the phase to decide what an error means.
internal sealed class ItemRun(ChangeItem item, OperationsBatchExecutor.ExecutionPhase phase)
{
    public ChangeItem Item { get; } = item;
    public OperationsBatchExecutor.ExecutionPhase Phase { get; set; } = phase;
    // The remote id the item is looked up by: a proposed recovery id, or the id the create returned, or the Before row's.
    public string? Id { get; set; }
}
