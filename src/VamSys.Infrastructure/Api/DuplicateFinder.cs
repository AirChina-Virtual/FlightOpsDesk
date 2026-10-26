using System.Globalization;
using System.Runtime.CompilerServices;
using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// Finds the remote records a new record would duplicate. A duplicate is decided by the resource's own rule (airport
// code, registration, fleet name, route signature on the same airport pair); candidates are looked up through a
// signature index that is published only after every page was read, so an incomplete read never hides a duplicate.
internal sealed class DuplicateFinder(ApiContext ctx)
{
    // The identity of a draft row as the duplicate rule sees it.
    public CandidateSignature DraftSignature(ResourceKind kind, DataRow row)
    {
        ctx.CheckConnection(row);
        if (kind is ResourceKind.Routes or ResourceKind.Routings)
            return new(kind, kind == ResourceKind.Routes && row.Get("Type") == "jumpseat" ? ApiOperationPolicy.JumpseatSignature(row) : row.Get(kind == ResourceKind.Routes ? "Flight Number" : "Route String"),
                ctx.Identity.EndpointId(kind, row, "Departure Airport (ICAO/IATA)", false), ctx.Identity.EndpointId(kind, row, "Arrival Airport (ICAO/IATA)", false));
        return new(kind, row.Get(kind == ResourceKind.Airports ? "ICAO/IATA" : kind == ResourceKind.Aircraft ? "Registration" : "Name"));
    }

    // The identities of a record the server returned. An airport answers to its ICAO and to its IATA code.
    public CandidateSignature[] RemoteSignatures(ResourceKind kind, DataRow row)
    {
        ctx.CheckConnection(row);
        if (row.Identity == null)
            throw Block("ApiRefreshFirst");
        if (kind == ResourceKind.Airports)
        {
            if (row.RawApiJson == null)
                throw Block("ApiRefreshFirst");
            return RowFacts.Of(row.RawApiJson).Aliases.Select(alias => new CandidateSignature(kind, alias)).ToArray();
        }
        if (kind is ResourceKind.Routes or ResourceKind.Routings)
            return [new(kind, kind == ResourceKind.Routes && row.Get("Type") == "jumpseat" ? ApiOperationPolicy.JumpseatSignature(row) : row.Get(kind == ResourceKind.Routes ? "Flight Number" : "Route String"),
                ctx.Identity.EndpointId(kind, row, "Departure Airport (ICAO/IATA)", true), ctx.Identity.EndpointId(kind, row, "Arrival Airport (ICAO/IATA)", true))];
        return [DraftSignature(kind, row)];
    }

    // Whether a remote record is the one the item would create. The text comparison must agree with the signatures.
    public bool IsDuplicate(ChangeItem item, DataRow current)
    {
        ctx.CheckConnection(current);
        ctx.CheckConnection(item.After);
        if (item.Resource == ResourceKind.Airports)
            return IdentityIndex.HasAirportAlias(current, item.After.Get("ICAO/IATA"));
        if (item.Resource is ResourceKind.Routes or ResourceKind.Routings)
        {
            // Jumpseats have no flight number: they are the same route when type, airports and every filled time or date agree.
            if (item.Resource == ResourceKind.Routes && (item.After.Get("Type") == "jumpseat" || current.Get("Type") == "jumpseat"))
                return item.After.Get("Type") == current.Get("Type")
                    && ctx.Identity.SameEndpoint(item.Resource, current, item.After, "Departure Airport (ICAO/IATA)")
                    && ctx.Identity.SameEndpoint(item.Resource, current, item.After, "Arrival Airport (ICAO/IATA)")
                    && new[] { "Departure Time (HH:MM)", "Arrival Time (HH:MM)", "Start Date", "End Date" }.All(f => current.Get(f) == "" || item.After.Get(f) == "" || current.Get(f) == item.After.Get(f));
            var signature = item.Resource == ResourceKind.Routes ? "Flight Number" : "Route String";
            return ctx.Identity.SameEndpoint(item.Resource, current, item.After, "Departure Airport (ICAO/IATA)")
                && ctx.Identity.SameEndpoint(item.Resource, current, item.After, "Arrival Airport (ICAO/IATA)")
                && current.Get(signature).Equals(item.After.Get(signature), StringComparison.OrdinalIgnoreCase);
        }
        var field = item.Resource == ResourceKind.Aircraft ? "Registration" : "Name";
        return current.Get(field).Equals(item.After.Get(field), StringComparison.OrdinalIgnoreCase);
    }

    // The remote records that could be duplicates of the item, in the cheapest complete way available:
    //   routes and routings   a filtered read per remote airport pair, cached once fully read (an early duplicate stops paging)
    //   everything else       one complete read of the resource into a signature index
    // Rows confirmed earlier in this run come first, so a record created by this very task is found without a request.
    public async IAsyncEnumerable<DataRow> ReadCandidatesAsync(ChangeItem item, CandidateQuerySession session, [EnumeratorCancellation] CancellationToken ct)
    {
        session.Bind(ctx.Workspace.Id, ctx.Workspace.AirlineId);
        var signature = DraftSignature(item.Resource, item.After);
        var seen = new HashSet<string>();
        if (session.Confirmed.TryGetValue(item.Resource, out var confirmed))
            foreach (var row in confirmed.Find(signature))
            { ct.ThrowIfCancellationRequested(); if (seen.Add(row.Identity!.RemoteId)) yield return row; }
        if (item.Resource is ResourceKind.Routes or ResourceKind.Routings)
        {
            var pair = (item.Resource, signature.Departure, signature.Arrival);
            if (session.Pairs.TryGetValue(pair, out var cached))
            {
                foreach (var row in cached)
                { ct.ThrowIfCancellationRequested(); if (seen.Add(row.Identity!.RemoteId)) yield return row; }
                yield break;
            }
            var suffix = item.Resource == ResourceKind.Routes ? "_id" : "_airport_id";
            var filters = new Dictionary<string, string>
            {
                ["departure" + suffix] = signature.Departure.ToString(CultureInfo.InvariantCulture),
                ["arrival" + suffix] = signature.Arrival.ToString(CultureInfo.InvariantCulture)
            };
            // Rows still stream so an early duplicate stops paging; only a fully read pair is cached.
            var complete = new List<DataRow>();
            await foreach (var row in ctx.Reader.ReadFilteredAsync(item.Resource, ResourcePaths.Collection(item.Resource), filters, ct))
            { complete.Add(row); if (seen.Add(row.Identity!.RemoteId)) yield return row; }
            ct.ThrowIfCancellationRequested();
            session.Bind(ctx.Workspace.Id, ctx.Workspace.AirlineId);
            session.Pairs[pair] = complete;
            yield break;
        }
        if (!session.Indexes.TryGetValue(item.Resource, out var rows))
        {
            // Publish only a complete index. Aircraft enumeration spans every fleet.
            var complete = new CandidateIndex();
            await foreach (var row in ctx.Reader.ReadAllAsync(item.Resource, ct))
                complete.Put(row, RemoteSignatures(item.Resource, row));
            ct.ThrowIfCancellationRequested();
            session.Bind(ctx.Workspace.Id, ctx.Workspace.AirlineId);
            session.Indexes[item.Resource] = rows = complete;
            PerformanceRun.Current?.IndexBuilt();
        }
        foreach (var row in rows.Find(signature))
        { ct.ThrowIfCancellationRequested(); if (seen.Add(row.Identity!.RemoteId)) yield return row; }
    }
}
