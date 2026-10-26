namespace VamSys.Core;

// Runs a demo task against the simulated server and brings the workspace in line with what succeeded.
public sealed class DemoRunner(WorkspaceSession session, Func<Workspace, IReadOnlyList<BatchJob>, (IResourceReader Reader, IResourceWriter Writer)> createEngine)
{
    // Failed items go back to Pending and are saved (only the task is written) before the task runs again.
    // Returns the items that were reset so their rows can be refreshed.
    public async Task<IReadOnlyList<ChangeItem>> ResetFailedAsync(BatchJob job)
    {
        var reset = job.Items.Where(i => i.State == ItemState.Failed).ToList();
        foreach (var i in reset)
            i.State = ItemState.Pending;
        // The items were changed in place, so the job has to be marked for the journal.
        session.Workspace.Journal().TouchJob(job);
        await session.SaveJobsAsync();
        return reset;
    }

    // beforeExecute runs once the simulated server knows the earlier demo results and before the first item is sent (the
    // window shows the task page there). progress is told after every saved step with the item it concerned.
    public async Task RunAsync(BatchJob job, CancellationToken ct, Action beforeExecute, Action<ChangeItem?> progress)
    {
        var workspace = session.Workspace;
        var (reader, writer) = createEngine(workspace, workspace.Jobs.Where(j => j.Mode == RunMode.Demo).ToList());
        beforeExecute();
        // The executor changes the job and its items in place and asks for a save after every item: only the job is written.
        await new BatchExecutor(reader, writer).ExecuteAsync(job, async () => { workspace.Journal().TouchJob(job); await session.SaveJobsAsync(); }, ct, progress);
        // Rebase only verified successful rows, keeping failures and unrelated drafts intact.
        foreach (var item in job.Items.Where(i => i.State == ItemState.Succeeded && !i.Rebased))
        {
            var data = workspace.Resources[item.Resource];
            data.Snapshot.RemoveAll(r => r.LocalId == item.After.LocalId);
            if (item.Kind == ChangeKind.Delete)
                data.Draft.RemoveAll(r => r.LocalId == item.After.LocalId && Schemas.Deleted(r));
            else
            {
                var updated = item.RemoteId != null ? item.After.With(Schemas.All[item.Resource].Key, item.RemoteId) : item.After;
                data.Snapshot.Add(updated);
                var index = data.FindDraftPosition(updated.LocalId);
                if (index >= 0)
                {
                    // Preserve edits made after a recovered job was submitted; only rebase identity/references.
                    var current = data.Draft[index].ToBuilder();
                    current[Schemas.All[item.Resource].Key] = updated.Get(Schemas.All[item.Resource].Key);
                    foreach (var field in Schemas.ReferenceFields(item.Resource))
                        if (LocalReference.ContainsLocal(current.Get(field)))
                            current[field] = updated.Get(field);
                    data.ReplaceDraftRows([current.ToRow()]);
                }
            }
            item.Rebased = true;
            data.SnapshotAt = DateTimeOffset.UtcNow;
            data.ClearHistory(invalidateIndex: item.Kind == ChangeKind.Delete);
        }
        workspace.Journal().TouchJob(job);
        await session.SaveAsync();
    }
}
