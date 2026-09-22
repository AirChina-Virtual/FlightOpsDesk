using System.Net;
using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed class OperationsBatchExecutor(OperationsAdapter api,Workspace workspace) : IBatchExecutor
{
    enum ExecutionPhase { Preflight, Writing, Verifying, Recovering }
    public Task ExecuteAsync(BatchJob job,Func<Task> persist,CancellationToken ct)
        => ExecuteCoreAsync(job,persist,null,ct,null,null);
    public Task ExecuteAsync(BatchJob job,IBatchCheckpointWriter writer,CancellationToken ct,Action? committed=null,Action<BatchProgress>? progress=null)
        => ExecuteCoreAsync(job,null,writer,ct,committed,progress);
    async Task ExecuteCoreAsync(BatchJob job,Func<Task>? persist,IBatchCheckpointWriter? writer,CancellationToken ct,Action? committed,Action<BatchProgress>? progress)
    {
        ChangeItem? active=null;
        async Task Save(CheckpointKind kind=CheckpointKind.Task)
        {
            try {
                if(writer==null) await persist!();
                else await writer.WriteAsync(new(kind,job,active),CancellationToken.None);
                committed?.Invoke();progress?.Invoke(new(workspace.Id,job.Id,active?.Id,job.StatusCode!=JobStatus.Running||active?.State is ItemState.Failed or ItemState.Unknown));
            } catch(Exception e) { throw new PersistenceException(e); }
        }
        async Task Complete(ChangeItem item,DataRow? verified,CandidateQuerySession queries)
        {
            if(writer==null) { ApplyRebase(workspace,item,verified); api.ConfirmCandidate(item,verified,queries); if(verified==null && (item.RemoteId ?? item.Before?.Identity?.RemoteId) is string id)api.Forget(item.Resource,id); return; }
            var original=workspace.Resources[item.Resource];
            var current=original.Draft.FirstOrDefault(r=>r.LocalId==item.After.LocalId);
            var data=new ResourceData{Draft=current==null?[]:[current.Copy()],SnapshotAt=original.SnapshotAt};
            var staged=workspace.StageResource(item.Resource,data);
            var stagedItem=System.Text.Json.JsonSerializer.Deserialize<ChangeItem>(System.Text.Json.JsonSerializer.Serialize(item))!;
            ApplyRebase(staged,stagedItem,verified?.Copy());
            var delta=new ResourceRebaseDelta(item.Resource,item.After.LocalId,
                new(verified==null?RowMutationKind.Delete:RowMutationKind.Upsert,data.Snapshot.FirstOrDefault()),
                new(current==null?RowMutationKind.Keep:data.Draft.Count==0?RowMutationKind.Delete:RowMutationKind.Upsert,data.Draft.FirstOrDefault()),
                data.SnapshotAt!.Value,true,$"{item.Resource}:{item.Kind}");
            try { await writer.WriteAsync(new(CheckpointKind.Rebase,job,stagedItem,Delta:delta),CancellationToken.None); }
            catch(Exception e) { throw new PersistenceException(e); }
            original.Snapshot.RemoveAll(r=>r.LocalId==delta.LocalId);
            if(delta.Snapshot.Row!=null)original.Snapshot.Add(delta.Snapshot.Row);
            if(delta.Draft.Action==RowMutationKind.Delete)original.Draft.RemoveAll(r=>r.LocalId==delta.LocalId);
            else if(delta.Draft.Row!=null)original.Draft[original.Draft.FindIndex(r=>r.LocalId==delta.LocalId)]=delta.Draft.Row;
            original.Undo.Clear();original.Redo.Clear();original.SnapshotAt=delta.SnapshotAt;
            workspace.VerifiedOperations=staged.VerifiedOperations;
            item.RemoteId=stagedItem.RemoteId;item.After=stagedItem.After;item.CreateCompleted=stagedItem.CreateCompleted;
            item.RecoveryCandidateId=stagedItem.RecoveryCandidateId;item.Rebased=stagedItem.Rebased;
            item.State=stagedItem.State;item.Message=stagedItem.Message;item.Description=stagedItem.Description;
            api.ConfirmCandidate(item,verified,queries);
            if(verified==null && (item.RemoteId ?? item.Before?.Identity?.RemoteId) is string removed) api.Forget(item.Resource,removed);
            try { committed?.Invoke();progress?.Invoke(new(workspace.Id,job.Id,item.Id)); } catch(Exception e) { throw new PersistenceException(e); }
        }
        var queries=new CandidateQuerySession();
        api.SeedCandidates(job,queries);
        job.SetStatus(JobStatus.Running,Messages.Define("Text_5026A63B58"));
        foreach(var item in job.Items.OrderBy(i=>i.Kind==ChangeKind.Delete?10-(int)i.Resource:(int)i.Resource))
        {
            active=item;
            // Recovery is read-only even when dependency checks or the recovery GET fail.
            var recovering=item.State is ItemState.Running or ItemState.Unknown || item.CreateCompleted || item.WriteAccepted;
            if(recovering && item.State is not (ItemState.Succeeded or ItemState.ManuallyConfirmed)) item.State=ItemState.Unknown;
            if(ct.IsCancellationRequested) { job.SetStatus(JobStatus.Canceled,Messages.Define("Text_6F312AE253")); break; }
            if(item.State is ItemState.Succeeded or ItemState.Conflict or ItemState.Failed or ItemState.ManuallyConfirmed) continue;
            if(!recovering && item.Dependencies.Any(id=>job.Items.Single(i=>i.Id==id).State!=ItemState.Succeeded))
            { item.State=ItemState.Skipped; item.SetMessage(Messages.Define("Text_3E85DDB213")); await Save(); continue; }
            var phase=recovering ? ExecutionPhase.Recovering : ExecutionPhase.Preflight;
            if(PerformanceRun.Current is {} metrics) metrics.Phase=phase.ToString();
            try
            {
                if(workspace.Resources[item.Resource].Conflicts.Any(c=>c.RowId==item.After.LocalId)) throw OperationsAdapter.Block("ApiConflict");
                foreach(var field in Schemas.ReferenceFields(item.Resource))
                {
                    var tokens=item.After.Get(field).Split(',',StringSplitOptions.TrimEntries);
                    if(!tokens.Any(t=>t.StartsWith("local:"))) continue;
                    var resolved=tokens.Select(t=>t.StartsWith("local:") ? job.Items.Single(d=>"local:"+d.After.LocalId==t).RemoteId ?? throw OperationsAdapter.Block("ApiRefreshFirst") : t);
                    item.After.Fields[field]=string.Join(',',resolved); item.Fields[field]=new(FieldIntent.Set,item.After.Fields[field]);
                }
                var id=item.CanProposeRecoveryId ? item.EditableRecoveryId : item.RemoteId ?? item.Before?.Identity?.RemoteId;
                if(recovering)
                {
                    // An interrupted request is never automatically repeated.
                    var found=id==null?null:await api.FindRow(item.Resource,item.After,id,ct);
                    if(item.Kind!=ChangeKind.Delete && found!=null && api.Matches(item,found,item.After,false)) await Complete(item,found,queries);
                    else if(item.Kind==ChangeKind.Delete && found==null && id!=null && item.Resource is ResourceKind.Fleets or ResourceKind.Aircraft or ResourceKind.Routings) await Complete(item,null,queries);
                    else { item.State=ItemState.Unknown; item.SetMessage(Messages.Define("ApiUnknown")); }
                    await Save(); continue;
                }
                var steps=api.Plan(item);
                if(item.Kind==ChangeKind.Create)
                {
                    if(api.CandidateBlock(item,queries) is string reason)
                    { item.State=ItemState.Pending; item.SetMessage(Messages.Define(reason)); await Save(); continue; }
                    if(PerformanceRun.Current is {} duplicateMetrics) duplicateMetrics.Phase="DuplicateCheck";
                    await foreach(var existing in api.ReadDuplicateCandidatesAsync(item,queries,ct))
                    { PerformanceRun.Current?.Candidate(); if(api.IsDuplicate(item,existing)) throw OperationsAdapter.Block("ApiDuplicate"); }
                    if(api.SessionAirlineId==null) throw OperationsAdapter.Block("ApiConnectFirst");
                }
                if(item.Before!=null)
                {
                    if(PerformanceRun.Current is {} conflictMetrics) conflictMetrics.Phase="ConflictCheck";
                    var current=await api.FindRow(item.Resource,item.Before,id,ct);
                    if(current==null || !api.Matches(item,current,item.Before,true))
                    { item.State=ItemState.Conflict; item.SetMessage(Messages.Define("ApiConflict")); await Save(); continue; }
                }
                if(item.Kind==ChangeKind.Delete)
                {
                    if(PerformanceRun.Current is {} dependencyMetrics) dependencyMetrics.Phase="DependencyCheck";
                    if(await api.HasDeletionDependenciesAsync(item,ct)) throw OperationsAdapter.Block("ApiDependencies");
                }
                item.ApiExpectedValues=OperationsAdapter.ExpectedValues(steps);
                OperationsAdapter.PrepareSteps(item,steps);
                phase=ExecutionPhase.Writing;
                if(PerformanceRun.Current is {} writeMetrics) writeMetrics.Phase="Writing";
                item.State=ItemState.Running; item.SetMessage(Messages.Define("Text_DCC1A22D9E")); await Save();
                api.ReserveCandidate(item,queries);
                item.RemoteId=await api.WriteStepsAsync(item,steps,()=>Save(CheckpointKind.Step),ct); await Save();
                phase=ExecutionPhase.Verifying;
                if(PerformanceRun.Current is {} verifyMetrics) verifyMetrics.Phase="Verifying";
                var verified=await api.FindRow(item.Resource,item.After,item.RemoteId,ct);
                if(item.Kind==ChangeKind.Delete)
                {
                    if(verified==null && item.Resource is ResourceKind.Fleets or ResourceKind.Aircraft or ResourceKind.Routings) await Complete(item,null,queries);
                    else { item.State=ItemState.Unknown; item.SetMessage(Messages.Define("ApiSoftDelete")); }
                }
                else if(verified!=null && api.Matches(item,verified,item.After,false)) await Complete(item,verified,queries);
                else { item.State=ItemState.Unknown; item.SetMessage(Messages.Define("ApiUnknown")); }
            }
            catch(PersistenceException) { throw; }
            catch(ApiAccessException)
            { item.State=phase!=ExecutionPhase.Preflight?ItemState.Unknown:ItemState.Pending; item.SetMessage(Messages.Define("ApiAuth")); job.SetStatus(JobStatus.Paused,Messages.Define("ApiAuth")); await Save(); break; }
            catch(ApiResponseException e)
            {
                item.Diagnostic=e.Diagnostic;
                item.State=phase is ExecutionPhase.Recovering or ExecutionPhase.Verifying || item.CreateCompleted || item.WriteAccepted
                    || phase==ExecutionPhase.Writing && (int)e.Status>=500 ? ItemState.Unknown
                    : phase==ExecutionPhase.Preflight && (e.Status==HttpStatusCode.TooManyRequests || (int)e.Status>=500) ? ItemState.Pending : ItemState.Failed;
                item.SetMessage(Messages.Define("ApiHttp",("status",(int)e.Status)));
            }
            catch(Exception e) when(e is OperationCanceledException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
            { item.State=phase!=ExecutionPhase.Preflight?ItemState.Unknown:ItemState.Pending; item.SetMessage(Messages.Define(phase==ExecutionPhase.Preflight?"ApiPreflight":"ApiUnknown")); }
            catch(Exception e)
            { item.State=phase!=ExecutionPhase.Preflight?ItemState.Unknown:ItemState.Failed; item.SetMessage(MessageErrors.Describe(e)); }
            if(item.State==ItemState.Failed && !item.CreateCompleted && !item.WriteAccepted) queries.Release(item);
            await Save();
            if(ct.IsCancellationRequested)
            { job.SetStatus(JobStatus.Canceled,Messages.Define("Text_6F312AE253")); break; }
        }
        active=null;
        if(job.StatusCode==JobStatus.Running) job.SetStatus(job.Items.All(i=>i.State==ItemState.Succeeded)?JobStatus.Completed:JobStatus.NeedsAttention,Messages.Define(job.Items.All(i=>i.State==ItemState.Succeeded)?"Text_140197D868":"Text_E9008C53CF"));
        await Save();
    }
    static void ApplyRebase(Workspace workspace,ChangeItem item,DataRow? verified)
    {
        var data=workspace.Resources[item.Resource];
        data.Snapshot.RemoveAll(r=>r.LocalId==item.After.LocalId);
        if(verified==null)
        {
            data.Draft.RemoveAll(r=>r.LocalId==item.After.LocalId && Schemas.Deleted(r));

        }
        else
        {
            item.RemoteId=verified.Identity!.RemoteId;
            item.After.Identity=verified.Identity;
            if(item.Kind==ChangeKind.Create) item.CreateCompleted=true;
            item.RecoveryCandidateId=null;
            verified.LocalId=item.After.LocalId;
            var current=data.Draft.FirstOrDefault(r=>r.LocalId==verified.LocalId);
            if(current!=null)
            {
                var position=data.Draft.IndexOf(current);current=current.Copy();data.Draft[position]=current;
                foreach(var field in verified.Fields.Keys)
                    if(field=="ID" || current.Get(field)==item.After.Get(field) || current.Get(field).Contains("local:")) current.Fields[field]=verified.Get(field);
                current.Identity=verified.Identity; current.RawApiJson=verified.RawApiJson;
                foreach(var p in current.Fields.Where(p=>!verified.Fields.ContainsKey(p.Key))) verified.Fields[p.Key]=p.Value;
            }
            data.Snapshot.Add(verified.Copy());
        }
        item.Rebased=true; item.State=ItemState.Succeeded; item.SetMessage(Messages.Define("Text_631F4DBEC2"));
        data.Undo.Clear(); data.Redo.Clear(); data.SnapshotAt=DateTimeOffset.UtcNow;
        var operation=$"{item.Resource}:{item.Kind}";
        if(!workspace.VerifiedOperations.Contains(operation)) workspace.VerifiedOperations.Add(operation);
    }
}
