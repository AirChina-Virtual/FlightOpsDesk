using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using VamSys.Core;
namespace VamSys.Infrastructure;
public sealed partial class WorkspaceStore
{
    internal sealed record StoredRow(int Collection,Guid Id,int Ordinal,string Json);
    internal sealed record StoredResource(ResourceKind Kind,string Columns,string Conflicts,string? SnapshotAt,string Undo,string Redo,List<StoredRow> Rows);
    static List<StoredResource> CaptureResources(Workspace w)=>w.Resources.Select(p=>new StoredResource(p.Key,Encode(p.Value.Columns),Encode(p.Value.Conflicts),p.Value.SnapshotAt?.ToString("O"),Encode(p.Value.Undo),Encode(p.Value.Redo),
        p.Value.Snapshot.Select((r,n)=>new StoredRow(0,r.LocalId,n,Encode(r))).Concat(p.Value.Draft.Select((r,n)=>new StoredRow(1,r.LocalId,n,Encode(r)))).ToList())).ToList();
    static void WriteResources(SqliteConnection c,SqliteTransaction tx,FullPayload p)
    {
        Execute(c,tx,"DELETE FROM resource_metadata WHERE workspace_id=$w",("$w",p.Id.ToString()));
        foreach(var r in p.Resources)
        {
            Execute(c,tx,"INSERT INTO resource_metadata VALUES($w,$r,$cols,$conflicts,$at)",("$w",p.Id.ToString()),("$r",(int)r.Kind),("$cols",r.Columns),("$conflicts",r.Conflicts),("$at",r.SnapshotAt));
            Execute(c,tx,"INSERT INTO resource_history VALUES($w,$r,$undo,$redo)",("$w",p.Id.ToString()),("$r",(int)r.Kind),("$undo",r.Undo),("$redo",r.Redo));
            using var cmd=Command(c,tx,"INSERT INTO resource_rows VALUES($w,$r,$collection,$id,$n,$json)",("$w",p.Id.ToString()),("$r",(int)r.Kind),("$collection",0),("$id",""),("$n",0),("$json",""));
            cmd.Prepare();
            foreach(var row in r.Rows)
            {
                cmd.Parameters["$collection"].Value=row.Collection;cmd.Parameters["$id"].Value=row.Id.ToString();
                cmd.Parameters["$n"].Value=row.Ordinal;cmd.Parameters["$json"].Value=row.Json;cmd.ExecuteNonQuery();
            }
        }
    }
    static void ReadResources(SqliteConnection c,SqliteTransaction tx,Workspace w)
    {
        w.Resources=Enum.GetValues<ResourceKind>().ToDictionary(k=>k,_=>new ResourceData());
        using(var cmd=Command(c,tx,"SELECT resource,columns_json,conflicts_json,snapshot_at,undo_json,redo_json FROM resource_metadata JOIN resource_history USING(workspace_id,resource) WHERE workspace_id=$w",("$w",w.Id.ToString())))
        using(var r=cmd.ExecuteReader())while(r.Read())
        {
            var data=w.Resources[(ResourceKind)r.GetInt32(0)];
            data.Columns=JsonSerializer.Deserialize<List<string>>(r.GetString(1))!;
            data.Conflicts=JsonSerializer.Deserialize<List<MergeConflict>>(r.GetString(2))!;
            data.SnapshotAt=r.IsDBNull(3)?null:DateTimeOffset.Parse(r.GetString(3),CultureInfo.InvariantCulture);
            data.Undo=JsonSerializer.Deserialize<List<List<DataRow>>>(r.GetString(4))!;
            data.Redo=JsonSerializer.Deserialize<List<List<DataRow>>>(r.GetString(5))!;
        }
        using(var cmd=Command(c,tx,"SELECT resource,collection,json FROM resource_rows WHERE workspace_id=$w ORDER BY resource,collection,ordinal",("$w",w.Id.ToString())))
        using(var r=cmd.ExecuteReader())while(r.Read())
        {
            var data=w.Resources[(ResourceKind)r.GetInt32(0)];var rows=r.GetInt32(1)==0?data.Snapshot:data.Draft;
            rows.Add(JsonSerializer.Deserialize<DataRow>(r.GetString(2))!);
        }
    }
    static void WriteDelta(SqliteConnection c,SqliteTransaction tx,Guid workspaceId,ResourceRebaseDelta delta,string? snapshot,string? draft)
    {
        var w=workspaceId.ToString();var kind=(int)delta.Resource;
        if(Execute(c,tx,"UPDATE resource_metadata SET snapshot_at=$at WHERE workspace_id=$w AND resource=$r",("$at",delta.SnapshotAt.ToString("O")),("$w",w),("$r",kind))!=1)throw OperationsAdapter.Block("StorageInvalid");
        void Row(int collection,RowMutation mutation,string? json)
        {
            if(mutation.Action==RowMutationKind.Keep)return;
            var args=new (string,object?)[]{("$w",w),("$r",kind),("$c",collection),("$id",delta.LocalId.ToString())};
            var ordinal=Scalar(c,tx,"SELECT ordinal FROM resource_rows WHERE workspace_id=$w AND resource=$r AND collection=$c AND local_id=$id",args);
            if(mutation.Action==RowMutationKind.Delete || collection==0)Execute(c,tx,"DELETE FROM resource_rows WHERE workspace_id=$w AND resource=$r AND collection=$c AND local_id=$id",args);
            if(mutation.Action==RowMutationKind.Delete)return;
            var position=collection==1&&ordinal!=null?Convert.ToInt64(ordinal):Convert.ToInt64(Scalar(c,tx,"SELECT COALESCE(MAX(ordinal),-1)+1 FROM resource_rows WHERE workspace_id=$w AND resource=$r AND collection=$c",args));
            Execute(c,tx,"INSERT INTO resource_rows VALUES($w,$r,$c,$id,$n,$json) ON CONFLICT(workspace_id,resource,collection,local_id) DO UPDATE SET json=$json",[..args,("$n",position),("$json",json)]);
        }
        Row(0,delta.Snapshot,snapshot);Row(1,delta.Draft,draft);
        if(delta.ClearHistory)Execute(c,tx,"UPDATE resource_history SET undo_json='[]',redo_json='[]' WHERE workspace_id=$w AND resource=$r",("$w",w),("$r",kind));
    }
}