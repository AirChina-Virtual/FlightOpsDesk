using System.Net;
using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed class OperationsBatchExecutor(OperationsAdapter api,Workspace workspace) : IBatchExecutor
{
    public async Task ExecuteAsync(BatchJob job,Func<Task> persist,CancellationToken ct)
    {
        async Task Save() { try { await persist(); } catch(Exception e) { throw new PersistenceException(e); } }
        job.SetStatus(JobStatus.Running,Messages.Define("Text_5026A63B58"));
        foreach(var item in job.Items.OrderBy(i=>i.Kind==ChangeKind.Delete?10-(int)i.Resource:(int)i.Resource))
        {
            if(ct.IsCancellationRequested) { job.SetStatus(JobStatus.Canceled,Messages.Define("Text_6F312AE253")); break; }
            if(item.State is ItemState.Succeeded or ItemState.Conflict or ItemState.Failed or ItemState.ManuallyConfirmed) continue;
            if(item.Dependencies.Any(id=>job.Items.Single(i=>i.Id==id).State!=ItemState.Succeeded))
            { item.State=ItemState.Skipped; item.SetMessage(Messages.Define("Text_3E85DDB213")); await Save(); continue; }
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
                var id=item.RemoteId ?? item.Before?.Identity?.RemoteId;
                if(item.State is ItemState.Running or ItemState.Unknown)
                {
                    // An interrupted request is never automatically repeated.
                    var found=id==null?null:await api.FindRow(item.Resource,item.After,id,ct);
                    if(item.Kind!=ChangeKind.Delete && found!=null && api.Matches(item,found,item.After,false)) Complete(item,found);
                    else if(item.Kind==ChangeKind.Delete && found==null && id!=null && item.Resource is ResourceKind.Fleets or ResourceKind.Aircraft or ResourceKind.Routings) Complete(item,null);
                    else { item.State=ItemState.Unknown; item.SetMessage(Messages.Define("ApiUnknown")); }
                    await Save(); continue;
                }
                api.Plan(item);
                if(item.Kind==ChangeKind.Create)
                {
                    string[] signature=item.Resource switch
                    {
                        ResourceKind.Airports=>["ICAO/IATA"],ResourceKind.Aircraft=>["Registration"],
                        ResourceKind.Routes=>["Departure Airport (ICAO/IATA)","Arrival Airport (ICAO/IATA)","Flight Number"],
                        ResourceKind.Routings=>["Departure Airport (ICAO/IATA)","Arrival Airport (ICAO/IATA)","Route String"],_=>["Name"]
                    };
                    await foreach(var existing in api.ReadAllAsync(item.Resource,ct))
                        if(signature.All(f=>existing.Get(f).Equals(item.After.Get(f),StringComparison.OrdinalIgnoreCase))) throw OperationsAdapter.Block("ApiDuplicate");
                    if(api.SessionAirlineId==null) throw OperationsAdapter.Block("ApiConnectFirst");
                }
                if(item.Before!=null)
                {
                    var current=await api.FindRow(item.Resource,item.Before,id,ct);
                    if(current==null || !api.Matches(item,current,item.Before,true))
                    { item.State=ItemState.Conflict; item.SetMessage(Messages.Define("ApiConflict")); await Save(); continue; }
                }
                if(item.Kind==ChangeKind.Delete)
                {
                    // Refresh related resources before local protection checks. Do not overwrite drafts.
                    var check=Workspace.Deserialize(workspace.Serialize());
                    var dependencies=item.Resource==ResourceKind.Fleets ? new[]{ResourceKind.Aircraft,ResourceKind.Routes} : item.Resource==ResourceKind.Airports ? new[]{ResourceKind.Routings,ResourceKind.Routes} : [];
                    foreach(var kind in dependencies)
                    {
                        var fresh=new List<DataRow>(); await foreach(var row in api.ReadAllAsync(kind,ct)) fresh.Add(row);
                        check.Resources[kind].Draft=fresh;
                    }
                    if(new DeletionService().CheckReferences(check,item.Resource,new HashSet<Guid> { item.After.LocalId }).Count>0) throw OperationsAdapter.Block("ApiDependencies");
                }
                item.State=ItemState.Running; item.SetMessage(Messages.Define("Text_DCC1A22D9E")); await Save();
                item.RemoteId=await api.WriteStepsAsync(item,Save,ct); await Save();
                var verified=await api.FindRow(item.Resource,item.After,item.RemoteId,ct);
                if(item.Kind==ChangeKind.Delete)
                {
                    if(verified==null && item.Resource is ResourceKind.Fleets or ResourceKind.Aircraft or ResourceKind.Routings) Complete(item,null);
                    else { item.State=ItemState.Unknown; item.SetMessage(Messages.Define("ApiSoftDelete")); }
                }
                else if(verified!=null && api.Matches(item,verified,item.After,false)) Complete(item,verified);
                else { item.State=ItemState.Unknown; item.SetMessage(Messages.Define("ApiUnknown")); }
            }
            catch(PersistenceException) { throw; }
            catch(ApiAccessException)
            { item.State=item.State==ItemState.Running?ItemState.Unknown:ItemState.Pending; item.SetMessage(Messages.Define("ApiAuth")); job.SetStatus(JobStatus.Paused,Messages.Define("ApiAuth")); await Save(); break; }
            catch(ApiResponseException e)
            {
                item.Diagnostic=e.Diagnostic;
                item.State=(int)e.Status>=500 || item.CreateCompleted || item.WriteAccepted ? ItemState.Unknown : ItemState.Failed;
                item.SetMessage(Messages.Define("ApiHttp",("status",(int)e.Status)));
            }
            catch(Exception e) when(e is OperationCanceledException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
            { item.State=item.State==ItemState.Running?ItemState.Unknown:ItemState.Pending; item.SetMessage(Messages.Define("ApiUnknown")); }
            catch(Exception e)
            { item.State=item.State==ItemState.Running?ItemState.Unknown:ItemState.Failed; item.SetMessage(MessageErrors.Describe(e)); }
            await Save();
        }
        if(job.StatusCode==JobStatus.Running) job.SetStatus(job.Items.All(i=>i.State==ItemState.Succeeded)?JobStatus.Completed:JobStatus.NeedsAttention,Messages.Define(job.Items.All(i=>i.State==ItemState.Succeeded)?"Text_140197D868":"Text_E9008C53CF"));
        await Save();
    }
    void Complete(ChangeItem item,DataRow? verified)
    {
        var data=workspace.Resources[item.Resource];
        data.Snapshot.RemoveAll(r=>r.LocalId==item.After.LocalId);
        if(verified==null) data.Draft.RemoveAll(r=>r.LocalId==item.After.LocalId && Schemas.Deleted(r));
        else
        {
            verified.LocalId=item.After.LocalId;
            var current=data.Draft.FirstOrDefault(r=>r.LocalId==verified.LocalId);
            if(current!=null)
            {
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
