using VamSys.Core;
using Phase = VamSys.Infrastructure.OperationsBatchExecutor.ExecutionPhase;

namespace VamSys.Infrastructure;

// Third stage: the only place a request that changes the server is sent. Every step is saved as in flight before it
// is sent and again after its answer (the StepWriter's rule); this stage adds the item-level checkpoints around it
// and reserves the item's identities for as long as its outcome can be unknown.
internal sealed class WriteStage(JobRun run)
{
    public async Task RunAsync(ItemRun r, List<ApiStep> steps)
    {
        var item = r.Item;
        var api = run.Api;
        item.ApiExpectedValues = OperationsAdapter.ExpectedValues(steps);
        OperationsAdapter.PrepareSteps(item, steps);
        r.Phase = Phase.Writing;
        if (PerformanceRun.Current is { } writeMetrics)
            writeMetrics.Phase = "Writing";
        item.State = ItemState.Running;
        item.SetMessage(Messages.Define("Text_DCC1A22D9E"));
        await run.Sink.SaveAsync();
        api.ReserveCandidate(item, run.Queries);
        item.RemoteId = await api.WriteStepsAsync(item, steps, () => run.Sink.SaveAsync(CheckpointKind.Step), run.Ct);
        await run.Sink.SaveAsync();
    }

    // A legacy two-step create whose POST was answered: only the steps that never reached the server are sent;
    // the POST is skipped, the record is never created again.
    public async Task ResumeAsync(ItemRun r, List<ApiStep> remaining)
    {
        var item = r.Item;
        item.ApiExpectedValues = OperationsAdapter.ExpectedValues(remaining);
        r.Phase = Phase.Writing;
        item.State = ItemState.Running;
        item.SetMessage(Messages.Define("Text_DCC1A22D9E"));
        await run.Sink.SaveAsync();
        item.RemoteId = await run.Api.WriteStepsAsync(item, remaining, () => run.Sink.SaveAsync(CheckpointKind.Step), run.Ct);
        await run.Sink.SaveAsync();
    }
}
