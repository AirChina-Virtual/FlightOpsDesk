using VamSys.Core;

namespace VamSys.Infrastructure;

// Submits a job to the Operations API. The work is done by ExecutionPipeline and its stages (Api/Pipeline); this class
// keeps the two entry points the app and the tests use.
public sealed class OperationsBatchExecutor(OperationsAdapter api, Workspace workspace) : IBatchExecutor
{
    // Where in its life an item is when something goes wrong; saved in diagnostics by name, so the values are fixed.
    internal enum ExecutionPhase { Preflight, Writing, Verifying, Recovering }

    public Task ExecuteAsync(BatchJob job, Func<Task> persist, CancellationToken ct)
        => new ExecutionPipeline(api, workspace).RunAsync(job, persist, null, ct, null, null);

    public Task ExecuteAsync(BatchJob job, IBatchCheckpointWriter writer, CancellationToken ct, Action? committed = null, Action<BatchProgress>? progress = null)
        => new ExecutionPipeline(api, workspace).RunAsync(job, null, writer, ct, committed, progress);
}
