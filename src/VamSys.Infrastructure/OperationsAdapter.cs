using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed record ApiStep(HttpMethod Method, string Path, Dictionary<string,object?> Body);
public interface IApiResourceAdapter
{
    DataRow FromJson(ResourceKind kind, JsonElement value);
    List<ApiStep> Plan(ChangeItem change);
}
public sealed partial class OperationsAdapter(OperationsTransport transport, Workspace workspace) : IResourceReader, IResourceWriter, IApiResourceAdapter
{
    public static readonly Uri BaseUri = new("https://vamsys.io/api/v3/operations/");
    static readonly JsonElement SchemasJson = JsonDocument.Parse(typeof(OperationsAdapter).Assembly.GetManifestResourceStream("Operations.OpenApi.json")!).RootElement.GetProperty("components").GetProperty("schemas");
    Dictionary<(ResourceKind,string),DataRow> known = new();
    public string? SessionAirlineId { get; private set; }
    public string ConnectionId => workspace.AirlineId ?? throw Block("ApiConnectFirst");
    public static InvalidOperationException Block(string key, string? field = null) => MessageErrors.Attach(new InvalidOperationException(Messages.Define(key, ("field",field))), Messages.Define(key, ("field",field)));
    public static string Collection(ResourceKind k) => k switch { ResourceKind.Airports => "airports", ResourceKind.Fleets => "fleet", ResourceKind.Routings => "routings", ResourceKind.Routes => "routes", _ => throw new ArgumentException() };
    static readonly Dictionary<ResourceKind, Dictionary<string,string>> Maps = new()
    {
        [ResourceKind.Airports] = new() { ["Name"]="name", ["Category"]="category", ["Container IDs"]="container_ids" },
        [ResourceKind.Fleets] = new() { ["Name"]="name", ["Type Code"]="code", ["Type (pax/cargo/...)"]="type", ["Max Passengers"]="max_pax", ["Max Freight"]="max_cargo", ["Container Units"]="container_units", ["Hide in Phoenix"]="hide_in_phoenix" },
        [ResourceKind.Aircraft] = new() { ["Name"]="name", ["Registration"]="registration", ["SELCAL"]="selcal", ["Fin Number"]="fin_number", ["Hex Code"]="hexcode", ["Passengers"]="passengers", ["Freight"]="cargo", ["Internal Remarks"]="internal_remarks" },
        [ResourceKind.Routings] = new() { ["Route String"]="route", ["Remarks"]="remarks", ["Internal Remarks"]="internal_remarks", ["Tags"]="tag", ["Days of Operation"]="days_of_operation" },
        [ResourceKind.Routes] = new() { ["Type"]="type", ["Callsign"]="callsign", ["Flight Number"]="flight_number", ["Fleet IDs"]="fleet_ids", ["Start Date"]="start_date", ["End Date"]="end_date", ["Departure Time (HH:MM)"]="departure_time", ["Arrival Time (HH:MM)"]="arrival_time", ["Service Days"]="service_days", ["Routing"]="route", ["Remarks"]="remarks", ["Internal Remarks"]="internal_remarks", ["Tags"]="tag", ["Is Hidden"]="hidden", ["Altitude"]="altitude", ["Cost Index"]="cost_index" }
    };
    static string Value(JsonElement e) => e.ValueKind switch { JsonValueKind.Null => "", JsonValueKind.Array => string.Join(',',e.EnumerateArray().Select(Value)), JsonValueKind.True => "TRUE", JsonValueKind.False => "FALSE", JsonValueKind.String => e.GetString()!, _ => e.ToString() };
    public DataRow FromJson(ResourceKind kind, JsonElement value)
    {
        var id = value.GetProperty("id").ToString();
        var airline = value.GetProperty("airline_id").ToString();
        if (workspace.AirlineId != null && workspace.AirlineId != airline) throw Block("ApiWrongVa");
        workspace.AirlineId ??= airline;
        SessionAirlineId = airline;
        string? parent = kind == ResourceKind.Aircraft ? value.GetProperty("fleet_id").ToString() : null;
        var row = new DataRow { Identity = new(id, airline, parent), RawApiJson = value.GetRawText() };
        row.Fields["_delete"] = "FALSE";
        if (kind != ResourceKind.Airports) row.Fields["ID"] = id;
        else row.Fields["ICAO/IATA"] = value.TryGetProperty("icao", out var icao) && icao.ValueKind == JsonValueKind.String ? icao.GetString()! : Value(value.GetProperty("iata"));
        foreach (var (field, wire) in Maps[kind]) if (value.TryGetProperty(wire, out var v))
        {
            var text = Value(v);
            if (wire is "departure_time" or "arrival_time" && text.Length == 8) text = text[..5];
            if (wire is "start_date" or "end_date" && DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)) text = date.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture);
            row.Fields[field] = text;
        }
        if (parent != null) row.Fields["Fleet ID"] = parent;
        if (kind is ResourceKind.Routes or ResourceKind.Routings)
        {
            foreach (var side in new[]{"Departure","Arrival"})
            {
                var wire = side.ToLowerInvariant() + (kind == ResourceKind.Routes ? "_id" : "_airport_id");
                var airportId = value.GetProperty(wire).ToString();
                var airport = Known(ResourceKind.Airports,airportId);
                row.Fields[side + " Airport (ICAO/IATA)"] = airport?.Get("ICAO/IATA") ?? "unresolved:" + airportId;
            }
        }
        known[(kind,id)] = row; return row;
    }
    DataRow? Known(ResourceKind kind, string id) => known.GetValueOrDefault((kind,id)) ?? workspace.Resources[kind].Snapshot.FirstOrDefault(r => r.Identity?.RemoteId == id && r.Identity.ConnectionId == workspace.AirlineId);
    public async IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind kind, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (kind == ResourceKind.Aircraft)
        {
            await foreach (var fleet in ReadAllAsync(ResourceKind.Fleets,ct))
                await foreach (var element in transport.ReadPagesAsync(new(BaseUri,$"fleet/{fleet.Identity!.RemoteId}/aircraft"),ct)) yield return FromJson(kind,element);
        }
        else await foreach (var element in transport.ReadPagesAsync(new(BaseUri,Collection(kind)),ct)) yield return FromJson(kind,element);
    }
    public async Task RefreshSnapshotsAsync(Func<Workspace,Task> persist,CancellationToken ct,Action<ResourceKind,int>? progress=null)
    {
        // Reads must not mutate the live identity index, even if a later page or save fails.
        var readWorkspace=new Workspace { AirlineId=workspace.AirlineId };
        var reader=new OperationsAdapter(transport,readWorkspace);
        var incoming=new Dictionary<ResourceKind,List<DataRow>>();
        foreach(var kind in Enum.GetValues<ResourceKind>())
        {
            var rows=new List<DataRow>();
            await foreach(var row in reader.ReadAllAsync(kind,ct))
            { rows.Add(row); if(rows.Count%100==0) progress?.Invoke(kind,rows.Count); }
            incoming[kind]=rows;
        }
        ct.ThrowIfCancellationRequested();
        var staged=Workspace.Deserialize(workspace.Serialize());
        staged.AirlineId ??= readWorkspace.AirlineId;
        foreach(var (kind,rows) in incoming)
        {
            SnapshotMerger.Merge(kind,staged.Resources[kind],rows);
            if(!staged.VerifiedOperations.Contains(kind+":Read")) staged.VerifiedOperations.Add(kind+":Read");
        }
        await persist(staged);
        workspace.StorageRevision=staged.StorageRevision;
        // No await between the durable snapshot and its in-memory index replacement.
        workspace.Resources=staged.Resources;
        workspace.VerifiedOperations=staged.VerifiedOperations;
        workspace.AirlineId=staged.AirlineId;
        known=reader.known;
        SessionAirlineId=reader.SessionAirlineId ?? SessionAirlineId;
    }
    public void Forget(ResourceKind kind,string id) => known.Remove((kind,id));
    string Path(ResourceKind kind, DataRow row, string? remoteId = null, bool preview = false)
    {
        var identity = row.Identity;
        if (identity != null && identity.ConnectionId != ConnectionId) throw Block("ApiWrongVa");
        var id = remoteId ?? identity?.RemoteId;
        if (kind == ResourceKind.Aircraft)
        {
            var parent = identity?.ParentFleetId ?? row.Get("Fleet ID");
            if(preview && parent.StartsWith("local:")) parent = PreviewReference(parent,ResourceKind.Fleets).ToString(CultureInfo.InvariantCulture);
            PositiveId(parent); return $"fleet/{parent}/aircraft" + (id == null ? "" : "/" + PositiveId(id));
        }
        return Collection(kind) + (id == null ? "" : "/" + PositiveId(id));
    }
    public async Task<DataRow?> FindAsync(ResourceKind kind, string id, CancellationToken ct)
    {
        var row = Known(kind,id);
        if (kind == ResourceKind.Aircraft && row == null) throw Block("ApiRefreshFirst");
        return await FindRow(kind,row ?? new DataRow { Identity = new(id,ConnectionId) },id,ct);
    }
    public async Task<DataRow?> FindRow(ResourceKind kind, DataRow row, string? id, CancellationToken ct)
    {
        try
        {
            using var json = await transport.GetAsync(new(BaseUri,Path(kind,row,id)),ct);
            var root = json.RootElement;
            var found = FromJson(kind,kind == ResourceKind.Routings ? root : root.GetProperty("data"));
            if (found.Identity!.RemoteId != (id ?? row.Identity?.RemoteId)) throw Block("ApiWrongVa");
            return found;
        }
        catch (ApiResponseException ex) when (ex.Status == HttpStatusCode.NotFound) { return null; }
    }
    static long PositiveId(string text) => long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var id) && id > 0 ? id : throw Block("ApiInvalid",text);
    long PreviewReference(string token,ResourceKind kind)
    {
        if(token.StartsWith("local:") && workspace.Resources[kind].Draft.Any(r=>"local:"+r.LocalId==token && !Schemas.Deleted(r))) return 1;
        return PositiveId(token);
    }
    long AirportId(string code,bool preview=false)
    {
        if(preview && code.StartsWith("local:")) return PreviewReference(code,ResourceKind.Airports);
        var match = known.Where(p => p.Key.Item1 == ResourceKind.Airports).Select(p => p.Value).Concat(workspace.Resources[ResourceKind.Airports].Snapshot)
            .Where(r => r.Identity?.ConnectionId == workspace.AirlineId && (r.Get("ICAO/IATA").Equals(code,StringComparison.OrdinalIgnoreCase) || r.Identity!.RemoteId == code || HasAirportAlias(r,code)))
            .DistinctBy(r=>r.Identity!.RemoteId).ToList();
        if (match.Count != 1) throw Block("ApiInvalid",code);
        return PositiveId(match[0].Identity!.RemoteId);
    }
    static bool HasAirportAlias(DataRow r,string code)
    {
        if(r.RawApiJson == null || string.IsNullOrWhiteSpace(code)) return false;
        using var doc=JsonDocument.Parse(r.RawApiJson);
        return new[]{"icao","iata"}.Any(k=>doc.RootElement.TryGetProperty(k,out var v) && v.ValueKind==JsonValueKind.String && v.GetString()!.Equals(code,StringComparison.OrdinalIgnoreCase));
    }
    void CheckConnection(DataRow row)
    {
        if(row.Identity != null && row.Identity.ConnectionId != ConnectionId) throw Block("ApiWrongVa");
    }
    long EndpointId(ResourceKind kind,DataRow row,string field,bool remote)
    {
        CheckConnection(row);
        if(remote)
        {
            if(row.RawApiJson == null) throw Block("ApiRefreshFirst");
            using var doc=JsonDocument.Parse(row.RawApiJson);
            var wire=(field.StartsWith("Departure") ? "departure" : "arrival") + (kind==ResourceKind.Routes ? "_id" : "_airport_id");
            if(!doc.RootElement.TryGetProperty(wire,out var value)) throw Block("ApiRefreshFirst");
            return PositiveId(value.ToString());
        }
        return AirportId(row.Get(field));
    }
    bool SameEndpoint(ResourceKind kind,DataRow current,DataRow expected,string field,bool before=false)
        => EndpointId(kind,current,field,true)==EndpointId(kind,expected,field,before && expected.RawApiJson!=null);

    internal bool IsDuplicate(ChangeItem item,DataRow current)
    {
        CheckConnection(current); CheckConnection(item.After);
        if(item.Resource==ResourceKind.Airports) return HasAirportAlias(current,item.After.Get("ICAO/IATA"));
        if(item.Resource is ResourceKind.Routes or ResourceKind.Routings)
        {
            var signature=item.Resource==ResourceKind.Routes ? "Flight Number" : "Route String";
            return SameEndpoint(item.Resource,current,item.After,"Departure Airport (ICAO/IATA)")
                && SameEndpoint(item.Resource,current,item.After,"Arrival Airport (ICAO/IATA)")
                && current.Get(signature).Equals(item.After.Get(signature),StringComparison.OrdinalIgnoreCase);
        }
        var field=item.Resource==ResourceKind.Aircraft ? "Registration" : "Name";
        return current.Get(field).Equals(item.After.Get(field),StringComparison.OrdinalIgnoreCase);
    }
    static string SchemaName(ResourceKind kind,bool create) => (kind,create) switch
    {
        (ResourceKind.Airports,true)=>"CreateAirportData",(ResourceKind.Airports,false)=>"UpdateAirportData",
        (ResourceKind.Fleets,true)=>"CreateFleetData",(ResourceKind.Fleets,false)=>"UpdateFleetData",
        (ResourceKind.Aircraft,true)=>"CreateAircraftData",(ResourceKind.Aircraft,false)=>"UpdateAircraftData",
        (ResourceKind.Routings,true)=>"CreateRoutingData",(ResourceKind.Routings,false)=>"UpdateRoutingData",
        (ResourceKind.Routes,true)=>"StoreRouteRequest",_=>"UpdateRouteRequest"
    };
    public List<ApiStep> Plan(ChangeItem c) => Plan(c,false);
    public List<ApiStep> PreviewPlan(ChangeItem c) => Plan(c,true);
    List<ApiStep> Plan(ChangeItem c,bool preview)
    {
        if (c.Before != null && c.Before.Identity == null) throw Block("ApiRefreshFirst");
        if (c.Kind != ChangeKind.Create && c.After.Identity == null) throw Block("ApiRefreshFirst");
        if (c.Kind == ChangeKind.Create && c.After.Get("ID") != "" && c.RemoteId == null) throw Block("ApiRefreshFirst");
        var path = Path(c.Resource,c.Before ?? c.After,c.RemoteId,preview);
        if(c.Kind == ChangeKind.Delete) return [new(HttpMethod.Delete,path,[])];
        bool create = c.Kind == ChangeKind.Create;
        var body = new Dictionary<string,object?>();
        foreach(var (field, intent) in c.Fields)
        {
            if (intent.Intent == FieldIntent.Unspecified || field == "_delete") continue;
            var text = intent.Value ?? "";
            if (create && text.Length == 0) continue;
            if (field == "ID") throw Block("ApiImmutable",field);
            if (field == "Fleet ID")
            { if (!create) throw Block("ApiMoveFleet"); if(preview) PreviewReference(text,ResourceKind.Fleets); else PositiveId(text); continue; }
            if (field == "ICAO/IATA")
            { if (!create) throw Block("ApiImmutable",field); body["icao_iata"] = text; continue; }
            if(field is "Departure Airport (ICAO/IATA)" or "Arrival Airport (ICAO/IATA)")
            {
                if (!create) throw Block("ApiImmutable",field);
                body[(field.StartsWith("Departure") ? "departure" : "arrival") + (c.Resource == ResourceKind.Routes ? "_id" : "_airport_id")] = AirportId(text,preview); continue;
            }
            if (!Maps[c.Resource].TryGetValue(field,out var wire))
            { if (Schemas.All[c.Resource].Columns.Contains(field)) throw Block("ApiUnsupported",field); continue; }
            if (wire == "fleet_ids" && !create) throw Block("ApiFleetSet");
            if (wire == "type" && !create && c.Resource == ResourceKind.Routes && (text == "jumpseat" || c.Before!.Get("Type") == "jumpseat")) throw Block("ApiJumpseat");
            if(text.Length == 0)
            {
                if(c.Resource==ResourceKind.Airports && wire=="container_ids") { body[wire]=Array.Empty<long>(); continue; }
                throw Block("ApiClear",field);
            }
            object result = text;
            if (wire is "max_pax" or "max_cargo" or "container_units" or "passengers" or "cargo" or "altitude")
            { if(!long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var number)) throw Block("ApiInvalid",field); result = number; }
            if (wire is "hide_in_phoenix" or "hidden") { if(!bool.TryParse(text,out var flag)) throw Block("ApiInvalid",field); result = flag; }
            if (wire is "tag" or "service_days" or "days_of_operation") result = text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            if (wire is "fleet_ids" or "container_ids") result = text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(t=>preview && wire=="fleet_ids"?PreviewReference(t,ResourceKind.Fleets):PositiveId(t)).Distinct().ToArray();
            if (wire is "departure_time" or "arrival_time")
            {
                if(workspace.LocalRouteTimes) throw Block("ApiUtc");
                if(!TimeOnly.TryParseExact(text,new[]{"HH:mm","HH:mm:ss"},CultureInfo.InvariantCulture,DateTimeStyles.None,out var time)) throw Block("ApiInvalid",field);
                var seconds = "00";
                if(text.Length == 5 && c.Before?.RawApiJson is string raw) { using var doc=JsonDocument.Parse(raw); if(doc.RootElement.TryGetProperty(wire,out var t) && t.ValueKind==JsonValueKind.String && t.GetString()!.Length==8) seconds=t.GetString()![6..]; }
                result = text.Length == 5 ? time.ToString("HH:mm",CultureInfo.InvariantCulture)+":"+seconds : text;
            }
            if (wire is "start_date" or "end_date")
            {
                if(workspace.LocalRouteTimes || !DateTime.TryParseExact(text,"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)) throw Block("ApiUtc");
                if(c.Before?.RawApiJson is string raw)
                { using var doc=JsonDocument.Parse(raw); if(doc.RootElement.TryGetProperty(wire,out var oldDate) && DateTimeOffset.TryParse(oldDate.ToString(),CultureInfo.InvariantCulture,DateTimeStyles.None,out var original)) date=date.AddTicks(original.Ticks%TimeSpan.TicksPerSecond); }
                result = DateTime.SpecifyKind(date,DateTimeKind.Utc).ToString("O",CultureInfo.InvariantCulture);
            }
            body[wire] = result;
        }
        if (create && c.Resource == ResourceKind.Routes && c.After.Get("Type") != "jumpseat" && !body.ContainsKey("fleet_ids")) throw Block("ApiInvalid","Fleet IDs");
        if(c.Resource==ResourceKind.Routes)
        {
            if(!new[]{"scheduled","cargo","charter","training","vfr","repositioning","jumpseat"}.Contains(c.After.Get("Type"))) throw Block("ApiInvalid","Type");
            foreach(var (field,min,max) in new[]{("Callsign",4,7),("Flight Number",3,6)})
                if((create || c.Fields.ContainsKey(field)) && c.After.Get(field).Length is var length && (length<min || length>max)) throw Block("ApiInvalid",field);
            if(DateTime.TryParseExact(c.After.Get("Start Date"),"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start) && DateTime.TryParseExact(c.After.Get("End Date"),"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var end) && end<=start) throw Block("ApiInvalid","End Date");
        }
        if(body.Count == 0) throw Block("ApiNoChanges");
        var steps = new List<ApiStep>();
        if(create)
        {
            var properties = SchemasJson.GetProperty(SchemaName(c.Resource,true)).GetProperty("properties");
            var first = body.Where(p=>properties.TryGetProperty(p.Key,out _)).ToDictionary();
            var rest = body.Where(p=>!properties.TryGetProperty(p.Key,out _)).ToDictionary();
            ValidateBody(c.Resource,true,first); steps.Add(new(HttpMethod.Post,Path(c.Resource,c.After,preview:preview),first));
            if(rest.Count>0) { ValidateBody(c.Resource,false,rest); steps.Add(new(HttpMethod.Put,"{created}",rest)); }
        }
        else { ValidateBody(c.Resource,false,body); steps.Add(new(HttpMethod.Put,path,body)); }
        return steps;
    }
    public static void ValidateBody(ResourceKind kind,bool create,Dictionary<string,object?> body)
    {
        var schema = SchemasJson.GetProperty(SchemaName(kind,create));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(body));
        ValidateSchema(schema,json.RootElement,SchemaName(kind,create));
    }
    static void ValidateSchema(JsonElement schema,JsonElement value,string field)
    {
        if(schema.TryGetProperty("$ref",out var reference)) { ValidateSchema(SchemasJson.GetProperty(reference.GetString()!.Split('/')[^1]),value,field); return; }
        if(schema.TryGetProperty("type",out var type))
        {
            var types=type.ValueKind==JsonValueKind.Array ? type.EnumerateArray().Select(v=>v.GetString()).ToArray() : [type.GetString()];
            string actual=value.ValueKind switch { JsonValueKind.Object=>"object",JsonValueKind.Array=>"array",JsonValueKind.Number=>value.TryGetInt64(out _)?"integer":"number",JsonValueKind.True or JsonValueKind.False=>"boolean",JsonValueKind.Null=>"null",_=>"string" };
            if(!types.Contains(actual)) throw Block("ApiInvalid",field);
        }
        if(schema.TryGetProperty("enum",out var allowed) && !allowed.EnumerateArray().Any(v=>v.ToString()==value.ToString())) throw Block("ApiInvalid",field);
        if(value.ValueKind==JsonValueKind.String)
        {
            var length=value.GetString()!.Length;
            if(schema.TryGetProperty("minLength",out var min)&&length<min.GetInt32() || schema.TryGetProperty("maxLength",out var max)&&length>max.GetInt32()) throw Block("ApiInvalid",field);
        }
        if(value.ValueKind==JsonValueKind.Number && schema.TryGetProperty("minimum",out var minimum) && value.GetDecimal()<minimum.GetDecimal()) throw Block("ApiInvalid",field);
        if(value.ValueKind==JsonValueKind.Array && schema.TryGetProperty("items",out var items))
        { if(schema.TryGetProperty("maxItems",out var max)&&value.GetArrayLength()>max.GetInt32()) throw Block("ApiInvalid",field); foreach(var item in value.EnumerateArray()) ValidateSchema(items,item,field); }
        if(value.ValueKind==JsonValueKind.Object && schema.TryGetProperty("properties",out var properties))
        {
            if(schema.TryGetProperty("required",out var required)) foreach(var r in required.EnumerateArray()) if(!value.TryGetProperty(r.GetString()!,out _)) throw Block("ApiInvalid",r.GetString());
            foreach(var p in value.EnumerateObject()) { if(!properties.TryGetProperty(p.Name,out var child)) throw Block("ApiUnsupported",p.Name); ValidateSchema(child,p.Value,p.Name); }
        }
    }
    public async Task<string?> WriteAsync(ChangeItem change,CancellationToken ct) => await WriteStepsAsync(change,()=>Task.CompletedTask,ct);
    internal static Dictionary<string,JsonElement> ExpectedValues(IEnumerable<ApiStep> steps)
        => steps.SelectMany(s=>s.Body).GroupBy(p=>p.Key).ToDictionary(g=>g.Key,g=>JsonSerializer.SerializeToElement(g.Last().Value));
    public Task<string?> WriteStepsAsync(ChangeItem change,Func<Task> persist,CancellationToken ct)
        => WriteStepsAsync(change,Plan(change),persist,ct);
    internal static void PrepareSteps(ChangeItem change,List<ApiStep> steps)
        => change.ApiSteps=steps.Select(s=>new BatchStep{Method=s.Method.Method,Path=s.Path,Body=s.Body.ToDictionary(p=>p.Key,p=>JsonSerializer.SerializeToElement(p.Value))}).ToList();
    internal async Task<string?> WriteStepsAsync(ChangeItem change,List<ApiStep> steps,Func<Task> persist,CancellationToken ct)
    {
        change.ApiExpectedValues=ExpectedValues(steps);
        if(change.ApiSteps.Count!=steps.Count) PrepareSteps(change,steps);
        for(int index=0;index<steps.Count;index++)
        {
            var step=steps[index];
            if(step.Method==HttpMethod.Post && change.CreateCompleted) continue;
            var path=step.Path=="{created}" ? Path(change.Resource,change.After,change.RemoteId) : step.Path;
            var record=change.ApiSteps[index];record.Path=path;record.State=BatchStepState.InFlight;await persist();
            using var response=await transport.SendAsync(step.Method,new(BaseUri,path),step.Method==HttpMethod.Delete?null:step.Body,ct);
            if(step.Method==HttpMethod.Post)
            {
                var created=FromJson(change.Resource,response.RootElement);
                change.RemoteId=created.Identity!.RemoteId; change.After.Identity=created.Identity;
                change.CreateCompleted=true;record.RemoteId=change.RemoteId;
            }
            record.State=BatchStepState.ResponseReceived;change.WriteAccepted=index==steps.Count-1; await persist();
        }
        return change.RemoteId ?? change.After.Identity?.RemoteId;
    }
    public bool Matches(ChangeItem item,DataRow current,DataRow expected,bool before)
    {
        CheckConnection(current); CheckConnection(expected);
        if(item.Resource==ResourceKind.Airports && item.Kind==ChangeKind.Create
            && !HasAirportAlias(current,expected.Get("ICAO/IATA"))) return false;
        if(!before)
        {
            if(current.RawApiJson==null) return false;
            var values=item.ApiExpectedValues ?? ExpectedValues(Plan(item));
            if(item.Resource==ResourceKind.Aircraft && item.Kind==ChangeKind.Create
                && current.Identity?.ParentFleetId!=expected.Get("Fleet ID")) return false;
            using var raw=JsonDocument.Parse(current.RawApiJson);
            foreach(var (wire,wanted) in values)
            {
                if(wire=="icao_iata")
                { if(!HasAirportAlias(current,wanted.GetString()!)) return false; continue; }
                if(!raw.RootElement.TryGetProperty(wire,out var actual) || !WireEquivalent(wire,actual,wanted)) return false;
            }
            return true;
        }
        IEnumerable<string> fields = before && item.Kind==ChangeKind.Delete ? Maps[item.Resource].Keys : item.Fields.Keys;
        var relevant=fields.Where(f=>Maps[item.Resource].ContainsKey(f)||Schemas.ReferenceFields(item.Resource).Contains(f)).ToList();
        if(!relevant.All(f=>f is "Departure Airport (ICAO/IATA)" or "Arrival Airport (ICAO/IATA)"
            ? SameEndpoint(item.Resource,current,expected,f,before)
            : Equivalent(f,current.Get(f),expected.Get(f)))) return false;
        if(current.RawApiJson != null)
        {
            using var actual=JsonDocument.Parse(current.RawApiJson);
            if(before && expected.RawApiJson != null)
            {
                using var original=JsonDocument.Parse(expected.RawApiJson);
                foreach(var f in relevant)
                    if(Maps[item.Resource].TryGetValue(f,out var wire) && wire is "departure_time" or "arrival_time" or "start_date" or "end_date")
                        if(original.RootElement.TryGetProperty(wire,out var a) && actual.RootElement.TryGetProperty(wire,out var b) && a.ToString()!=b.ToString()) return false;
            }

        }
        return true;
    }
    static bool WireEquivalent(string wire,JsonElement actual,JsonElement wanted)
    {
        if(wire is "start_date" or "end_date")
            return actual.ValueKind==JsonValueKind.String && wanted.ValueKind==JsonValueKind.String
                && DateTimeOffset.TryParse(actual.GetString(),CultureInfo.InvariantCulture,DateTimeStyles.None,out var a)
                && DateTimeOffset.TryParse(wanted.GetString(),CultureInfo.InvariantCulture,DateTimeStyles.None,out var b) && a==b;
        if(wanted.ValueKind==JsonValueKind.Array)
        {
            if(actual.ValueKind!=JsonValueKind.Array) return false;
            // The documented response allows string IDs, while requests require integer IDs.
            string Key(JsonElement value) => wire is "fleet_ids" or "container_ids"
                && long.TryParse(value.ToString(),NumberStyles.None,CultureInfo.InvariantCulture,out var id)
                ? "id:"+id.ToString(CultureInfo.InvariantCulture) : value.ValueKind==JsonValueKind.String ? "string:"+value.GetString() : value.GetRawText();
            return wanted.EnumerateArray().Select(Key).ToHashSet().SetEquals(actual.EnumerateArray().Select(Key));
        }
        return JsonElement.DeepEquals(actual,wanted);
    }
    static bool Equivalent(string field,string a,string b) => field is "Fleet IDs" or "Tags" or "Service Days" or "Days of Operation" or "Container IDs"
        ? a.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToHashSet().SetEquals(b.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)) : a==b;
}
