using VamSys.Core;

namespace VamSys.Infrastructure;

// Text comparison must agree with IsDuplicate; separators in user values are not keys.
internal readonly record struct CandidateSignature(ResourceKind Kind,string Text,long Departure=0,long Arrival=0)
{
    internal sealed class Comparer : IEqualityComparer<CandidateSignature>
    {
        public bool Equals(CandidateSignature x,CandidateSignature y) => x.Kind==y.Kind && x.Departure==y.Departure && x.Arrival==y.Arrival && StringComparer.OrdinalIgnoreCase.Equals(x.Text,y.Text);
        public int GetHashCode(CandidateSignature x) => HashCode.Combine(x.Kind,x.Departure,x.Arrival,StringComparer.OrdinalIgnoreCase.GetHashCode(x.Text));
    }
    internal static readonly Comparer Equality=new();
}

internal sealed class CandidateIndex
{
    readonly Dictionary<string,(DataRow Row,HashSet<CandidateSignature> Keys)> rows=[];
    readonly Dictionary<CandidateSignature,HashSet<string>> signatures=new(CandidateSignature.Equality);
    internal void Remove(string id)
    {
        if(!rows.Remove(id,out var old)) return;
        foreach(var key in old.Keys)
            if(signatures.TryGetValue(key,out var ids)) { ids.Remove(id); if(ids.Count==0) signatures.Remove(key); }
    }
    internal void Put(DataRow row,IEnumerable<CandidateSignature> keys)
    {
        var id=row.Identity!.RemoteId;
        var unique=keys.ToHashSet(CandidateSignature.Equality);
        Remove(id); rows[id]=(row.Copy(),unique);
        foreach(var key in unique)
        {
            if(!signatures.TryGetValue(key,out var ids)) signatures[key]=ids=[];
            ids.Add(id);
        }
    }
    internal IEnumerable<DataRow> Find(CandidateSignature key)
    {
        if(signatures.TryGetValue(key,out var ids)) foreach(var id in ids) yield return rows[id].Row;
    }
}

public sealed class CandidateQuerySession
{
    internal readonly Dictionary<ResourceKind,CandidateIndex> Indexes=[];
    internal readonly Dictionary<ResourceKind,CandidateIndex> Confirmed=[];
    readonly Dictionary<Guid,HashSet<CandidateSignature>> reservations=[];
    readonly Dictionary<CandidateSignature,HashSet<Guid>> uncertain=new(CandidateSignature.Equality);
    readonly Dictionary<ResourceKind,HashSet<Guid>> unresolved=[];
    readonly HashSet<Guid> airports=[];
    Guid? workspace;
    string? connection;

    internal void Bind(Guid workspaceId,string? connectionId)
    {
        if(workspace.HasValue && workspace!=workspaceId || connection!=null && connection!=connectionId) throw OperationsAdapter.Block("ApiWrongVa");
        workspace=workspaceId; connection=connectionId;
    }
    internal void Release(ChangeItem item)
    {
        airports.Remove(item.Id);
        if(unresolved.TryGetValue(item.Resource,out var opaque)) opaque.Remove(item.Id);
        if(!reservations.Remove(item.Id,out var keys)) return;
        foreach(var key in keys)
            if(uncertain.TryGetValue(key,out var ids)) { ids.Remove(item.Id); if(ids.Count==0) uncertain.Remove(key); }
    }
    internal void Reserve(ChangeItem item,HashSet<CandidateSignature> keys,bool opaque)
    {
        Release(item); reservations[item.Id]=keys;
        if(item.Resource==ResourceKind.Airports && item.Kind==ChangeKind.Create) airports.Add(item.Id);
        if(opaque)
        {
            if(!unresolved.TryGetValue(item.Resource,out var ids)) unresolved[item.Resource]=ids=[];
            ids.Add(item.Id);
        }
        foreach(var key in keys)
        {
            if(!uncertain.TryGetValue(key,out var ids)) uncertain[key]=ids=[];
            ids.Add(item.Id);
        }
    }
    internal string? Blocked(CandidateSignature key)
    {
        if(key.Kind==ResourceKind.Airports && airports.Count>0) return "ApiAirportCreatePending";
        return unresolved.TryGetValue(key.Kind,out var opaque) && opaque.Count>0 || uncertain.ContainsKey(key) ? "ApiIdentityPending" : null;
    }
}
