using VamSys.Core;
using Phase = VamSys.Infrastructure.OperationsBatchExecutor.ExecutionPhase;

namespace VamSys.Infrastructure;

// Runs one job: items in dependency-safe order, each through the same stages.
//
//   interrupted item   recovery   read what became of the write, settle from the answer, never repeat it
//   fresh item         preflight (local) -> plan -> duplicate check (creates) -> preflight (server)
//                      -> write -> read back -> complete        (each item ends with a checkpoint)
//   second pass        E7 (D5): an item whose write was rejected with 429 or never sent goes through once more, with
//                      the items that were skipped only because of it (each item at most twice per round)
//   end of the round   items this round left Unknown are looked up once more, read-only (see RecheckAsync); the items
//                      skipped only because of an item this lookup settled then run once (and are looked up likewise)
//
// The order of the requests, of the saves and of the metrics phases is the write-safety contract and is locked by the
// request goldens and the process-kill recovery tests. This class only sequences the stages and applies the job-level
// rules (cancellation, dependencies, pause on authentication, final status).
internal sealed class ExecutionPipeline(OperationsAdapter api, Workspace workspace)
{
    public async Task RunAsync(BatchJob job, Func<Task>? persist, IBatchCheckpointWriter? writer, CancellationToken ct, Action? committed, Action<BatchProgress>? progress)
    {
        var sink = new CheckpointSink(workspace, job, persist, writer, committed, progress);
        var run = new JobRun(api, workspace, job, sink, ct);
        var preflight = new PreflightStage(run);
        var duplicates = new DuplicateStage(run);
        var write = new WriteStage(run);
        var readback = new ReadbackStage(run);
        var recovery = new RecoveryStage(run, write);
        var failures = new FailureHandler(run);

        // Every step is marked in flight and saved before it is sent, so an interrupted item whose
        // saved steps are all still prepared never reached the server. Done before candidates are seeded.
        foreach (var item in job.Items)
            if (item.State is ItemState.Running or ItemState.Unknown && item.ApiSteps.Count > 0 && item.ApiSteps.All(s => s.State == BatchStepState.Prepared)
                && !item.CreateCompleted && !item.WriteAccepted && !item.CreateFoundWithoutAnswer)
                item.State = ItemState.Pending;
        api.InvalidateParameters();
        api.SeedCandidates(job, run.Queries);
        job.SetStatus(JobStatus.Running, Messages.Define("Text_5026A63B58"));
        var fresh = new List<ChangeItem>();
        var unprocessed = new HashSet<ChangeItem>();
        var order = job.Items.OrderBy(i => i.Kind == ChangeKind.Delete ? 10 - (int)i.Resource : (int)i.Resource).ToList();
        if (!await PassAsync(order, collect: true) && unprocessed.Count > 0 && job.StatusCode == JobStatus.Running && !ct.IsCancellationRequested)
        {
            // E7 (D5): the server did not process these writes (429, or the request never left), so they are sent once
            // more in this round, after the budget's Retry-After wait; items skipped only because of them follow them.
            var again = order.Where(i => unprocessed.Contains(i)
                || i.State == ItemState.Skipped && i.Dependencies.Any(id => unprocessed.Contains(run.Items.ById(id)))).ToList();
            await PassAsync(again, collect: false);
        }
        if (job.StatusCode == JobStatus.Running)
        {
            var rechecked = fresh.Distinct().Where(i => i.State == ItemState.Unknown).ToList();
            await RecheckAsync(rechecked, run, recovery);
            // An item the end-of-round lookup settled no longer holds back the items skipped because of it: they run now,
            // once, in order, instead of waiting for "Continue". A skipped item never reached the server, and an item
            // whose other dependency is still not Succeeded (unknown, failed) is skipped again by the pass itself.
            var dependents = SkippedDependents(order, rechecked.Where(i => i.State == ItemState.Succeeded), run.Items);
            if (dependents.Count > 0 && job.StatusCode == JobStatus.Running && !ct.IsCancellationRequested)
            {
                var before = fresh.Count;
                if (!await PassAsync(dependents, collect: false) && job.StatusCode == JobStatus.Running)
                    await RecheckAsync(fresh.Skip(before).Distinct().Where(i => i.State == ItemState.Unknown).ToList(), run, recovery);
            }
        }
        sink.Active = null;
        if (job.StatusCode == JobStatus.Running)
            job.SetStatus(job.Items.All(i => i.State == ItemState.Succeeded) ? JobStatus.Completed : JobStatus.NeedsAttention, Messages.Define(job.Items.All(i => i.State == ItemState.Succeeded) ? "Text_140197D868" : "Text_E9008C53CF"));
        await sink.SaveAsync();

        // Returns true when the job stopped (cancelled, or paused on authentication).
        async Task<bool> PassAsync(List<ChangeItem> items, bool collect)
        {
            foreach (var item in items)
            {
                sink.Active = item;
                // Recovery is read-only even when dependency checks or the recovery GET fail.
                var recovering = item.State is ItemState.Running or ItemState.Unknown || item.CreateCompleted || item.WriteAccepted || item.CreateFoundWithoutAnswer;
                if (recovering && item.State is not (ItemState.Succeeded or ItemState.ManuallyConfirmed))
                    item.State = ItemState.Unknown;
                if (ct.IsCancellationRequested)
                { job.SetStatus(JobStatus.Canceled, Messages.Define("Text_6F312AE253")); return true; }
                if (item.State is ItemState.Succeeded or ItemState.Conflict or ItemState.Failed or ItemState.ManuallyConfirmed)
                    continue;
                if (!recovering && item.Dependencies.Any(id => run.Items.ById(id).State != ItemState.Succeeded))
                { item.State = ItemState.Skipped; item.SetMessage(Messages.Define("Text_3E85DDB213")); await sink.SaveAsync(); continue; }
                var r = new ItemRun(item, recovering ? Phase.Recovering : Phase.Preflight);
                if (PerformanceRun.Current is { } metrics)
                    metrics.Phase = r.Phase.ToString();
                try
                {
                    preflight.CheckLocal(item);
                    r.Id = item.CanProposeRecoveryId ? item.EditableRecoveryId : item.RemoteId ?? item.Before?.Identity?.RemoteId;
                    if (recovering)
                    {
                        // An interrupted request is never automatically repeated.
                        await recovery.RunAsync(r);
                        await sink.SaveAsync();
                        continue;
                    }
                    var steps = api.ExecutionPlan(item);
                    if (item.Kind == ChangeKind.Create && await duplicates.RunAsync(r))
                        continue;
                    if (await preflight.CheckRemoteAsync(r))
                        continue;
                    fresh.Add(item);
                    await write.RunAsync(r, steps);
                    await readback.RunAsync(r);
                }
                catch (PersistenceException) { throw; }
                catch (Exception e)
                {
                    if (await failures.HandleAsync(e, r))
                        return true;
                    if (collect && item.State == ItemState.Pending && NotProcessed(e, r.Phase) && !ct.IsCancellationRequested)
                        unprocessed.Add(item);
                }
                if (item.State is ItemState.Failed or ItemState.Pending && !item.CreateCompleted && !item.WriteAccepted && !item.CreateFoundWithoutAnswer)
                    run.Queries.Release(item);
                await sink.SaveAsync();
                if (ct.IsCancellationRequested)
                { job.SetStatus(JobStatus.Canceled, Messages.Define("Text_6F312AE253")); return true; }
            }
            return false;
        }
    }

    // The skipped items that wait, directly or through other skipped items, on one of the settled items, in job order.
    // Only items that never reached the server qualify: a skip happens before any stage, and an item with an answered
    // or interrupted write is recovered, never skipped.
    internal static List<ChangeItem> SkippedDependents(List<ChangeItem> order, IEnumerable<ChangeItem> settled, ItemIndex items)
    {
        var released = new HashSet<ChangeItem>(settled);
        if (released.Count == 0)
            return [];
        var result = new HashSet<ChangeItem>();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var item in order)
                if (item.State == ItemState.Skipped && !item.CreateCompleted && !item.WriteAccepted && !result.Contains(item)
                    && item.Dependencies.Any(id => released.Contains(items.ById(id))))
                { result.Add(item); released.Add(item); grew = true; }
        }
        return order.Where(result.Contains).ToList();
    }

    // E7 (D5): the only two failures the server certainly did not process: a 429 (rejected before processing) and a
    // request that never left (rate-limit wait, token refresh). An authentication failure pauses the job instead.
    static bool NotProcessed(Exception e, Phase phase)
        => e is ApiResponseException { Status: System.Net.HttpStatusCode.TooManyRequests } && phase is Phase.Preflight or Phase.Writing
           || e is RequestNotSentException { InnerException: not ApiAccessException and not OperationCanceledException };

    // E2: a write that ended Unknown in this round (timeout, 5xx, a read-back that did not match) is looked up once more
    // after every other item, through the same read-only recovery path the "Continue" button uses, so what the server
    // already shows is settled now instead of waiting for the user. Nothing is written: a legacy create whose later
    // steps would still have to be sent, and a create with no id that cannot be searched for (E1), are left for the user. When the lookup
    // fails, or still cannot settle the item, the item keeps the state and message it had; only newer evidence is kept.
    static async Task RecheckAsync(List<ChangeItem> unknown, JobRun run, RecoveryStage recovery)
    {
        foreach (var item in unknown)
        {
            if (run.Ct.IsCancellationRequested)
                return;
            if (OperationsAdapter.CanResumeCreate(item))
                continue;
            var r = new ItemRun(item, Phase.Recovering) { Id = item.CanProposeRecoveryId ? item.EditableRecoveryId : item.RemoteId ?? item.Before?.Identity?.RemoteId };
            if (r.Id == null && !CreatedRecordFinder.CanSearch(item))
                continue;
            run.Sink.Active = item;
            if (PerformanceRun.Current is { } metrics)
                metrics.Phase = r.Phase.ToString();
            var (description, message, readback) = (item.Description, item.Message, item.ReadbackDetails);
            try
            {
                await recovery.RunAsync(r);
            }
            catch (PersistenceException) { throw; }
            catch (Exception)
            { item.State = ItemState.Unknown; item.ReadbackDetails = readback; }
            // A search that found several records the create may have made says so instead (E1), as does a create whose
            // record was found but whose later steps were never sent.
            if (item.State == ItemState.Unknown && item.Description?.Code is not ("ApiCreateCandidates" or "ApiCreateFoundFollowUpsNotSent"))
            { item.Description = description; item.Message = message; }
            await run.Sink.SaveAsync();
        }
    }
}
