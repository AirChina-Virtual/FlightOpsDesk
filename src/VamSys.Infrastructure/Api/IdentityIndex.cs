using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// Who is who in this session: the remote rows read so far, the local drafts that await an id, and the airport code
// index that turns "EGLL", "LHR" or "101" into one remote airport id (zero or several candidates block).
internal sealed class IdentityIndex(ApiContext ctx)
{
    Dictionary<(ResourceKind, string), DataRow> known = new();

    // Every row read from the server is remembered here so that references (a route's airports, an aircraft's fleet)
    // resolve without another request.
    public void Remember(ResourceKind kind, string id, DataRow row)
    {
        known[(kind, id)] = row;
        if (kind == ResourceKind.Airports)
            knownAirportsVersion++;
    }
    public void Forget(ResourceKind kind, string id) { if (known.Remove((kind, id)) && kind == ResourceKind.Airports) knownAirportsVersion++; }
    // A completed refresh read everything into a scratch session; its rows become this session's.
    public void Adopt(IdentityIndex scratch) { known = scratch.known; knownAirportsVersion++; }

    public DataRow? Known(ResourceKind kind, string id)
        => known.GetValueOrDefault((kind, id)) ?? ctx.Workspace.Resources[kind].Snapshot.FirstOrDefault(r => r.Identity?.RemoteId == id && r.Identity.ConnectionId == ctx.Workspace.AirlineId);

    // A "local:" token names a draft row that has no remote id yet.
    public DataRow? LocalDraft(ResourceKind kind, string token)
        => LocalReference.TryParse(token, out var localId) ? ctx.Workspace.Resources[kind].Draft.FirstOrDefault(r => r.LocalId == localId) : null;

    // Preview plans run before anything exists on the server: a live local draft stands for id 1.
    public long PreviewReference(string token, ResourceKind kind)
    {
        if (LocalDraft(kind, token) is { } draft && !Schemas.Deleted(draft))
            return 1;
        return PositiveId(token);
    }

    public long AirportId(string code, bool preview = false)
    {
        if (preview && LocalReference.IsLocal(code))
            return PreviewReference(code, ResourceKind.Airports);
        // Exactly one remote airport may carry the code as its key, remote ID, ICAO or IATA; zero or several block.
        if (!AirportIndex().TryGetValue(code, out var ids) || ids.Count != 1)
            throw Block("ApiInvalid", code);
        return PositiveId(ids.First());
    }

    // Rebuilt whenever the known rows, the snapshot list or the connection change. Snapshot rows are never edited
    // in place: refresh replaces the list, while rebase and deletion remove and append rows.
    int knownAirportsVersion;
    (List<DataRow>? List, int Count, DataRow? Last, int Known, string? Airline) airportIndexKey;
    Dictionary<string, HashSet<string>>? airportIndex;
    Dictionary<string, HashSet<string>> AirportIndex()
    {
        var snapshot = ctx.Workspace.Resources[ResourceKind.Airports].Snapshot;
        var key = (snapshot, snapshot.Count, snapshot.Count == 0 ? null : snapshot[^1], knownAirportsVersion, ctx.Workspace.AirlineId);
        if (airportIndex != null && airportIndexKey.Equals(key))
            return airportIndex;
        var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string code, string id)
        { if (!index.TryGetValue(code, out var ids)) index[code] = ids = []; ids.Add(id); }
        foreach (var r in known.Where(p => p.Key.Item1 == ResourceKind.Airports).Select(p => p.Value).Concat(snapshot))
        {
            if (r.Identity == null || r.Identity.ConnectionId != ctx.Workspace.AirlineId)
                continue;
            var id = r.Identity.RemoteId;
            Add(r.Get("ICAO/IATA"), id);
            Add(id, id);
            foreach (var alias in AirportAliases(r))
                Add(alias, id);
        }
        airportIndexKey = key;
        return airportIndex = index;
    }

    // icao and iata of the server's airport record (empty for a row that never came from the server).
    public static string[] AirportAliases(DataRow r) => r.RawApiJson == null ? [] : RowFacts.Of(r.RawApiJson).Aliases;
    public static bool HasAirportAlias(DataRow r, string code)
        => !string.IsNullOrWhiteSpace(code) && AirportAliases(r).Any(a => a.Equals(code, StringComparison.OrdinalIgnoreCase));

    // The remote airport id behind an endpoint column. remote: read it from the server's own record (RawApiJson);
    // otherwise resolve the code in the grid through the airport index.
    public long EndpointId(ResourceKind kind, DataRow row, string field, bool remote)
    {
        ctx.CheckConnection(row);
        if (remote)
        {
            if (row.RawApiJson == null)
                throw Block("ApiRefreshFirst");
            var wire = (field.StartsWith("Departure") ? "departure" : "arrival") + (kind == ResourceKind.Routes ? "_id" : "_airport_id");
            return PositiveId(RowFacts.Of(row.RawApiJson).Endpoint(wire) ?? throw Block("ApiRefreshFirst"));
        }
        return AirportId(row.Get(field));
    }

    public bool SameEndpoint(ResourceKind kind, DataRow current, DataRow expected, string field, bool before = false)
        => EndpointId(kind, current, field, true) == EndpointId(kind, expected, field, before && expected.RawApiJson != null);
}
