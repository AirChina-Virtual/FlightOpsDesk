using System.Runtime.CompilerServices;
using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed partial class OperationsAdapter
{
    CandidateSignature DraftSignature(ResourceKind kind,DataRow row)
    {
        CheckConnection(row);
        if(kind is ResourceKind.Routes or ResourceKind.Routings)
            return new(kind,row.Get(kind==ResourceKind.Routes?"Flight Number":"Route String"),
                EndpointId(kind,row,"Departure Airport (ICAO/IATA)",false),EndpointId(kind,row,"Arrival Airport (ICAO/IATA)",false));
        return new(kind,row.Get(kind==ResourceKind.Airports?"ICAO/IATA":kind==ResourceKind.Aircraft?"Registration":"Name"));
    }
    CandidateSignature[] RemoteSignatures(ResourceKind kind,DataRow row)
    {
        CheckConnection(row);
        if(row.Identity==null) throw Block("ApiRefreshFirst");
        if(kind==ResourceKind.Airports)
        {
            if(row.RawApiJson==null) throw Block("ApiRefreshFirst");
            using var doc=System.Text.Json.JsonDocument.Parse(row.RawApiJson);
            return new[]{"icao","iata"}.Where(k=>doc.RootElement.TryGetProperty(k,out var v) && v.ValueKind==System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                .Select(k=>new CandidateSignature(kind,doc.RootElement.GetProperty(k).GetString()!)).ToArray();
        }
        if(kind is ResourceKind.Routes or ResourceKind.Routings)
            return [new(kind,row.Get(kind==ResourceKind.Routes?"Flight Number":"Route String"),
                EndpointId(kind,row,"Departure Airport (ICAO/IATA)",true),EndpointId(kind,row,"Arrival Airport (ICAO/IATA)",true))];
        return [DraftSignature(kind,row)];
    }
    internal void ReserveCandidate(ChangeItem item,CandidateQuerySession session)
    {
        session.Bind(workspace.Id,workspace.AirlineId);
        var keys=new HashSet<CandidateSignature>(CandidateSignature.Equality); var opaque=false;
        // Legacy archives may lack raw identities or contain unresolved local references.
        // Fail closed for creation without preventing read-only recovery of those archives.
        try
        {
            if(item.Before!=null) keys.UnionWith(RemoteSignatures(item.Resource,item.Before));
            if(item.Kind!=ChangeKind.Delete) keys.Add(DraftSignature(item.Resource,item.After));
        }
        catch(Exception e) when(e is InvalidOperationException or System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        { opaque=true; }
        session.Reserve(item,keys,opaque || keys.Count==0);
    }
    internal void SeedCandidates(BatchJob job,CandidateQuerySession session)
    {
        session.Bind(workspace.Id,workspace.AirlineId);
        // Only uncertain writes survive a session. Confirmed candidates are read fresh on resume.
        foreach(var item in job.Items.Where(i=>i.State is not (ItemState.Succeeded or ItemState.ManuallyConfirmed)
            && (i.State is ItemState.Unknown or ItemState.Running || i.CreateCompleted || i.WriteAccepted))) ReserveCandidate(item,session);
    }
    internal string? CandidateBlock(ChangeItem item,CandidateQuerySession session)
    {
        session.Bind(workspace.Id,workspace.AirlineId);
        return session.Blocked(DraftSignature(item.Resource,item.After));
    }
    internal void ConfirmCandidate(ChangeItem item,DataRow? row,CandidateQuerySession session)
    {
        session.Bind(workspace.Id,workspace.AirlineId);
        var keys=row==null?[]:RemoteSignatures(item.Resource,row);
        var id=row?.Identity?.RemoteId ?? item.RemoteId ?? item.Before?.Identity?.RemoteId;
        if(id==null) throw Block("ApiRefreshFirst");
        if(session.Indexes.TryGetValue(item.Resource,out var index))
        { if(row==null) index.Remove(id); else index.Put(row,keys); }
        if(!session.Confirmed.TryGetValue(item.Resource,out var confirmed)) session.Confirmed[item.Resource]=confirmed=new();
        if(row==null) confirmed.Remove(id); else confirmed.Put(row,keys);
        session.Release(item);
    }
    public static Uri FilterUri(string path,IEnumerable<KeyValuePair<string,string>> filters)
        => new(BaseUri,path+"?"+string.Join('&',filters.Select(p=>Uri.EscapeDataString("filter["+p.Key+"]")+"="+Uri.EscapeDataString(p.Value))));
    async IAsyncEnumerable<DataRow> ReadFiltered(ResourceKind kind,string path,Dictionary<string,string> filters,[EnumeratorCancellation] CancellationToken ct)
    {
        await foreach(var value in transport.ReadPagesAsync(FilterUri(path,filters),ct)) yield return FromJson(kind,value);
    }
    public async IAsyncEnumerable<DataRow> ReadDuplicateCandidatesAsync(ChangeItem item,CandidateQuerySession session,[EnumeratorCancellation] CancellationToken ct)
    {
        session.Bind(workspace.Id,workspace.AirlineId);
        var signature=DraftSignature(item.Resource,item.After);
        var seen=new HashSet<string>();
        if(session.Confirmed.TryGetValue(item.Resource,out var confirmed))
            foreach(var row in confirmed.Find(signature))
            { ct.ThrowIfCancellationRequested(); if(seen.Add(row.Identity!.RemoteId)) yield return row.Copy(); }
        if(item.Resource is ResourceKind.Routes or ResourceKind.Routings)
        {
            var suffix=item.Resource==ResourceKind.Routes?"_id":"_airport_id";
            var filters=new Dictionary<string,string>
            {
                ["departure"+suffix]=signature.Departure.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["arrival"+suffix]=signature.Arrival.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            await foreach(var row in ReadFiltered(item.Resource,Collection(item.Resource),filters,ct))
                if(seen.Add(row.Identity!.RemoteId)) yield return row;
            yield break;
        }
        if(!session.Indexes.TryGetValue(item.Resource,out var rows))
        {
            // Publish only a complete index. Aircraft enumeration spans every fleet.
            var complete=new CandidateIndex();
            await foreach(var row in ReadAllAsync(item.Resource,ct)) complete.Put(row,RemoteSignatures(item.Resource,row));
            ct.ThrowIfCancellationRequested(); session.Bind(workspace.Id,workspace.AirlineId);
            session.Indexes[item.Resource]=rows=complete; PerformanceRun.Current?.IndexBuilt();
        }
        foreach(var row in rows.Find(signature))
        { ct.ThrowIfCancellationRequested(); if(seen.Add(row.Identity!.RemoteId)) yield return row.Copy(); }
    }

    public async IAsyncEnumerable<(ResourceKind Kind,DataRow Row)> ReadDependencyCandidatesAsync(ChangeItem item,[EnumeratorCancellation] CancellationToken ct)
    {
        var id=item.Before?.Identity?.RemoteId ?? item.RemoteId ?? throw Block("ApiRefreshFirst");
        CheckConnection(item.Before ?? item.After); PositiveId(id);
        var seen=new HashSet<(ResourceKind,string)>();
        if(item.Resource==ResourceKind.Airports)
        {
            foreach(var kind in new[]{ResourceKind.Routes,ResourceKind.Routings})
                foreach(var side in new[]{"departure","arrival"})
                    await foreach(var row in ReadFiltered(kind,Collection(kind),new(){[side+(kind==ResourceKind.Routes?"_id":"_airport_id")]=id},ct))
                        if(seen.Add((kind,row.Identity!.RemoteId))) yield return (kind,row);
        }
        else if(item.Resource==ResourceKind.Fleets)
        {
            await foreach(var value in transport.ReadPagesAsync(new(BaseUri,$"fleet/{id}/aircraft"),ct))
            {
                var row=FromJson(ResourceKind.Aircraft,value);
                if(seen.Add((ResourceKind.Aircraft,row.Identity!.RemoteId))) yield return (ResourceKind.Aircraft,row);
            }
            await foreach(var row in ReadFiltered(ResourceKind.Routes,"routes",new(){["fleet_id"]=id},ct))
                if(seen.Add((ResourceKind.Routes,row.Identity!.RemoteId))) yield return (ResourceKind.Routes,row);
        }
    }
    public async Task<bool> HasDeletionDependenciesAsync(ChangeItem item,CancellationToken ct)
    {
        var service=new DeletionService();
        if(service.CheckReferences(workspace,item.Resource,new HashSet<Guid>{item.After.LocalId}).Count>0) return true;
        var context=new Workspace {AirlineId=workspace.AirlineId};
        var target=(item.Before ?? item.After).Copy();target.LocalId=item.After.LocalId;
        context.Resources[item.Resource].Draft.Add(target);
        await foreach(var (kind,row) in ReadDependencyCandidatesAsync(item,ct))
        {
            PerformanceRun.Current?.Candidate();
            // At most one candidate is needed in the lightweight reference context.
            context.Resources[kind].Draft.Add(row);
            var blocked=service.CheckReferences(context,item.Resource,new HashSet<Guid>{target.LocalId}).Count>0;
            context.Resources[kind].Draft.Clear();
            if(blocked) return true;
        }
        return false;
    }
}
