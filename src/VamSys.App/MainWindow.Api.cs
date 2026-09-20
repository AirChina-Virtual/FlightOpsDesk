using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.App;
public sealed partial class MainWindow
{
    readonly Dictionary<Guid,(HttpClient Client,OperationsAdapter Api)> apiSessions = [];
    OperationsAdapter Api()
    {
        if(apiSessions.TryGetValue(workspace.Id,out var session)) return session.Api;
        var raw=store.LoadSecret(workspace.Id) ?? throw OperationsAdapter.Block("ApiConnectFirst");
        var credentials=JsonSerializer.Deserialize<OperationsCredentials>(raw) ?? throw OperationsAdapter.Block("ApiConnectFirst");
        var client=OperationsTransport.CreateHttpClient();
        // Conservative global pool also covers clients whose airline is not identified yet.
        var transport=new OperationsTransport(client,OperationsAdapter.BaseUri,"operations-shared",TokenProvider.ClientCredentials(client,credentials));
        var api=new OperationsAdapter(transport,workspace); apiSessions[workspace.Id]=(client,api); return api;
    }
    async Task ConnectApi()
    {
        if(busy) return;
        await Work(async()=>
        {
            running=new();
            try
            {
                // A documented list call checks the token, permissions and airline identity.
                await foreach(var row in Api().ReadAllAsync(ResourceKind.Fleets,running.Token)) break;
                if(Api().SessionAirlineId==null) await foreach(var row in Api().ReadAllAsync(ResourceKind.Airports,running.Token)) break;
                workspace.ConnectedAt=DateTimeOffset.UtcNow; workspace.Mode=RunMode.Online;
                if(workspace.ContractHash!=ApiContracts.Sha256) workspace.VerifiedOperations.Clear();
                workspace.ContractHash=ApiContracts.Sha256;
                await Save(); Status(()=>L("ApiConnected",("id",workspace.AirlineId ?? "?")));
            }
            finally { running.Dispose(); running=null; }
        });
    }
    async Task RefreshApi()
    {
        if(busy) return;
        if(workspace.Mode!=RunMode.Online) { Navigation.SelectedItem=Navigation.MenuItems[5]; Status(()=>L("ApiConnectFirst")); return; }
        if(workspace.Jobs.Any(j=>j.Mode==RunMode.Online && j.Items.Any(i=>i.State is ItemState.Pending or ItemState.Running or ItemState.Unknown))) throw OperationsAdapter.Block("ApiPendingJob");
        await Work(async()=>
        {
            running=new();
            try
            {
                var api=Api();
                // Stage a complete response before touching snapshots or drafts.
                var incoming=new Dictionary<ResourceKind,List<DataRow>>();
                foreach(var kind in Enum.GetValues<ResourceKind>())
                {
                    var rows=new List<DataRow>();
                    await foreach(var row in api.ReadAllAsync(kind,running.Token))
                    {
                        rows.Add(row);
                        if(rows.Count%100==0) { var count=rows.Count; Status(()=>L("ApiLoading",("resource",L(Schemas.All[kind].Name)),("count",count))); }
                    }
                    incoming[kind]=rows;
                }
                var staged=Workspace.Deserialize(workspace.Serialize());
                foreach(var (kind,rows) in incoming) SnapshotMerger.Merge(kind,staged.Resources[kind],rows);
                workspace.Resources=staged.Resources;
                foreach(var kind in incoming.Keys) if(!workspace.VerifiedOperations.Contains(kind+":Read")) workspace.VerifiedOperations.Add(kind+":Read");
                await Save(); Status(()=>L("ApiRefreshed",("count",workspace.Resources.Sum(p=>p.Value.Conflicts.Count))));
            }
            finally { running.Dispose(); running=null; }
        });
    }
    async Task ResolveApiConflicts()
    {
        if(busy) return;
        foreach(var conflict in Data.Conflicts.ToList())
        {
            var row=Data.Draft.FirstOrDefault(r=>r.LocalId==conflict.RowId);
            if(row==null) { Data.Conflicts.Remove(conflict); continue; }
            var content=Stack(Text(()=>Schemas.Key(resource,row)+" · "+conflict.Field),Text(()=>L("ApiConflictValues",("old",conflict.Original??"∅"),("local",conflict.Local??"∅"),("remote",conflict.Remote??"∅"))));
            var dialog=new ContentDialog { XamlRoot=Content.XamlRoot,Title=L("ApiConflict"),Content=content,PrimaryButtonText=L("ApiKeepLocal"),SecondaryButtonText=L("ApiUseRemote"),CloseButtonText=L("Text_2CD0F3BE87"),DefaultButton=ContentDialogButton.Close,IsPrimaryButtonEnabled=conflict.Field!="*" };
            var choice=await dialog.ShowAsync(); if(choice==ContentDialogResult.None) break;
            if(choice==ContentDialogResult.Secondary)
            { if(conflict.Field=="*") Data.Draft.Remove(row); else row.Fields[conflict.Field]=conflict.Remote??""; }
            Data.Conflicts.Remove(conflict); await Save();
        }
        Render();
    }
    string ApiReview(ChangeItem change)
    {
        var text=$"{L(Schemas.All[change.Resource].Name)} · {EnumText(change.Kind)} · {Schemas.Key(change.Resource,change.After)}\n" + string.Join("; ",change.Fields.Select(f=>$"{f.Key}: {change.Before?.Get(f.Key)??"∅"} → {f.Value.Value}"));
        try
        {
            if(change.Dependencies.Count>0) return text+"\n"+L("ApiDependent",("count",change.Dependencies.Count));
            var steps=Api().Plan(change);
            return text+"\n"+string.Join(" → ",steps.Select(s=>$"{s.Method} {s.Path}"));
        }
        catch(Exception e) { return text+"\n"+L(MessageErrors.Describe(e)); }
    }
    async Task StartApi()
    {
        if(busy || workspace.Mode!=RunMode.Online) return;
        if(workspace.Resources.Values.Any(d=>d.Conflicts.Count>0)) throw OperationsAdapter.Block("ApiConflict");
        if(workspace.Jobs.Any(j=>j.Mode==RunMode.Online && j.Items.Any(i=>i.State is ItemState.Pending or ItemState.Running or ItemState.Unknown))) throw OperationsAdapter.Block("ApiPendingJob");
        var changes=planner.PlanWorkspace(workspace); if(changes.Count==0) return;
        var api=Api(); var allowed=new List<ChangeItem>(); var blocked=new List<string>();
        if(api.SessionAirlineId==null) throw OperationsAdapter.Block("ApiConnectFirst");
        foreach(var c in changes)
        {
            try { api.PreviewPlan(c); allowed.Add(c); }
            catch(Exception e) { blocked.Add($"{Schemas.Key(c.Resource,c.After)}: {L(MessageErrors.Describe(e))}"); }
        }
        if(blocked.Count>0) await Confirm(L("ApiBlocked"),new ScrollViewer { MaxHeight=350,Content=Text(()=>string.Join("\n",blocked)) },L("Text_3FD47EDCE4"));
        if(allowed.Count==0) return;
        // Unvalidated operations enter explicit small-sample verification, never automatic bulk enablement.
        var samples=allowed.Where(c=>!workspace.VerifiedOperations.Contains($"{c.Resource}:{c.Kind}")).GroupBy(c=>$"{c.Resource}:{c.Kind}").SelectMany(g=>g.Take(3)).ToHashSet();
        allowed=allowed.Where(c=>workspace.VerifiedOperations.Contains($"{c.Resource}:{c.Kind}")||samples.Contains(c)).ToList();
        bool changed;
        do { changed=allowed.RemoveAll(c=>c.Dependencies.Any(id=>!allowed.Any(d=>d.Id==id)))>0; } while(changed);
        if(allowed.Count==0) throw OperationsAdapter.Block("ApiDependencies");
        if(!await Confirm(L("ApiSubmit"),new ListView { MaxHeight=440,Header=Text(()=>L("ApiSubmitScope",("va",workspace.Name),("id",workspace.AirlineId),("count",allowed.Count))),ItemsSource=allowed.Select(ApiReview).ToList() },L("ApiSubmit"))) return;
        var deletes=allowed.Where(c=>c.Kind==ChangeKind.Delete).ToList();
        if(deletes.Count>0 && !await Confirm(L("Text_5D6042B30E"),Text(()=>L("ApiDeleteConfirm",("count",deletes.Count))+"\n"+string.Join("\n",deletes.Select(c=>$"{c.Resource}: {Schemas.Key(c.Resource,c.After)}"))),L("ApiSubmit"))) return;
        var job=new BatchJob { Mode=RunMode.Online,Items=allowed }; workspace.Jobs.Add(job); await Save(); await ExecuteApi(job);
    }
    async Task ExecuteApi(BatchJob job)
    {
        if(busy || workspace.Mode!=RunMode.Online || job.Mode!=RunMode.Online) return;
        await Work(async()=>
        {
            running=new();
            try
            {
                Navigation.SelectedItem=Navigation.MenuItems[4]; Render();
                await new OperationsBatchExecutor(Api(),workspace).ExecuteAsync(job,async()=>{ await Save(); if(page=="tasks") Render(); },running.Token);
                Status(()=>JobText(job));
            }
            finally { running.Dispose(); running=null; }
        });
    }
    async Task ReviewUnknown(BatchJob job)
    {
        if(busy || workspace.Mode!=RunMode.Online) return;
        foreach(var item in job.Items.Where(i=>i.State==ItemState.Unknown).ToList())
        {
            if(item.Kind==ChangeKind.Create && item.RemoteId==null)
            {
                var id=new TextBox { Header="Remote ID" };
                if(!await Confirm(L("ApiUnknownReview"),Stack(Text(()=>ApiReview(item)),Text(()=>L("ApiRecoverId")),id),L("ApiCheckRemote"))) continue;
                if(!long.TryParse(id.Text,out var number)||number<=0) throw OperationsAdapter.Block("ApiInvalid","Remote ID");
                item.RemoteId=number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Save(); await ExecuteApi(job);
            }
            else if(item.Kind==ChangeKind.Delete && item.WriteAccepted && item.Resource is ResourceKind.Airports or ResourceKind.Routes)
            {
                if(!await Confirm(L("ApiUnknownReview"),Text(()=>L("ApiManualDelete",("id",item.RemoteId ?? item.After.Identity?.RemoteId))),L("ApiManualConfirm"))) continue;
                item.State=ItemState.ManuallyConfirmed; item.SetMessage(Messages.Define("ApiManualResult")); item.Rebased=true;
                var data=workspace.Resources[item.Resource];data.Draft.RemoveAll(r=>r.LocalId==item.After.LocalId);data.Snapshot.RemoveAll(r=>r.LocalId==item.After.LocalId);data.Undo.Clear();data.Redo.Clear();
                job.SetStatus(JobStatus.NeedsAttention,Messages.Define("ApiManualResult")); await Save();
            }
            else await Confirm(L("ApiUnknownReview"),Text(()=>L("ApiUnknown")+"\n"+ApiReview(item)),L("Text_3FD47EDCE4"));
        }
        Render();
    }
}
