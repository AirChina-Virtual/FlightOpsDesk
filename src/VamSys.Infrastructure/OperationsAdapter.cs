using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed record ApiStep(HttpMethod Method, string Path, Dictionary<string, object?> Body);
internal sealed record ReadbackVerification(bool Passed, List<ApiFieldDifference> Differences);
public interface IApiResourceAdapter
{
    DataRow FromJson(ResourceKind kind, JsonElement value);
    List<ApiStep> Plan(ChangeItem change);
}
public sealed partial class OperationsAdapter(OperationsTransport transport, Workspace workspace) : IResourceReader, IResourceWriter, IApiResourceAdapter
{
    public static readonly Uri BaseUri = new("https://vamsys.io/api/v3/operations/");
    static readonly JsonElement SchemasJson = JsonDocument.Parse(typeof(OperationsAdapter).Assembly.GetManifestResourceStream("Operations.OpenApi.json")!).RootElement.GetProperty("components").GetProperty("schemas");
    Dictionary<(ResourceKind, string), DataRow> known = new();
    public string? SessionAirlineId { get; private set; }
    public string ConnectionId => workspace.AirlineId ?? throw Block("ApiConnectFirst");
    public static InvalidOperationException Block(string key, string? field = null) => MessageErrors.Attach(new InvalidOperationException(Messages.Define(key, ("field", field))), Messages.Define(key, ("field", field)));
    public static string Collection(ResourceKind k) => k switch { ResourceKind.Airports => "airports", ResourceKind.Fleets => "fleet", ResourceKind.Routings => "routings", ResourceKind.Routes => "routes", _ => throw new ArgumentException() };
    // CSV column, wire name, value type and the other per-field facts live in the field table (FieldSpecs).
    static IReadOnlyList<FieldSpec> WireFields(ResourceKind kind) => FieldSpecs.Get(kind).WireFields;
    static string Value(JsonElement e) => e.ValueKind switch { JsonValueKind.Null => "", JsonValueKind.Array => string.Join(',', e.EnumerateArray().Select(Value)), JsonValueKind.True => "TRUE", JsonValueKind.False => "FALSE", JsonValueKind.String => e.GetString()!, _ => e.ToString() };
    public DataRow FromJson(ResourceKind kind, JsonElement value)
    {
        var id = value.GetProperty("id").ToString();
        string airline;
        if (Owner(value) is string owner)
            airline = owner;
        // RouteData documents no airline_id; the credentials belong to exactly one VA.
        else if (kind == ResourceKind.Routes)
            airline = workspace.AirlineId ?? SessionAirlineId ?? throw Block("ApiConnectFirst");
        else
            throw Block("ApiInvalid", "airline_id");
        string? parent = kind == ResourceKind.Aircraft ? value.GetProperty("fleet_id").ToString() : null;
        var row = new DataRow.Builder(16) { Identity = new(id, airline, parent), RawApiJson = value.GetRawText() };
        row["_delete"] = "FALSE";
        if (kind != ResourceKind.Airports)
            row["ID"] = id;
        else
            row["ICAO/IATA"] = value.TryGetProperty("icao", out var icao) && icao.ValueKind == JsonValueKind.String ? icao.GetString()! : Value(value.GetProperty("iata"));
        foreach (var spec in WireFields(kind))
        if (value.TryGetProperty(spec.Wire!, out var v))
        {
            var field = spec.Csv;
            var text = Value(v);
            if (spec.Type == FieldType.Time && text.Length == 8)
                text = text[..5];
            if (spec.Type == FieldType.Date && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                text = date.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            row[field] = text;
        }
        if (parent != null)
            row["Fleet ID"] = parent;
        if (kind is ResourceKind.Routes or ResourceKind.Routings)
        {
            foreach (var side in new[] { "Departure", "Arrival" })
            {
                var wire = side.ToLowerInvariant() + (kind == ResourceKind.Routes ? "_id" : "_airport_id");
                var airportId = value.GetProperty(wire).ToString();
                var airport = Known(ResourceKind.Airports, airportId);
                row[side + " Airport (ICAO/IATA)"] = airport?.Get("ICAO/IATA") ?? "unresolved:" + airportId;
            }
        }
        var result = row.ToRow();
        known[(kind, id)] = result;
        if (kind == ResourceKind.Airports)
            knownAirportsVersion++;
        return result;
    }
    string? Owner(JsonElement value)
    {
        if (!value.TryGetProperty("airline_id", out var owner) || owner.ValueKind == JsonValueKind.Null)
            return null;
        var airline = owner.ToString();
        if (workspace.AirlineId != null && workspace.AirlineId != airline)
            throw Block("ApiWrongVa");
        workspace.AirlineId ??= airline;
        SessionAirlineId = airline;
        return airline;
    }
    // Fleets or airports name the VA. A new, empty VA has neither; ranks and containers also carry airline_id.
    public async Task<bool> IdentifyAirlineAsync(CancellationToken ct)
    {
        await foreach (var _ in ReadAllAsync(ResourceKind.Fleets, ct))
            break;
        if (SessionAirlineId == null)
        await foreach (var _ in ReadAllAsync(ResourceKind.Airports, ct))
            break;
        foreach (var path in new[] { "ranks", "containers" })
        {
            if (SessionAirlineId != null)
                break;
            try
            { await foreach (var value in transport.ReadPagesAsync(new(BaseUri, path), ct)) { Owner(value); break; } }
            catch (Exception e) when (e is ApiAccessException or ApiResponseException) { }
        }
        return SessionAirlineId != null;
    }
    DataRow? LocalDraft(ResourceKind kind, string token) => LocalReference.TryParse(token, out var localId) ? workspace.Resources[kind].Draft.FirstOrDefault(r => r.LocalId == localId) : null;
    DataRow? Known(ResourceKind kind, string id) => known.GetValueOrDefault((kind, id)) ?? workspace.Resources[kind].Snapshot.FirstOrDefault(r => r.Identity?.RemoteId == id && r.Identity.ConnectionId == workspace.AirlineId);
    public async IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind kind, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (kind == ResourceKind.Aircraft)
        {
            await foreach (var row in ReadAircraftAsync(ReadAllAsync(ResourceKind.Fleets, ct), ct))
                yield return row;
        }
        else
        await foreach (var element in transport.ReadPagesAsync(new(BaseUri, Collection(kind)), ct))
            yield return FromJson(kind, element);
    }
    // Aircraft have no collection endpoint; they are read fleet by fleet.
    async IAsyncEnumerable<DataRow> ReadAircraftAsync(IAsyncEnumerable<DataRow> fleets, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var fleet in fleets.WithCancellation(ct))
            await foreach (var element in transport.ReadPagesAsync(new(BaseUri, $"fleet/{fleet.Identity!.RemoteId}/aircraft"), ct))
                yield return FromJson(ResourceKind.Aircraft, element);
    }
    public async Task RefreshSnapshotsAsync(Func<Workspace, Task> persist, CancellationToken ct, Action<ResourceKind, int>? progress = null)
    {
        // Reads must not mutate the live identity index, even if a later page or save fails.
        var readWorkspace = new Workspace { AirlineId = workspace.AirlineId };
        var reader = new OperationsAdapter(transport, readWorkspace);
        var incoming = new Dictionary<ResourceKind, List<DataRow>>();
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var rows = new List<DataRow>();
            // Fleets precede aircraft in the enumeration, so their pages are not requested twice.
            var source = kind == ResourceKind.Aircraft ? reader.ReadAircraftAsync(incoming[ResourceKind.Fleets].ToAsyncEnumerable(), ct) : reader.ReadAllAsync(kind, ct);
            await foreach (var row in source)
            { rows.Add(row); if (rows.Count % 100 == 0) progress?.Invoke(kind, rows.Count); }
            incoming[kind] = rows;
        }
        ct.ThrowIfCancellationRequested();
        // Merging replaces each staged resource's collections, so the live workspace stays untouched until persisted.
        var staged = workspace.StageResources();
        staged.AirlineId ??= readWorkspace.AirlineId;
        foreach (var (kind, rows) in incoming)
        {
            SnapshotMerger.Merge(kind, staged.Resources[kind], rows);
            if (!staged.VerifiedOperations.Contains(kind + ":Read"))
                staged.VerifiedOperations.Add(kind + ":Read");
        }
        await persist(staged);
        workspace.StorageRevision = staged.StorageRevision;
        // No await between the durable snapshot and its in-memory index replacement.
        workspace.Resources = staged.Resources;
        workspace.VerifiedOperations = staged.VerifiedOperations;
        workspace.AirlineId = staged.AirlineId;
        // The staged copy was just saved in full and now is the workspace: the journal of the copy describes the database.
        workspace.Journal().AdoptFrom(staged.Journal());
        known = reader.known;
        knownAirportsVersion++;
        SessionAirlineId = reader.SessionAirlineId ?? SessionAirlineId;
    }
    public void Forget(ResourceKind kind, string id) { if (known.Remove((kind, id)) && kind == ResourceKind.Airports) knownAirportsVersion++; }
    string Path(ResourceKind kind, DataRow row, string? remoteId = null, bool preview = false)
    {
        var identity = row.Identity;
        if (identity != null && identity.ConnectionId != ConnectionId)
            throw Block("ApiWrongVa");
        var id = remoteId ?? identity?.RemoteId;
        if (kind == ResourceKind.Aircraft)
        {
            var parent = identity?.ParentFleetId ?? row.Get("Fleet ID");
            if (preview && LocalReference.IsLocal(parent))
                parent = PreviewReference(parent, ResourceKind.Fleets).ToString(CultureInfo.InvariantCulture);
            PositiveId(parent);
            return $"fleet/{parent}/aircraft" + (id == null ? "" : "/" + PositiveId(id));
        }
        return Collection(kind) + (id == null ? "" : "/" + PositiveId(id));
    }
    public async Task<DataRow?> FindAsync(ResourceKind kind, string id, CancellationToken ct)
    {
        var row = Known(kind, id);
        if (kind == ResourceKind.Aircraft && row == null)
            throw Block("ApiRefreshFirst");
        return await FindRow(kind, row ?? new DataRow { Identity = new(id, ConnectionId) }, id, ct);
    }
    public async Task<DataRow?> FindRow(ResourceKind kind, DataRow row, string? id, CancellationToken ct)
    {
        try
        {
            using var json = await transport.GetAsync(new(BaseUri, Path(kind, row, id)), ct);
            var root = json.RootElement;
            var found = FromJson(kind, kind == ResourceKind.Routings ? root : root.GetProperty("data"));
            if (found.Identity!.RemoteId != (id ?? row.Identity?.RemoteId))
                throw Block("ApiWrongVa");
            return found;
        }
        catch (ApiResponseException ex) when (ex.Status == HttpStatusCode.NotFound) { return null; }
    }
    static long PositiveId(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw Block("ApiInvalid", text);
    long PreviewReference(string token, ResourceKind kind)
    {
        if (LocalDraft(kind, token) is { } draft && !Schemas.Deleted(draft))
            return 1;
        return PositiveId(token);
    }
    long AirportId(string code, bool preview = false)
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
        var snapshot = workspace.Resources[ResourceKind.Airports].Snapshot;
        var key = (snapshot, snapshot.Count, snapshot.Count == 0 ? null : snapshot[^1], knownAirportsVersion, workspace.AirlineId);
        if (airportIndex != null && airportIndexKey.Equals(key))
            return airportIndex;
        var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string code, string id)
        { if (!index.TryGetValue(code, out var ids)) index[code] = ids = []; ids.Add(id); }
        foreach (var r in known.Where(p => p.Key.Item1 == ResourceKind.Airports).Select(p => p.Value).Concat(snapshot))
        {
            if (r.Identity == null || r.Identity.ConnectionId != workspace.AirlineId)
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
    static IEnumerable<string> AirportAliases(DataRow r)
    {
        if (r.RawApiJson == null)
            return [];
        using var doc = JsonDocument.Parse(r.RawApiJson);
        return new[] { "icao", "iata" }.Select(k => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "")
            .Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
    }
    static bool HasAirportAlias(DataRow r, string code)
        => !string.IsNullOrWhiteSpace(code) && AirportAliases(r).Any(a => a.Equals(code, StringComparison.OrdinalIgnoreCase));
    void CheckConnection(DataRow row)
    {
        if (row.Identity != null && row.Identity.ConnectionId != ConnectionId)
            throw Block("ApiWrongVa");
    }
    long EndpointId(ResourceKind kind, DataRow row, string field, bool remote)
    {
        CheckConnection(row);
        if (remote)
        {
            if (row.RawApiJson == null)
                throw Block("ApiRefreshFirst");
            using var doc = JsonDocument.Parse(row.RawApiJson);
            var wire = (field.StartsWith("Departure") ? "departure" : "arrival") + (kind == ResourceKind.Routes ? "_id" : "_airport_id");
            if (!doc.RootElement.TryGetProperty(wire, out var value))
                throw Block("ApiRefreshFirst");
            return PositiveId(value.ToString());
        }
        return AirportId(row.Get(field));
    }
    bool SameEndpoint(ResourceKind kind, DataRow current, DataRow expected, string field, bool before = false)
        => EndpointId(kind, current, field, true) == EndpointId(kind, expected, field, before && expected.RawApiJson != null);

    internal bool IsDuplicate(ChangeItem item, DataRow current)
    {
        CheckConnection(current);
        CheckConnection(item.After);
        if (item.Resource == ResourceKind.Airports)
            return HasAirportAlias(current, item.After.Get("ICAO/IATA"));
        if (item.Resource is ResourceKind.Routes or ResourceKind.Routings)
        {
            if (item.Resource == ResourceKind.Routes && (item.After.Get("Type") == "jumpseat" || current.Get("Type") == "jumpseat"))
                return item.After.Get("Type") == current.Get("Type")
                    && SameEndpoint(item.Resource, current, item.After, "Departure Airport (ICAO/IATA)")
                    && SameEndpoint(item.Resource, current, item.After, "Arrival Airport (ICAO/IATA)")
                    && new[] { "Departure Time (HH:MM)", "Arrival Time (HH:MM)", "Start Date", "End Date" }.All(f => current.Get(f) == "" || item.After.Get(f) == "" || current.Get(f) == item.After.Get(f));
            var signature = item.Resource == ResourceKind.Routes ? "Flight Number" : "Route String";
            return SameEndpoint(item.Resource, current, item.After, "Departure Airport (ICAO/IATA)")
                && SameEndpoint(item.Resource, current, item.After, "Arrival Airport (ICAO/IATA)")
                && current.Get(signature).Equals(item.After.Get(signature), StringComparison.OrdinalIgnoreCase);
        }
        var field = item.Resource == ResourceKind.Aircraft ? "Registration" : "Name";
        return current.Get(field).Equals(item.After.Get(field), StringComparison.OrdinalIgnoreCase);
    }
    static string SchemaName(ResourceKind kind, bool create) => (kind, create) switch
    {
        (ResourceKind.Airports, true) => "CreateAirportData",
        (ResourceKind.Airports, false) => "UpdateAirportData",
        (ResourceKind.Fleets, true) => "CreateFleetData",
        (ResourceKind.Fleets, false) => "UpdateFleetData",
        (ResourceKind.Aircraft, true) => "CreateAircraftData",
        (ResourceKind.Aircraft, false) => "UpdateAircraftData",
        (ResourceKind.Routings, true) => "CreateRoutingData",
        (ResourceKind.Routings, false) => "UpdateRoutingData",
        (ResourceKind.Routes, true) => "StoreRouteRequest",
        _ => "UpdateRouteRequest"
    };
    public List<ApiStep> Plan(ChangeItem c) => Plan(c, false);
    internal List<ApiStep> ExecutionPlan(ChangeItem item)
    {
        var current = Plan(item);
        if (item.ApiSteps.Count == 0)
            return current;
        var saved = item.ApiSteps.Select(ToStep).ToList();
        var wanted = ExpectedValues(current);
        var previous = ExpectedValues(saved);
        if (wanted.Count != previous.Count || wanted.Any(p => !previous.TryGetValue(p.Key, out var v) || !JsonElement.DeepEquals(p.Value, v)))
            throw Block("ApiRefreshFirst");
        if (saved.Count > 2)
            throw Block("ApiRefreshFirst");
        for (var n = 0; n < saved.Count; n++)
        {
            var step = saved[n];
            var create = item.Kind == ChangeKind.Create && n == 0;
            var move = IndependentApiEvidence.IsMove(item);
            var method = move ? HttpMethod.Patch : create ? HttpMethod.Post : item.Kind == ChangeKind.Delete ? HttpMethod.Delete : HttpMethod.Put;
            var path = move ? current.Single().Path : item.Kind == ChangeKind.Create ? (n == 0 ? Path(item.Resource, Unsaved(item.After)) : "{created}") : Path(item.Resource, item.Before ?? item.After, item.RemoteId);
            if (step.Method != method || step.Path != path)
                throw Block("ApiRefreshFirst");
            if (move)
                ValidateMoveBody(step.Body);
            else if (method != HttpMethod.Delete)
                ValidateBody(item.Resource, create, step.Body);
        }
        return saved;
    }
    // A create's POST targets the collection even after the response stored the new identity.
    static DataRow Unsaved(DataRow row) => row.WithIdentity(null);
    static ApiStep ToStep(BatchStep s) => new(new HttpMethod(s.Method), s.Path, s.Body.ToDictionary(p => p.Key, p => (object?)p.Value));
    public List<ApiStep> PreviewPlan(ChangeItem c) { var steps = Plan(c, true); ValidateParameterPreview(c); return steps; }
    List<ApiStep> Plan(ChangeItem c, bool preview)
    {
        if (c.Before != null && c.Before.Identity == null)
            throw Block("ApiRefreshFirst");
        if (c.Kind != ChangeKind.Create && c.After.Identity == null)
            throw Block("ApiRefreshFirst");
        if (c.Kind == ChangeKind.Create && c.After.Get("ID") != "" && c.RemoteId == null)
            throw Block("ApiRefreshFirst");
        if (IndependentApiEvidence.IsMove(c))
            return [MovePlan(c, preview)];
        var path = Path(c.Resource, c.Before ?? c.After, c.RemoteId, preview);
        if (c.Kind == ChangeKind.Delete)
            return [new(HttpMethod.Delete, path, [])];
        bool create = c.Kind == ChangeKind.Create;
        if (create && c.Resource == ResourceKind.Fleets && c.RequiresAirlineParameter && c.After.Get("Parameter IDs") == "")
            throw Block("ApiParameterRequired");
        var body = new Dictionary<string, object?>();
        foreach (var (field, intent) in c.Fields)
        {
            if (intent.Intent == FieldIntent.Unspecified || field == "_delete")
                continue;
            var text = intent.Value ?? "";
            if (create && text.Length == 0)
                continue;
            if (field == "ID")
                throw Block("ApiImmutable", field);
            if (field == "Fleet ID")
            { if (!create) throw Block("ApiMoveFleet"); if (preview) PreviewReference(text, ResourceKind.Fleets); else PositiveId(text); continue; }
            if (field == "ICAO/IATA")
            { if (!create) throw Block("ApiImmutable", field); body["icao_iata"] = text; continue; }
            if (FieldSpecs.Column(field) is { Role: var role } && role == FieldRole.Endpoint)
            {
                if (!create)
                    throw Block("ApiImmutable", field);
                body[(field.StartsWith("Departure") ? "departure" : "arrival") + (c.Resource == ResourceKind.Routes ? "_id" : "_airport_id")] = AirportId(text, preview);
                continue;
            }
            var spec = FieldSpecs.Get(c.Resource).Find(field);
            if (spec?.Wire is not { } wire)
            { if (Schemas.All[c.Resource].Columns.Contains(field)) throw Block("ApiUnsupported", field); continue; }
            // Fleet sets can only change on scheduled routes.
            if (wire == "fleet_ids" && !create && !(c.Before?.Get("Type") == "scheduled" && c.After.Get("Type") == "scheduled"))
                throw Block("ApiFleetSet");
            if (spec.CreateOnly && !create)
                throw Block("ApiParameterCreateOnly");
            if (!create && c.Resource == ResourceKind.Routes && c.Before?.Get("Type") == "jumpseat" && spec.JumpseatReadOnly)
                throw Block("ApiJumpseatIdentity");
            if (wire == "type" && !create && c.Resource == ResourceKind.Routes && (text == "jumpseat" || c.Before!.Get("Type") == "jumpseat"))
                throw Block("ApiJumpseat");
            if (text.Length == 0)
            {
                if (spec.ClearsToEmptyArray)
                { body[wire] = Array.Empty<long>(); continue; }
                throw Block("ApiClear", field);
            }
            object result = text;
            if (spec.Type == FieldType.Integer || spec.Type == FieldType.KgInteger)
            { if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw Block("ApiInvalid", field); result = number; }
            if (spec.Type == FieldType.Bool)
            { if (!bool.TryParse(text, out var flag)) throw Block("ApiInvalid", field); result = flag; }
            if (spec.Type == FieldType.TagList || spec.Type == FieldType.DayList)
                result = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            if (spec.Type == FieldType.IdList)
                result = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t => preview && spec.Reference == ResourceKind.Fleets ? PreviewReference(t, ResourceKind.Fleets) : PositiveId(t)).Distinct().ToArray();
            if (spec.Type == FieldType.ParameterIdList)
            {
                var ids = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t => PositiveId(t).ToString(CultureInfo.InvariantCulture)).Distinct().ToArray();
                if (ids.Length != 1)
                    throw Block("ApiInvalid", field);
                result = ids;
            }
            if (spec.Type == FieldType.Time)
            {
                if (workspace.LocalRouteTimes)
                    throw Block("ApiUtc");
                if (!TimeOnly.TryParseExact(text, new[] { "HH:mm", "HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    throw Block("ApiInvalid", field);
                var seconds = "00";
                if (text.Length == 5 && c.Before?.RawApiJson is string raw)
                { using var doc = JsonDocument.Parse(raw); if (doc.RootElement.TryGetProperty(wire, out var t) && t.ValueKind == JsonValueKind.String && t.GetString()!.Length == 8) seconds = t.GetString()![6..]; }
                result = text.Length == 5 ? time.ToString("HH:mm", CultureInfo.InvariantCulture) + ":" + seconds : text;
            }
            if (spec.Type == FieldType.Date)
            {
                if (workspace.LocalRouteTimes || !DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw Block("ApiUtc");
                if (c.Before?.RawApiJson is string raw)
                { using var doc = JsonDocument.Parse(raw); if (doc.RootElement.TryGetProperty(wire, out var oldDate) && DateTimeOffset.TryParse(oldDate.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var original)) date = date.AddTicks(original.Ticks % TimeSpan.TicksPerSecond); }
                // Match the documented ISO 8601 example; fractional seconds appear only when the original carried them.
                result = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
            }
            body[wire] = result;
        }
        if (create && c.Resource == ResourceKind.Routes && c.After.Get("Type") != "jumpseat" && !body.ContainsKey("fleet_ids"))
            throw Block("ApiInvalid", "Fleet IDs");
        if (c.Resource == ResourceKind.Routes)
        {
            if (body.ContainsKey("fleet_ids") && c.After.Get("Type") == "scheduled" && ((long[])body["fleet_ids"]!).Length == 0)
                throw Block("ApiInvalid", "Fleet IDs");
            if (!Schemas.RouteTypes.Contains(c.After.Get("Type")))
                throw Block("ApiInvalid", "Type");
            foreach (var (field, min, max) in new[] { ("Callsign", 4, 7), ("Flight Number", 3, 6) })
                if ((create || c.Fields.ContainsKey(field)) && c.After.Get(field).Length is var length && (length < min || length > max))
                    throw Block("ApiInvalid", field);
            if (body.TryGetValue("cost_index", out var costIndex) && !Schemas.ValidCostIndex((string)costIndex!))
                throw Block("ApiInvalid", "Cost Index");
            if (DateTime.TryParseExact(c.After.Get("Start Date"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) && DateTime.TryParseExact(c.After.Get("End Date"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) && end <= start)
                throw Block("ApiInvalid", "End Date");
        }
        if (body.Count == 0)
            throw Block("ApiNoChanges");
        var steps = new List<ApiStep>();
        if (create)
        {
            var properties = SchemasJson.GetProperty(SchemaName(c.Resource, true)).GetProperty("properties");
            var first = body.Where(p => properties.TryGetProperty(p.Key, out _)).ToDictionary();
            var rest = body.Where(p => !properties.TryGetProperty(p.Key, out _)).ToDictionary();
            ValidateBody(c.Resource, true, first);
            steps.Add(new(HttpMethod.Post, Path(c.Resource, c.After, preview: preview), first));
            if (rest.Count > 0)
            { ValidateBody(c.Resource, false, rest); steps.Add(new(HttpMethod.Put, "{created}", rest)); }
        }
        else
        { ValidateBody(c.Resource, false, body); steps.Add(new(HttpMethod.Put, path, body)); }
        return steps;
    }
    public static void ValidateBody(ResourceKind kind, bool create, Dictionary<string, object?> body)
    {
        ValidateCompatibility(body);
        var schema = SchemasJson.GetProperty(SchemaName(kind, create));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(body));
        ValidateSchema(schema, json.RootElement, SchemaName(kind, create));
    }
    static void ValidateCompatibility(IReadOnlyDictionary<string, object?> body)
    {
        if (body.Keys.Any(k => k is "weight_unit" or "simbrief_overrides"))
            throw Block("ApiKnownWeightRestriction");
    }
    static void ValidateSchema(JsonElement schema, JsonElement value, string field)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        { ValidateSchema(SchemasJson.GetProperty(reference.GetString()!.Split('/')[^1]), value, field); return; }
        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(v => v.GetString()).ToArray() : [type.GetString()];
            string actual = value.ValueKind switch { JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number", JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Null => "null", _ => "string" };
            if (!types.Contains(actual))
                throw Block("ApiInvalid", field);
        }
        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(v => v.ToString() == value.ToString()))
            throw Block("ApiInvalid", field);
        if (value.ValueKind == JsonValueKind.String)
        {
            var length = value.GetString()!.Length;
            if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt32() || schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32())
                throw Block("ApiInvalid", field);
        }
        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out var minimum) && value.GetDecimal() < minimum.GetDecimal())
            throw Block("ApiInvalid", field);
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        { if (schema.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) throw Block("ApiInvalid", field); foreach (var item in value.EnumerateArray()) ValidateSchema(items, item, field); }
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties))
        {
            if (schema.TryGetProperty("required", out var required))
            foreach (var r in required.EnumerateArray())
            if (!value.TryGetProperty(r.GetString()!, out _))
                throw Block("ApiInvalid", r.GetString());
            foreach (var p in value.EnumerateObject())
            { if (!properties.TryGetProperty(p.Name, out var child)) throw Block("ApiUnsupported", p.Name); ValidateSchema(child, p.Value, p.Name); }
        }
    }
    public async Task<string?> WriteAsync(ChangeItem change, CancellationToken ct) => await WriteStepsAsync(change, () => Task.CompletedTask, ct);
    internal static Dictionary<string, JsonElement> ExpectedValues(IEnumerable<ApiStep> steps)
        => steps.SelectMany(s => s.Body).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => JsonSerializer.SerializeToElement(g.Last().Value));
    public Task<string?> WriteStepsAsync(ChangeItem change, Func<Task> persist, CancellationToken ct)
        => WriteStepsAsync(change, Plan(change), persist, ct);
    internal static void PrepareSteps(ChangeItem change, List<ApiStep> steps)
        => change.ApiSteps = steps.Select(s => new BatchStep { Method = s.Method.Method, Path = s.Path, Body = s.Body.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)) }).ToList();
    internal async Task<string?> WriteStepsAsync(ChangeItem change, List<ApiStep> steps, Func<Task> persist, CancellationToken ct)
    {
        change.ApiExpectedValues = ExpectedValues(steps);
        if (change.ApiSteps.Count != steps.Count)
            PrepareSteps(change, steps);
        for (int index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.Method == HttpMethod.Post && change.CreateCompleted)
                continue;
            var path = step.Path == "{created}" ? Path(change.Resource, change.After, change.RemoteId) : step.Path;
            ValidateCompatibility(step.Body);
            var record = change.ApiSteps[index];
            record.Path = path;
            record.State = BatchStepState.InFlight;
            record.StartedAt = transport.UtcNow;
            record.FinishedAt = null;
            await persist();
            JsonDocument result;
            Func<DateTimeOffset, Task>? start = ApiOperationPolicy.IsJumpseatCreate(change) && step.Method == HttpMethod.Post ? async when =>
                { record.StartedAt = when; record.DeadlineAt = transport.RequestTimeout > TimeSpan.Zero ? when + transport.RequestTimeout : null; await persist(); }
            : null;
            try
            { result = await transport.SendAsync(step.Method, new(BaseUri, path), step.Method == HttpMethod.Delete ? null : step.Body, ct, start); }
            finally { record.FinishedAt = transport.UtcNow; }
            change.DiagnosticDetails = transport.LastResponse is { } evidence ? evidence with { Phase = "Writing" } : change.DiagnosticDetails;
            using var response = result;
            if (step.Method == HttpMethod.Post)
            {
                // Keep the created ID durable before a full parse can fail on an unexpected response shape.
                if (response.RootElement.ValueKind == JsonValueKind.Object && response.RootElement.TryGetProperty("id", out var createdId) && long.TryParse(createdId.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId) && parsedId > 0)
                {
                    change.RemoteId = parsedId.ToString(CultureInfo.InvariantCulture);
                    change.CreateCompleted = true;
                    record.RemoteId = change.RemoteId;
                    record.State = BatchStepState.ResponseReceived;
                    await persist();
                }
                var created = FromJson(change.Resource, response.RootElement);
                change.RemoteId = created.Identity!.RemoteId;
                change.After = change.After.WithIdentity(created.Identity);
                change.CreateCompleted = true;
                record.RemoteId = change.RemoteId;
            }
            record.State = BatchStepState.ResponseReceived;
            change.WriteAccepted = index == steps.Count - 1;
            await persist();
        }
        return change.RemoteId ?? change.After.Identity?.RemoteId;
    }
    public bool Matches(ChangeItem item, DataRow current, DataRow expected, bool before)
    {
        CheckConnection(current);
        CheckConnection(expected);
        if (!before)
            return VerifyReadback(item, current, expected, item.ApiExpectedValues ?? ExpectedValues(Plan(item))).Passed;
        if (item.Resource == ResourceKind.Airports && item.Kind == ChangeKind.Create
            && !HasAirportAlias(current, expected.Get("ICAO/IATA")))
            return false;
        if (item.Resource == ResourceKind.Aircraft && current.Identity?.ParentFleetId != expected.Identity?.ParentFleetId)
            return false;
        IEnumerable<string> fields = before && item.Kind == ChangeKind.Delete ? WireFields(item.Resource).Select(f => f.Csv) : item.Fields.Keys;
        var relevant = fields.Where(f => FieldSpecs.Get(item.Resource).Find(f)?.Wire != null || Schemas.ReferenceFields(item.Resource).Contains(f)).ToList();
        if (!relevant.All(f => f is "Departure Airport (ICAO/IATA)" or "Arrival Airport (ICAO/IATA)"
            ? SameEndpoint(item.Resource, current, expected, f, before)
            : Equivalent(f, current.Get(f), f == "Fleet ID" ? expected.Identity?.ParentFleetId ?? expected.Get(f) : expected.Get(f))))
            return false;
        if (current.RawApiJson != null)
        {
            using var actual = JsonDocument.Parse(current.RawApiJson);
            if (before && expected.RawApiJson != null)
            {
                using var original = JsonDocument.Parse(expected.RawApiJson);
                foreach (var f in relevant)
                    if (FieldSpecs.Get(item.Resource).Find(f) is { Wire: { } wire, Type: var type } && (type == FieldType.Time || type == FieldType.Date))
                        if (original.RootElement.TryGetProperty(wire, out var a) && actual.RootElement.TryGetProperty(wire, out var b) && a.ToString() != b.ToString())
                            return false;
            }

        }
        return true;
    }
    // The only read-back verdict after a write: it passes exactly when no reason is listed, so the saved
    // evidence always explains a failed check. Matches, MatchesCreated and RecordReadback all use it.
    internal ReadbackVerification VerifyReadback(ChangeItem item, DataRow? current, DataRow expected, IReadOnlyDictionary<string, JsonElement> values)
    {
        var reasons = new List<ApiFieldDifference>();
        if (current?.RawApiJson is not string json)
        { reasons.Add(new("record", item.RemoteId ?? item.Before?.Identity?.RemoteId, null)); return new(false, reasons); }
        string Aliases() => string.Join(",", AirportAliases(current));
        var code = expected.Get("ICAO/IATA");
        if (item.Resource == ResourceKind.Airports && item.Kind == ChangeKind.Create && !HasAirportAlias(current, code))
            reasons.Add(new("ICAO/IATA", code, Aliases()));
        if (item.Resource == ResourceKind.Aircraft && (item.Kind == ChangeKind.Create || IndependentApiEvidence.IsMove(item))
            && current.Identity?.ParentFleetId != expected.Get("Fleet ID"))
            reasons.Add(new("fleet_id", expected.Get("Fleet ID"), current.Identity?.ParentFleetId));
        using var raw = JsonDocument.Parse(json);
        foreach (var (wire, wanted) in values)
        {
            if (wire == "icao_iata")
            {
                // The same code as the row's ICAO/IATA is already listed above.
                if (!HasAirportAlias(current, wanted.GetString()!) && !reasons.Any(r => r.Field == "ICAO/IATA" && string.Equals(r.Expected, wanted.GetString(), StringComparison.OrdinalIgnoreCase)))
                    reasons.Add(new(wire, wanted.GetRawText(), Aliases()));
                continue;
            }
            if (!raw.RootElement.TryGetProperty(wire, out var actual))
            { reasons.Add(new(wire, wanted.GetRawText(), null)); continue; }
            if (ApiOperationPolicy.IsJumpseatCreate(item) && (wire is "callsign" or "flight_number") && actual.ValueKind == JsonValueKind.Null)
                continue;
            if (!WireEquivalent(item.Resource, wire, actual, wanted))
                reasons.Add(new(wire, wanted.GetRawText(), actual.GetRawText()));
        }
        if (ApiOperationPolicy.IsJumpseatCreate(item) && item.CanProposeRecoveryId)
        {
            // A recovered jumpseat must be new since the request and created within its time window.
            var id = current.Identity!.RemoteId;
            if (item.PreCreateCandidateIds == null)
                reasons.Add(new("pre_create_candidates", "recorded", null));
            else if (item.PreCreateCandidateIds.Contains(id))
                reasons.Add(new("pre_create_candidates", "new record", id));
            var step = item.ApiSteps.FirstOrDefault(s => s.Method == "POST");
            var end = step?.FinishedAt ?? step?.DeadlineAt;
            if (step?.StartedAt == null || end == null)
                reasons.Add(new("request_window", "recorded", null));
            else
            {
                var window = $"{step.StartedAt.Value.AddSeconds(-60):O} .. {end.Value.AddSeconds(60):O}";
                var present = raw.RootElement.TryGetProperty("created_at", out var createdAt);
                if (!present || !DateTimeOffset.TryParse(createdAt.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
                    || created < step.StartedAt.Value.AddSeconds(-60) || created > end.Value.AddSeconds(60))
                    reasons.Add(new("created_at", window, present ? createdAt.GetRawText() : null));
            }
        }
        return new(reasons.Count == 0, reasons);
    }
    static bool WireEquivalent(ResourceKind kind, string wire, JsonElement actual, JsonElement wanted)
    {
        var type = FieldSpecs.Get(kind).FindByWire(wire)?.Type ?? default;
        if (type == FieldType.Date)
            return actual.ValueKind == JsonValueKind.String && wanted.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(actual.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
                && DateTimeOffset.TryParse(wanted.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var b) && a == b;
        if (wanted.ValueKind == JsonValueKind.Array)
        {
            if (actual.ValueKind != JsonValueKind.Array)
                return false;
            // The documented response allows string IDs, while requests require integer IDs.
            string Key(JsonElement value) => (type == FieldType.IdList || type == FieldType.ParameterIdList)
                && long.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? "id:" + id.ToString(CultureInfo.InvariantCulture) : value.ValueKind == JsonValueKind.String ? "string:" + value.GetString() : value.GetRawText();
            var keys = actual.EnumerateArray().Select(Key).ToArray();
            return keys.Distinct().Count() == keys.Length && wanted.EnumerateArray().Select(Key).ToHashSet().SetEquals(keys);
        }
        return JsonElement.DeepEquals(actual, wanted);
    }
    internal static bool Equivalent(string field, string a, string b) => FieldSpecs.IsSetCompared(field)
        ? a.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet().SetEquals(b.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) : a == b;
}
