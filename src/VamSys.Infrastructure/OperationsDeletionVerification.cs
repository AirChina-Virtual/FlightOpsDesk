using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed partial class OperationsAdapter
{
    internal bool RecordDeletionVerification(ChangeItem item, DataRow? current)
    {
        RecordReadback(item, current);
        var id = item.RemoteId ?? item.Before?.Identity?.RemoteId;
        var relative = id is null ? null : Collection(item.Resource) + "/" + id;
        var absolute = relative is null ? null : BaseUri.AbsolutePath + relative;
        var write = item.DiagnosticDetails; var read = item.ReadbackDetails;
        var step = item.ApiSteps.Count == 1 ? item.ApiSteps[0] : null;
        var proof = IndependentApiEvidence.Covers(item, ApiContracts.Sha256) && IndependentApiEvidence.IsSoftDelete(item)
            && item.WriteAccepted && item.Before?.Identity is {} identity && identity.ConnectionId == ConnectionId
            && identity.RemoteId == id && item.Before.LocalId==item.After.LocalId && item.After.Identity?.RemoteId == id && item.After.Identity.ConnectionId == ConnectionId
            && SessionAirlineId == ConnectionId
            && step is {Method:"DELETE",State:BatchStepState.ResponseReceived} && step.Path == relative && step.Body.Count == 0
            && write is {Method:"DELETE",Status:204} && write.Path == absolute
            && step.StartedAt is not null && step.FinishedAt is not null && step.FinishedAt>=step.StartedAt
            && write.StartedAt>=step.StartedAt && write.FinishedAt>=write.StartedAt && write.FinishedAt<=step.FinishedAt
            && read is {Method:"GET",Status:404} && read.Path == absolute && read.StartedAt >= write.FinishedAt;
        var outcome = proof && current is null ? "ApiInvisible" : current is not null ? "StillReadable" : "MissingWriteEvidence";
        item.DeletionVerification = new(outcome, transport.UtcNow, write?.Status, read?.Status,
            item.State == ItemState.ManuallyConfirmed || item.DeletionVerification?.WasManuallyConfirmed == true);
        if(proof && current is null) item.VerificationDifferences=[];
        return proof && current is null;
    }
    // For display only; whether the record is unchanged is decided by Matches(before: true).
    internal List<ApiFieldDifference> BeforeDifferences(ChangeItem item, DataRow current)
        => Maps[item.Resource].Keys.Where(f => !Equivalent(f, item.Before?.Get(f) ?? "", current.Get(f)))
            .Select(f => new ApiFieldDifference(f, item.Before?.Get(f), current.Get(f))).ToList();
    internal async Task EnsureReadConnectionAsync(CancellationToken ct)
    {
        if(SessionAirlineId == ConnectionId)return;
        await IdentifyAirlineAsync(ct);
        if(SessionAirlineId != ConnectionId)throw Block("ApiConnectFirst");
    }
}

// What a read-only GET found for a soft delete without a write receipt. Candidate is the item with the read evidence.
public sealed record NoReceiptReview(ChangeItem Item, ChangeItem Candidate, DataRow? Current, bool MatchesBefore, IReadOnlyList<ApiFieldDifference> Differences)
{
    public bool CanConfirmDeleted => Current == null;
    public bool CanMarkUnchanged => Current != null && MatchesBefore;
}

// Separate from ExecuteAsync: this path cannot submit Pending writes, even in a
// mixed job. Previously rebased/manual items receive evidence without row edits.
public sealed class OperationsDeletionVerifier(OperationsAdapter api, Workspace workspace)
{
    public async Task ConfirmManuallyAsync(BatchJob job,ChangeItem item,IBatchCheckpointWriter writer,CancellationToken ct)
    {
        if(job.Mode!=RunMode.Online||workspace.Mode!=RunMode.Online||!workspace.Jobs.Contains(job)||!job.Items.Contains(item)||item.State!=ItemState.Unknown||!item.WriteAccepted||!IndependentApiEvidence.IsSoftDelete(item))
            throw OperationsAdapter.Block("ApiUnknown");
        if(workspace.AirlineId is not {} owner||item.Before?.Identity is not {} original||item.After.Identity is not {} target
            ||original.ConnectionId!=owner||target.ConnectionId!=owner||original.RemoteId!=target.RemoteId||item.Before!.LocalId!=item.After.LocalId)throw OperationsAdapter.Block("ApiWrongVa");
        ct.ThrowIfCancellationRequested();
        var candidate=System.Text.Json.JsonSerializer.Deserialize<ChangeItem>(System.Text.Json.JsonSerializer.Serialize(item))!;
        candidate.State=ItemState.ManuallyConfirmed;candidate.Rebased=true;candidate.SetMessage(Messages.Define("ApiManualResult"));
        if(candidate.DeletionVerification is {} evidence)candidate.DeletionVerification=evidence with{WasManuallyConfirmed=true};
        var header=new BatchJob{Id=job.Id,Created=job.Created,Mode=job.Mode,Items=job.Items};
        header.SetStatus(JobStatus.NeedsAttention,Messages.Define("ApiManualResult"));
        var data=workspace.Resources[item.Resource];
        var delta=new ResourceRebaseDelta(item.Resource,item.After.LocalId,new(RowMutationKind.Delete),
            new(data.FindDraftRow(item.After.LocalId) is {} draft&&Schemas.Deleted(draft)?RowMutationKind.Delete:RowMutationKind.Keep),
            DateTimeOffset.UtcNow,true,null);
        try{await writer.WriteAsync(new(CheckpointKind.Rebase,header,candidate,Delta:delta),CancellationToken.None);}
        catch(Exception error){throw new PersistenceException(error);}
        data.Snapshot.RemoveAll(r=>r.LocalId==item.After.LocalId);
        if(delta.Draft.Action==RowMutationKind.Delete)data.Draft.RemoveAll(r=>r.LocalId==item.After.LocalId);
        data.ClearHistory(invalidateIndex:delta.Draft.Action==RowMutationKind.Delete);data.SnapshotAt=delta.SnapshotAt;
        job.Items[job.Items.IndexOf(item)]=candidate;job.StatusCode=header.StatusCode;job.Status=header.Status;job.Description=header.Description;
        api.Forget(item.Resource,original.RemoteId);
    }

    // A soft delete the server never answered (timeout or 5xx) has no write receipt, so neither the
    // 204/404 proof nor ConfirmManuallyAsync applies. A read-only GET decides what the user may confirm:
    // 404 marks it ManuallyConfirmed, a record that still reads exactly as before the change may be marked
    // not applied (Failed, draft kept). Evidence says so and it is never a verified operation.
    public static bool CanConfirmWithoutReceipt(ChangeItem item)=>item.State==ItemState.Unknown&&!item.WriteAccepted&&!item.CreateCompleted&&IndependentApiEvidence.IsSoftDelete(item);
    ResourceIdentity RequireNoReceipt(BatchJob job,ChangeItem item)
    {
        if(job.Mode!=RunMode.Online||workspace.Mode!=RunMode.Online||!workspace.Jobs.Contains(job)||!job.Items.Contains(item)||!CanConfirmWithoutReceipt(item))
            throw OperationsAdapter.Block("ApiUnknown");
        if(workspace.AirlineId is not {} owner||item.Before?.Identity is not {} original||item.After.Identity is not {} target
            ||original.ConnectionId!=owner||target.ConnectionId!=owner||original.RemoteId!=target.RemoteId||item.Before!.LocalId!=item.After.LocalId)throw OperationsAdapter.Block("ApiWrongVa");
        return original;
    }
    static ChangeItem Clone(ChangeItem item)=>System.Text.Json.JsonSerializer.Deserialize<ChangeItem>(System.Text.Json.JsonSerializer.Serialize(item))!;
    public async Task<NoReceiptReview> InspectWithoutReceiptAsync(BatchJob job,ChangeItem item,CancellationToken ct)
    {
        var original=RequireNoReceipt(job,item);
        await api.EnsureReadConnectionAsync(ct);
        var candidate=Clone(item);
        var found=await api.FindRow(item.Resource,item.After,original.RemoteId,ct);
        api.RecordDeletionVerification(candidate,found);
        if(candidate.ReadbackDetails is {} read)candidate.ReadbackDetails=read with{Phase="Recovering"};
        var unchanged=found!=null&&api.Matches(candidate,found,candidate.Before!,true);
        return new(item,candidate,found,unchanged,found==null?[]:api.BeforeDifferences(candidate,found));
    }
    public async Task<bool> ConfirmWithoutReceiptAsync(BatchJob job,ChangeItem item,IBatchCheckpointWriter writer,CancellationToken ct)
    {
        var review=await InspectWithoutReceiptAsync(job,item,ct);
        if(!review.CanConfirmDeleted)return false;
        await ConfirmWithoutReceiptAsync(job,review,writer);return true;
    }
    public async Task MarkUnchangedAsync(BatchJob job,NoReceiptReview review,IBatchCheckpointWriter writer)
    {
        var item=review.Item;RequireNoReceipt(job,item);
        if(!review.CanMarkUnchanged)throw OperationsAdapter.Block("ApiUnknown");
        var candidate=Clone(review.Candidate);
        // Like a proven not-applied write: the step returns to prepared so the item no longer counts as unresolved.
        foreach(var step in candidate.ApiSteps.Where(s=>s.State==BatchStepState.InFlight))step.State=BatchStepState.Prepared;
        var now=DateTimeOffset.UtcNow;var status=candidate.ReadbackDetails?.Status;
        candidate.DeletionVerification=new("UnchangedSinceBefore",now,item.DiagnosticDetails?.Status,status,true);
        candidate.ManualResolution=new("SoftDeleteUnchanged",now,status,false);
        candidate.State=ItemState.Failed;candidate.SetMessage(Messages.Define("ApiSoftDeleteUnchanged"));
        var header=new BatchJob{Id=job.Id,Created=job.Created,Mode=job.Mode,Items=job.Items};
        header.SetStatus(JobStatus.NeedsAttention,Messages.Define("Text_E9008C53CF"));
        try{await writer.WriteAsync(new(CheckpointKind.Task,header,candidate),CancellationToken.None);}
        catch(Exception error){throw new PersistenceException(error);}
        job.Items[job.Items.IndexOf(item)]=candidate;job.StatusCode=header.StatusCode;job.Status=header.Status;job.Description=header.Description;
    }
    public async Task ConfirmWithoutReceiptAsync(BatchJob job,NoReceiptReview review,IBatchCheckpointWriter writer)
    {
        var item=review.Item;var original=RequireNoReceipt(job,item);
        if(!review.CanConfirmDeleted)throw OperationsAdapter.Block("ApiUnknown");
        var candidate=Clone(review.Candidate);
        var now=DateTimeOffset.UtcNow;var status=candidate.ReadbackDetails?.Status;
        candidate.DeletionVerification=new("ManualWithoutWriteReceipt",now,item.DiagnosticDetails?.Status,status,true);
        candidate.ManualResolution=new("SoftDeleteWithoutReceipt",now,status,false);
        candidate.State=ItemState.ManuallyConfirmed;candidate.Rebased=true;candidate.SetMessage(Messages.Define("ApiManualDeleteNoReceipt"));
        var header=new BatchJob{Id=job.Id,Created=job.Created,Mode=job.Mode,Items=job.Items};
        header.SetStatus(JobStatus.NeedsAttention,Messages.Define("ApiManualResult"));
        var data=workspace.Resources[item.Resource];
        var delta=new ResourceRebaseDelta(item.Resource,item.After.LocalId,new(RowMutationKind.Delete),
            new(data.FindDraftRow(item.After.LocalId) is {} draft&&Schemas.Deleted(draft)?RowMutationKind.Delete:RowMutationKind.Keep),now,true,null);
        try{await writer.WriteAsync(new(CheckpointKind.Rebase,header,candidate,Delta:delta),CancellationToken.None);}
        catch(Exception error){throw new PersistenceException(error);}
        data.Snapshot.RemoveAll(r=>r.LocalId==item.After.LocalId);
        if(delta.Draft.Action==RowMutationKind.Delete)data.Draft.RemoveAll(r=>r.LocalId==item.After.LocalId);
        data.ClearHistory(invalidateIndex:delta.Draft.Action==RowMutationKind.Delete);data.SnapshotAt=delta.SnapshotAt;
        job.Items[job.Items.IndexOf(item)]=candidate;job.StatusCode=header.StatusCode;job.Status=header.Status;job.Description=header.Description;
        api.Forget(item.Resource,original.RemoteId);
    }

    public async Task VerifyAsync(BatchJob job, IBatchCheckpointWriter writer, CancellationToken ct, Action<BatchProgress>? progress = null)
    {
        if(job.Mode != RunMode.Online || workspace.Mode != RunMode.Online || !workspace.Jobs.Contains(job))throw OperationsAdapter.Block("TaskStartBlocked");
        await api.EnsureReadConnectionAsync(ct);
        var positions=job.Items.Select((item,index)=>(item.Id,index)).ToDictionary(p=>p.Id,p=>p.index);
        var remaining=job.Items.Count(i=>i.State!=ItemState.Succeeded);
        var unknown=job.Items.Count(i=>i.State==ItemState.Unknown);
        foreach(var item in job.Items.Where(IndependentApiEvidence.CanCheckDeletion).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var candidate=System.Text.Json.JsonSerializer.Deserialize<ChangeItem>(System.Text.Json.JsonSerializer.Serialize(item))!;
            var id=item.RemoteId??item.Before?.Identity?.RemoteId;
            if(id is null || item.Before?.Identity?.ConnectionId!=workspace.AirlineId || item.After.Identity?.RemoteId!=id)continue;
            var complete=false;var accessDenied=false;
            try
            {
                var found=await api.FindRow(item.Resource,item.After,id,ct);
                complete=api.RecordDeletionVerification(candidate,found);
                if(candidate.ReadbackDetails is {} read)candidate.ReadbackDetails=read with{Phase="Recovering"};
            }
            catch(Exception error) when(error is ApiResponseException or ApiAccessException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
            {
                var details=error is ApiResponseException response?response.Details:error is ApiAccessException access?access.Details:null;
                accessDenied=error is ApiAccessException;
                if(details!=null)candidate.ReadbackDetails=details with{Phase="Recovering"};
                candidate.DeletionVerification=new("ReadFailed",DateTimeOffset.UtcNow,item.DiagnosticDetails?.Status,details?.Status,
                    item.State==ItemState.ManuallyConfirmed||item.DeletionVerification?.WasManuallyConfirmed==true);
            }
            if(complete)
            {
                candidate.State=ItemState.Succeeded;candidate.Rebased=true;
                candidate.SetMessage(Messages.Define("ApiDeletedInvisible"));
            }
            var remainingAfter=remaining+(candidate.State==ItemState.Succeeded?0:1)-(item.State==ItemState.Succeeded?0:1);
            var unknownAfter=unknown+(candidate.State==ItemState.Unknown?1:0)-(item.State==ItemState.Unknown?1:0);
            var staged=new BatchJob{Id=job.Id,Created=job.Created,Mode=job.Mode,Items=job.Items,
                StatusCode=job.StatusCode,Status=job.Status,Description=job.Description};
            if(remainingAfter==0)staged.SetStatus(JobStatus.Completed,Messages.Define("Text_140197D868"));
            else if(unknownAfter>0)staged.SetStatus(JobStatus.NeedsAttention,Messages.Define("Text_E9008C53CF"));
            if(accessDenied)staged.SetStatus(JobStatus.Paused,Messages.Define("ApiAuth"));
            var data=workspace.Resources[item.Resource];
            // Do not rebase a manually confirmed historical row again or disturb
            // new drafts/history. Unknown rows only remove their deletion draft.
            var remove=complete&&!item.Rebased;
            ResourceRebaseDelta? delta=complete?new(item.Resource,item.After.LocalId,
                new(remove?RowMutationKind.Delete:RowMutationKind.Keep),
                new(remove&&data.FindDraftRow(item.After.LocalId) is {} draft&&Schemas.Deleted(draft)?RowMutationKind.Delete:RowMutationKind.Keep),
                data.SnapshotAt??DateTimeOffset.UtcNow,remove,ApiOperationPolicy.Key(candidate)):null;
            try {await writer.WriteAsync(new(delta is null?CheckpointKind.Task:CheckpointKind.Rebase,staged,candidate,Delta:delta),CancellationToken.None);}
            catch(Exception error){throw new PersistenceException(error);}
            if(delta!=null)
            {
                if(delta.Snapshot.Action==RowMutationKind.Delete)data.Snapshot.RemoveAll(r=>r.LocalId==delta.LocalId);
                if(delta.Draft.Action==RowMutationKind.Delete)data.Draft.RemoveAll(r=>r.LocalId==delta.LocalId);
                if(delta.ClearHistory)data.ClearHistory(invalidateIndex:true);
                data.SnapshotAt=delta.SnapshotAt;
                if(!workspace.VerifiedOperations.Contains(delta.VerifiedOperation!))workspace.VerifiedOperations.Add(delta.VerifiedOperation!);
                api.Forget(item.Resource,id);
            }
            job.Items[positions[item.Id]]=candidate;remaining=remainingAfter;unknown=unknownAfter;
            job.StatusCode=staged.StatusCode;job.Status=staged.Status;job.Description=staged.Description;
            progress?.Invoke(new(workspace.Id,job.Id,item.Id,true));
            if(accessDenied)break;
        }
    }
}
