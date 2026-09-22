using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed partial class WorkspaceStore
{
    static readonly JsonSerializerOptions withoutCollections=CreateOptions();
    static JsonSerializerOptions CreateOptions()
    {
        var resolver=new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info=>{
            if(info.Type!=typeof(Workspace) && info.Type!=typeof(BatchJob)) return;
            var property=info.Properties.Single(p=>p.Name==(info.Type==typeof(Workspace)?"Jobs":"Items"));
            info.Properties.Remove(property);
            if(info.Type==typeof(Workspace))info.Properties.Remove(info.Properties.Single(p=>p.Name=="Resources"));
        });
        return new(){TypeInfoResolver=resolver};
    }
    internal sealed record StoredItem(Guid Id,int Ordinal,string Json);
    internal sealed record StoredJob(Guid Id,int Ordinal,string Json,List<StoredItem> Items);
    internal sealed record FullPayload(Guid Id,string Name,long Revision,string Json,List<StoredJob> Jobs,List<StoredResource> Resources);
    static string Encode<T>(T value,bool header=false)=>JsonSerializer.Serialize(value,header?withoutCollections:null);
    static void Measured(long started,params string?[] json)
        => PerformanceRun.Current?.Serialized(json.Where(j=>j!=null).Sum(j=>(long)Encoding.UTF8.GetByteCount(j!)),Stopwatch.GetElapsedTime(started));
    internal static FullPayload Capture(Workspace w,bool measure=true)
    {
        var start=Stopwatch.GetTimestamp();
        var jobs=w.Jobs.Select((j,n)=>new StoredJob(j.Id,n,Encode(j,true),j.Items.Select((i,k)=>new StoredItem(i.Id,k,Encode(i))).ToList())).ToList();
        var resources=CaptureResources(w);var json=Encode(w,true);
        if(measure) Measured(start,[json,..jobs.SelectMany(j=>new[]{j.Json}.Concat(j.Items.Select(i=>i.Json))),..resources.SelectMany(r=>new[]{r.Columns,r.Conflicts,r.Undo,r.Redo}.Concat(r.Rows.Select(row=>row.Json)))]);
        return new(w.Id,w.Name,w.StorageRevision,json,jobs,resources);
    }
    static void WriteJobs(SqliteConnection c,SqliteTransaction tx,FullPayload p)
    {
        Execute(c,tx,"DELETE FROM batch_jobs WHERE workspace_id=$w",("$w",p.Id.ToString()));
        foreach(var job in p.Jobs)
        {
            Execute(c,tx,"INSERT INTO batch_jobs(workspace_id,id,ordinal,json) VALUES($w,$j,$n,$json)",("$w",p.Id.ToString()),("$j",job.Id.ToString()),("$n",job.Ordinal),("$json",job.Json));
            foreach(var item in job.Items)
                Execute(c,tx,"INSERT INTO batch_items(workspace_id,job_id,id,ordinal,json) VALUES($w,$j,$i,$n,$json)",("$w",p.Id.ToString()),("$j",job.Id.ToString()),("$i",item.Id.ToString()),("$n",item.Ordinal),("$json",item.Json));
        }
    }
    long Commit(Guid id,long expected,string kind,Action<SqliteConnection,SqliteTransaction> write, bool allowNew=false)
    {
        lock(writeLock)
        {
            var metrics=PerformanceRun.Current;metrics?.Saving();metrics?.Checkpoint(kind);var start=Stopwatch.GetTimestamp();var committed=false;
            try
            {
                using var c=Open();using var tx=c.BeginTransaction();
                var revision=Scalar(c,tx,"SELECT revision FROM workspace_revisions WHERE workspace_id=$w",("$w",id.ToString()));
                if(revision==null ? !allowNew || expected!=0 : Convert.ToInt64(revision)!=expected) throw OperationsAdapter.Block("StorageStale");
                write(c,tx);
                var next=expected+1;
                Execute(c,tx,"INSERT INTO workspace_revisions(workspace_id,revision) VALUES($w,$r) ON CONFLICT(workspace_id) DO UPDATE SET revision=$r",("$w",id.ToString()),("$r",next));
                Barrier?.Invoke(kind+":BeforeCommit");tx.Commit();committed=true;
                metrics?.Saved(true,Stopwatch.GetElapsedTime(start));
                Barrier?.Invoke(kind+":AfterCommit");
                return next;
            }
            catch {if(!committed)metrics?.Saved(false,Stopwatch.GetElapsedTime(start));throw;}
        }
    }
    internal long SavePayload(FullPayload p,byte[]? secret=null)
        => Commit(p.Id,p.Revision,"Full", (c,tx)=>{
            Execute(c,tx,"INSERT INTO workspaces(id,name,json,format_version) VALUES($id,$name,$json,3) ON CONFLICT(id) DO UPDATE SET name=$name,json=$json,format_version=3",
                ("$id",p.Id.ToString()),("$name",p.Name),("$json",p.Json));
            WriteJobs(c,tx,p);WriteResources(c,tx,p);
            if(secret!=null) Execute(c,tx,"INSERT INTO secrets(id,value) VALUES($id,$s) ON CONFLICT(id) DO UPDATE SET value=$s",("$id",p.Id.ToString()),("$s",secret));
        },true);
    public void Save(Workspace w) { var p=Capture(w); w.StorageRevision=SavePayload(p); }
    public async Task SaveAsync(Workspace w)
    {var p=Capture(w);w.StorageRevision=await Task.Run(()=>SavePayload(p));}
    // Compatibility entry point for callers exchanging a complete workspace JSON.
    public void SaveJson(Guid id,string name,string json)
    {
        var w=Workspace.Deserialize(json);
        if(w.Id!=id || w.Name!=name) throw OperationsAdapter.Block("StorageInvalid");
        Save(w);
    }
    public static string SerializeForSave(Workspace w)=>w.Serialize();
    public IBatchCheckpointWriter CheckpointWriter(Workspace w)=>new Writer(this,w);
    sealed class Writer(WorkspaceStore store,Workspace workspace) : IBatchCheckpointWriter
    {
        public async Task WriteAsync(BatchCheckpoint checkpoint,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var start=Stopwatch.GetTimestamp();var expected=workspace.StorageRevision;
            var header=Encode(checkpoint.Job,true);var item=checkpoint.Item==null?null:Encode(checkpoint.Item);
            var delta=checkpoint.Delta;
            var full=checkpoint.RebasedWorkspace==null?null:Capture(checkpoint.RebasedWorkspace);
            if((checkpoint.Kind==CheckpointKind.Rebase)!=(delta!=null||full!=null) || delta!=null&&full!=null || checkpoint.Kind==CheckpointKind.Rebase&&item==null || full!=null&&full.Id!=workspace.Id)throw OperationsAdapter.Block("StorageInvalid");
            if(delta!=null && (checkpoint.Item!.Resource!=delta.Resource || checkpoint.Item.After.LocalId!=delta.LocalId || new[]{delta.Snapshot,delta.Draft}.Any(m=>m.Action==RowMutationKind.Upsert?(m.Row==null||m.Row.LocalId!=delta.LocalId):m.Row!=null)))throw OperationsAdapter.Block("StorageInvalid");
            string? snapshot=delta?.Snapshot.Row is {} sr?Encode(sr):null, draft=delta?.Draft.Row is {} dr?Encode(dr):null;
            string? root=null;
            if(delta?.VerifiedOperation!=null)
            {
                var staged=workspace.StageResource(delta.Resource,workspace.Resources[delta.Resource]);
                if(!staged.VerifiedOperations.Contains(delta.VerifiedOperation))staged.VerifiedOperations.Add(delta.VerifiedOperation);
                root=Encode(staged,true);
            }
            Measured(start,header,item,root,snapshot,draft);
            var w=workspace.Id.ToString();var j=checkpoint.Job.Id.ToString();var i=checkpoint.Item?.Id.ToString();
            var next=await Task.Run(()=>store.Commit(workspace.Id,expected,checkpoint.Kind.ToString(),(c,tx)=>{
                if(Execute(c,tx,"UPDATE batch_jobs SET json=$json WHERE workspace_id=$w AND id=$j",("$json",header),("$w",w),("$j",j))!=1) throw OperationsAdapter.Block("StorageInvalid");
                if(item!=null && Execute(c,tx,"UPDATE batch_items SET json=$json WHERE workspace_id=$w AND job_id=$j AND id=$i",("$json",item),("$w",w),("$j",j),("$i",i))!=1) throw OperationsAdapter.Block("StorageInvalid");
                if(full!=null){Execute(c,tx,"UPDATE workspaces SET name=$name,json=$json WHERE id=$w",("$name",full.Name),("$json",full.Json),("$w",w));WriteResources(c,tx,full);}
                if(delta!=null){WriteDelta(c,tx,workspace.Id,delta,snapshot,draft);if(root!=null)Execute(c,tx,"UPDATE workspaces SET json=$json WHERE id=$w",("$json",root),("$w",w));}
            }));
            workspace.StorageRevision=next;
        }
    }
}
