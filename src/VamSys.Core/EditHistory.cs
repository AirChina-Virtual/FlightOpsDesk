using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VamSys.Core;

// Captured values never expose a mutable DataRow or Dictionary to an in-flight save.
public sealed record RowImage(Guid LocalId, ResourceIdentity? Identity, string? RawApiJson, ImmutableDictionary<string,string> Fields,ImmutableArray<string> FieldOrder)
{
    public static RowImage Capture(DataRow row) => new(row.LocalId,row.Identity,row.RawApiJson,row.Fields.ToImmutableDictionary(StringComparer.Ordinal),[..row.Fields.Keys]);
    public DataRow Restore()
    {
        if(FieldOrder.IsDefault||FieldOrder.Length!=Fields.Count)throw new InvalidDataException("Invalid history field order.");
        var fields=new Dictionary<string,string>(Fields.Count,StringComparer.Ordinal);
        foreach(var key in FieldOrder)
            if(key is null||!Fields.TryGetValue(key,out var value)||!fields.TryAdd(key,value))throw new InvalidDataException("Invalid history field order.");
        return new(LocalId,Identity,RawApiJson,fields);
    }
    public static bool Same(RowImage? a,RowImage? b) => ReferenceEquals(a,b) || a!=null && b!=null && a.LocalId==b.LocalId && a.Identity==b.Identity && a.RawApiJson==b.RawApiJson && a.Fields.Count==b.Fields.Count && a.FieldOrder.SequenceEqual(b.FieldOrder) && a.Fields.All(p=>b.Fields.TryGetValue(p.Key,out var v)&&v==p.Value);
    internal static bool Matches(RowImage? image, DataRow? row)
    {
        if (image is null || row is null) return image is null && row is null;
        if (image.LocalId != row.LocalId || image.Identity != row.Identity || image.RawApiJson != row.RawApiJson
            || image.Fields.Count != row.Fields.Count || image.FieldOrder.IsDefault
            || image.FieldOrder.Length != row.Fields.Count) return false;
        var orderIndex=0;
        foreach(var field in row.Fields)
            if(field.Key!=image.FieldOrder[orderIndex++])return false;
        foreach (var field in image.Fields)
            if (!row.Fields.TryGetValue(field.Key, out var value) || value != field.Value) return false;
        return true;
    }
}
public sealed record RowEdit(Guid LocalId,RowImage? Before,RowImage? After);
public sealed record HistoryStep(Guid StepId,ImmutableArray<RowEdit> Rows,ImmutableArray<Guid>? BeforeOrder,ImmutableArray<Guid>? AfterOrder);
public sealed record HistorySnapshot(ImmutableArray<HistoryStep> Undo,ImmutableArray<HistoryStep> Redo);
public sealed record HistoryChanges(HistorySnapshot Captured,ImmutableArray<HistoryStep> Upserts,ImmutableArray<Guid> Deletes,bool OrderChanged);
// Result of an undo or redo. ReplacedRows lists the draft rows replaced at their existing positions;
// null when the step added, removed or reordered rows.
public sealed record HistoryMove(bool Moved,IReadOnlyList<Guid>? ReplacedRows);

public sealed class EditHistory
{
    List<HistoryStep> undo=[],redo=[];
    HistorySnapshot saved=new([],[]);
    public IReadOnlyList<HistoryStep> Undo => undo.AsReadOnly();
    public IReadOnlyList<HistoryStep> Redo => redo.AsReadOnly();
    public HistorySnapshot Capture() => new([..undo],[..redo]);
    public void Acknowledge(HistorySnapshot captured) => saved=captured;
    public HistoryChanges Changes()
    {
        var current=Capture();var previous=saved.Undo.Concat(saved.Redo).ToDictionary(s=>s.StepId);
        var steps=current.Undo.Concat(current.Redo).ToArray();var ids=steps.Select(s=>s.StepId).ToHashSet();
        return new(current,[..steps.Where(s=>!previous.TryGetValue(s.StepId,out var old)||!ReferenceEquals(s,old))],
            [..previous.Keys.Where(id=>!ids.Contains(id))],!current.Undo.Select(s=>s.StepId).SequenceEqual(saved.Undo.Select(s=>s.StepId))||!current.Redo.Select(s=>s.StepId).SequenceEqual(saved.Redo.Select(s=>s.StepId)));
    }
    public EditHistory Copy() => new() { undo=[..undo],redo=[..redo],saved=saved };
    public static EditHistory From(HistorySnapshot state) => new() {undo=[..state.Undo],redo=[..state.Redo]};
    public void Clear() {undo.Clear();redo.Clear();}
    internal void Push(HistoryStep step) {undo.Add(step);if(undo.Count>20)undo.RemoveAt(0);redo.Clear();}
    internal void Amend(IReadOnlyList<RowEdit> edits,ImmutableArray<Guid>? oldOrder,ImmutableArray<Guid>? newOrder)
    {
        HistoryStep Change(HistoryStep step,bool after)
        {
            var rows=step.Rows.ToDictionary(r=>r.LocalId);
            foreach(var edit in edits)
            {
                var original=rows.GetValueOrDefault(edit.LocalId);
                var before=after?(original?.Before ?? (original==null?edit.Before:null)):edit.After;
                var next=after?edit.After:(original?.After ?? (original==null?edit.Before:null));
                if(RowImage.Same(before,next))rows.Remove(edit.LocalId);else rows[edit.LocalId]=new(edit.LocalId,before,next);
            }
            return step with {Rows=[..rows.Values],BeforeOrder=after?step.BeforeOrder??oldOrder:newOrder??step.BeforeOrder,AfterOrder=after?newOrder??step.AfterOrder:step.AfterOrder??oldOrder};
        }
        if(undo.Count>0)undo[^1]=Change(undo[^1],true);
        if(redo.Count>0)redo[^1]=Change(redo[^1],false);
    }
    internal HistoryMove Move(ResourceData data,bool undo)
    {
        var source=undo?this.undo:redo;var target=undo?redo:this.undo;
        if(source.Count==0)return new(false,[]);
        var step=source[^1];Apply(data,step,!undo);source.RemoveAt(source.Count-1);target.Add(step);
        return new(true,InPlace(step)?[..step.Rows.Select(r=>r.LocalId)]:null);
    }
    static bool InPlace(HistoryStep step) => step.BeforeOrder is null && step.AfterOrder is null && step.Rows.All(r => r.Before != null && r.After != null);
    // Validate every precondition before mutating any row or stack.
    static void Apply(ResourceData data,HistoryStep step,bool forward)
    {
        if (InPlace(step))
        {
            data.EnsureDraftIndex();
            var prepared = new List<DataRow>(step.Rows.Length);
            var touched = new HashSet<Guid>(step.Rows.Length);
            foreach (var edit in step.Rows)
            {
                var before = forward ? edit.Before! : edit.After!;
                var after = forward ? edit.After! : edit.Before!;
                if (!touched.Add(edit.LocalId) || before.LocalId != edit.LocalId || after.LocalId != edit.LocalId)
                    throw new InvalidDataException("Invalid history row.");
                if (!RowImage.Matches(before, data.FindDraftRow(edit.LocalId)))
                    throw new InvalidDataException("History row does not match draft.");
                prepared.Add(after.Restore());
            }
            data.ReplaceDraftRows(prepared);
            return;
        }
        var draft=data.Draft;var positions=data.GetDraftPositions();
        // Validate the complete cached source index, including undeclared external reorders.
        for(var n=0;n<draft.Count;n++)
            if(!positions.TryGetValue(draft[n].LocalId,out var at)||at!=n)
            {data.InvalidateDraftIndex();positions=data.GetDraftPositions();break;}
        var from=forward?step.BeforeOrder:step.AfterOrder;var to=forward?step.AfterOrder:step.BeforeOrder;
        if((from==null)!=(to==null)||from is {} order&&!draft.Select(r=>r.LocalId).SequenceEqual(order))throw new InvalidDataException("History order does not match draft.");
        var changes=new Dictionary<Guid,DataRow?>(step.Rows.Length);var count=draft.Count;
        foreach(var edit in step.Rows)
        {
            var before=forward?edit.Before:edit.After;var after=forward?edit.After:edit.Before;
            if(changes.ContainsKey(edit.LocalId)||before!=null&&before.LocalId!=edit.LocalId||after!=null&&after.LocalId!=edit.LocalId||before==null&&after==null)throw new InvalidDataException("Invalid history row.");
            var actual=positions.TryGetValue(edit.LocalId,out var at)?draft[at]:null;
            if(!RowImage.Matches(before,actual))throw new InvalidDataException("History row does not match draft.");
            changes.Add(edit.LocalId,after?.Restore());
            if(before==null)count++;else if(after==null)count--;
        }
        var resultOrder=to??[..draft.Select(r=>r.LocalId)];
        if(resultOrder.Length!=count)throw new InvalidDataException("Invalid history result order.");
        var nextPositions=new Dictionary<Guid,int>(count);var restoredRows=new List<DataRow>(count);
        foreach(var id in resultOrder)
        {
            if(!nextPositions.TryAdd(id,restoredRows.Count))throw new InvalidDataException("Invalid history result order.");
            var row=changes.TryGetValue(id,out var changed)?changed:positions.TryGetValue(id,out var at)?draft[at]:null;
            if(row==null)throw new InvalidDataException("Invalid history result order.");
            restoredRows.Add(row);
        }
        data.ReplaceIndexedDraft(restoredRows,nextPositions);
    }
    public void Validate(ResourceData data)
    {
        _=data.Draft.ToDictionary(r=>r.LocalId);
        if(undo.Count>20||undo.Concat(redo).Select(s=>s.StepId).Distinct().Count()!=undo.Count+redo.Count||undo.Concat(redo).Any(s=>s.StepId==Guid.Empty))throw new InvalidDataException("Invalid history steps.");
        foreach(var backwards in new[]{true,false})
        {
            // Apply replaces rows; untouched row objects can be shared safely.
            var probe=new ResourceData {Draft=[..data.Draft]};
            foreach(var step in (backwards?undo:redo).AsEnumerable().Reverse())Apply(probe,step,!backwards);
        }
    }
    public static EditHistory ConvertLegacy(List<DataRow> current,List<List<DataRow>> undo,List<List<DataRow>> redo)
    {
        var result=new EditHistory();
        for(var n=0;n<undo.Count;n++)result.undo.Add(Difference(undo[n],n+1<undo.Count?undo[n+1]:current));
        for(var n=0;n<redo.Count;n++)result.redo.Add(Difference(n+1<redo.Count?redo[n+1]:current,redo[n]));
        result.Validate(new(){Draft=current});return result;
    }
    static HistoryStep Difference(List<DataRow> before,List<DataRow> after)
    {
        var a=before.ToDictionary(r=>r.LocalId);var b=after.ToDictionary(r=>r.LocalId);
        bool Same(DataRow? x,DataRow? y)=>x!=null&&y!=null&&x.Identity==y.Identity&&x.RawApiJson==y.RawApiJson&&x.Fields.SequenceEqual(y.Fields);
        var rows=a.Keys.Union(b.Keys).Where(id=>!Same(a.GetValueOrDefault(id),b.GetValueOrDefault(id))).Select(id=>new RowEdit(id,a.TryGetValue(id,out var x)?RowImage.Capture(x):null,b.TryGetValue(id,out var y)?RowImage.Capture(y):null)).ToImmutableArray();
        var structural=!before.Select(r=>r.LocalId).SequenceEqual(after.Select(r=>r.LocalId));
        return new(Guid.NewGuid(),rows,structural?[..before.Select(r=>r.LocalId)]:null,structural?[..after.Select(r=>r.LocalId)]:null);
    }
}

public sealed partial class ResourceData
{
    // The caller declares the affected IDs. Structural edits additionally capture only GUID order.
    public void Edit(IEnumerable<Guid> ids,Action mutate,bool newStep=true,bool structural=false,bool replaceRows=false)
    {
        var keys=ids.Distinct().ToArray();
        var before=keys.ToDictionary(id=>id,id=>FindDraftRow(id) is {} row?RowImage.Capture(row):null);
        ImmutableArray<Guid>? order=structural?[..Draft.Select(r=>r.LocalId)]:null;
        try {mutate();} finally {if(structural||replaceRows)InvalidateDraftIndex();}
        var changes=keys.Select(id=>new RowEdit(id,before[id],FindDraftRow(id) is {} row?RowImage.Capture(row):null)).Where(r=>!RowImage.Same(r.Before,r.After)).ToArray();
        ImmutableArray<Guid>? nextOrder=structural?[..Draft.Select(r=>r.LocalId)]:null;
        if(newStep)History.Push(new(Guid.NewGuid(),[..changes],order,nextOrder));else History.Amend(changes,order,nextOrder);
    }
}

public sealed class ResourceDataConverter : JsonConverter<ResourceData>
{
    public override ResourceData Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
    {
        using var document=JsonDocument.ParseValue(ref reader);var root=document.RootElement;
        T Read<T>(string name,T fallback) => root.TryGetProperty(name,out var value)?value.Deserialize<T>(options)??throw new JsonException(name):fallback;
        var data=new ResourceData {Snapshot=Read<List<DataRow>>("Snapshot",[]),Draft=Read<List<DataRow>>("Draft",[]),Columns=Read<List<string>>("Columns",[]),Conflicts=Read<List<MergeConflict>>("Conflicts",[]),SnapshotAt=root.TryGetProperty("SnapshotAt",out var at)?at.Deserialize<DateTimeOffset?>():null};
        if(root.TryGetProperty("History",out var h))
        {
            if(root.TryGetProperty("Undo",out _)||root.TryGetProperty("Redo",out _)||h.GetProperty("Version").GetInt32()!=1)throw new JsonException("Unsupported or ambiguous history.");
            data.History=EditHistory.From(new(h.GetProperty("Undo").Deserialize<ImmutableArray<HistoryStep>>(options),h.GetProperty("Redo").Deserialize<ImmutableArray<HistoryStep>>(options)));
        }
        else data.History=EditHistory.ConvertLegacy(data.Draft,Read<List<List<DataRow>>>("Undo",[]),Read<List<List<DataRow>>>("Redo",[]));
        data.History.Validate(data);return data;
    }
    public override void Write(Utf8JsonWriter writer,ResourceData data,JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        void Write<T>(string name,T value){writer.WritePropertyName(name);JsonSerializer.Serialize(writer,value,options);}
        Write("Snapshot",data.Snapshot);Write("Draft",data.Draft);Write("Columns",data.Columns);Write("SnapshotAt",data.SnapshotAt);Write("Conflicts",data.Conflicts);
        Write("History",new {Version=1,Undo=data.History.Capture().Undo,Redo=data.History.Capture().Redo});writer.WriteEndObject();
    }
}
