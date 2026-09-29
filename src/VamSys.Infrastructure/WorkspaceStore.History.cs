using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VamSys.Core;
namespace VamSys.Infrastructure;
public sealed partial class WorkspaceStore
{
    internal sealed record StoredHistory(EditHistory Owner,HistorySnapshot Captured,string Undo,string Redo,List<(Guid Id,string Json)> Steps,ImmutableArray<Guid> Deletes,bool OrderChanged);
    static StoredHistory CaptureHistory(EditHistory history,bool full=false,HistoryChanges? changes=null)
    {
        var delta=changes??history.Changes();var state=delta.Captured;
        return new(history,state,Encode(state.Undo.Select(s=>s.StepId)),Encode(state.Redo.Select(s=>s.StepId)),
            (full?state.Undo.Concat(state.Redo):delta.Upserts).Select(s=>(s.StepId,Encode(s))).ToList(),delta.Deletes,full||delta.OrderChanged);
    }
    static IEnumerable<string> HistoryJson(StoredHistory h) => h.Steps.Select(s=>s.Json).Concat(h.OrderChanged?[h.Undo,h.Redo]:[]);
    static void WriteHistory(SqliteConnection c,SqliteTransaction tx,Guid id,ResourceKind kind,StoredHistory h,bool full=false)
    {
        var args=new (string,object?)[]{("$w",id.ToString()),("$r",(int)kind)};
        foreach(var step in h.Deletes)Execute(c,tx,"DELETE FROM resource_history_steps WHERE workspace_id=$w AND resource=$r AND step_id=$id",[..args,("$id",step.ToString())]);
        foreach(var step in h.Steps)Execute(c,tx,"INSERT INTO resource_history_steps VALUES($w,$r,$id,$json) ON CONFLICT(workspace_id,resource,step_id) DO UPDATE SET json=$json",[..args,("$id",step.Id.ToString()),("$json",step.Json)]);
        if(full)Execute(c,tx,"INSERT INTO resource_history_state VALUES($w,$r,$u,$d)",[..args,("$u",h.Undo),("$d",h.Redo)]);
        else if(h.OrderChanged&&Execute(c,tx,"UPDATE resource_history_state SET undo_json=$u,redo_json=$d WHERE workspace_id=$w AND resource=$r",[..args,("$u",h.Undo),("$d",h.Redo)])!=1)throw OperationsAdapter.Block("StorageInvalid");
    }
    static void ReadHistory(SqliteConnection c,SqliteTransaction tx,Guid id,ResourceKind kind,ResourceData data)
    {
        var args=new (string,object?)[]{("$w",id.ToString()),("$r",(int)kind)};
        Guid[] undo,redo;
        using(var cmd=Command(c,tx,"SELECT undo_json,redo_json FROM resource_history_state WHERE workspace_id=$w AND resource=$r",args))
        using(var r=cmd.ExecuteReader())
        {
            if(!r.Read())throw OperationsAdapter.Block("StorageInvalid");
            undo=JsonSerializer.Deserialize<Guid[]>(r.GetString(0))!;redo=JsonSerializer.Deserialize<Guid[]>(r.GetString(1))!;
        }
        var steps=new Dictionary<Guid,HistoryStep>();
        using(var cmd=Command(c,tx,"SELECT step_id,json FROM resource_history_steps WHERE workspace_id=$w AND resource=$r",args))
        using(var r=cmd.ExecuteReader())while(r.Read())
        {
            var step=JsonSerializer.Deserialize<HistoryStep>(r.GetString(1))!;
            if(step.StepId!=Guid.Parse(r.GetString(0)))throw OperationsAdapter.Block("StorageInvalid");steps.Add(step.StepId,step);
        }
        if(undo.Length+redo.Length!=steps.Count)throw OperationsAdapter.Block("StorageInvalid");
        data.History=EditHistory.From(new([..undo.Select(id=>steps[id])],[..redo.Select(id=>steps[id])]));
        data.History.Validate(data);data.History.Acknowledge(data.History.Capture());
        data.InvalidateDraftIndex();if(data.Draft.Count>0)data.FindDraftRow(data.Draft[0].LocalId);
    }
    static void Acknowledge(FullPayload payload)
    {foreach(var resource in payload.Resources)resource.History.Owner.Acknowledge(resource.History.Captured);}
}
