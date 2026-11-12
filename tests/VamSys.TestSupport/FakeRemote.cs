using System.Net;
using System.Text.Json.Nodes;

namespace VamSys.TestSupport;

/// <summary>
/// A small stateful Operations API behind <see cref="FakeOperationsServer"/> (which records every request).
/// Shapes follow docs/API-CONTRACTS.md: collections answer data + meta.next_cursor_url, single GET/PUT answer a data
/// wrapper (routings answer a bare object), POST answers a bare object with 201, DELETE answers 204 and the row is gone.
/// Ids, timestamps and the airline are fixed so that recorded bodies never vary between runs.
/// </summary>
public sealed class FakeRemote
{
    const string Prefix = FakeOperationsServer.OperationsPrefix;
    const string Stamp = "2026-01-01T00:00:00Z";
    public readonly FakeOperationsServer Server = new();
    public readonly Dictionary<string, JsonObject> Rows = new(StringComparer.Ordinal);
    /// <summary>Rows per page by collection path (default 100).</summary>
    public readonly Dictionary<string, int> PageSizes = new(StringComparer.Ordinal);
    /// <summary>Called before normal handling; return a response to override it, or throw to simulate a transport failure.</summary>
    public Func<RecordedRequest, FakeResponse?>? Intercept;
    int nextId = 90;
    readonly HashSet<string> registered = [];

    public FakeRemote()
    {
        foreach (var collection in new[] { "airports", "fleet", "routings", "routes", "parameters" })
            RegisterCollection(collection);
    }

    void RegisterCollection(string path)
    {
        if (!registered.Add("C " + path))
            return;
        Server.On(HttpMethod.Get, path, Handle).On(HttpMethod.Post, path, Handle);
    }

    void RegisterRow(string path)
    {
        if (!registered.Add("R " + path))
            return;
        Server.On(HttpMethod.Get, path, Handle).On(HttpMethod.Put, path, Handle).On(HttpMethod.Delete, path, Handle);
        var parts = path.Split('/');
        if (parts[0] == "fleet" && parts.Length == 2)
            RegisterCollection($"fleet/{parts[1]}/aircraft");
        if (parts[0] == "fleet" && parts.Length == 4)
            Server.On(HttpMethod.Patch, "aircraft/" + parts[3], Handle);
    }

    /// <summary>Add an existing remote row, e.g. <c>AddRow("airports/101", """{"id":101,...}""")</c>.</summary>
    public JsonObject AddRow(string path, string json)
    {
        var row = JsonNode.Parse(json)!.AsObject();
        Rows[path] = row;
        RegisterRow(path);
        return row;
    }

    FakeResponse Handle(RecordedRequest request)
    {
        if (Intercept?.Invoke(request) is { } forced)
            return forced;
        return Respond(request);
    }

    /// <summary>Normal behaviour, also usable from an interceptor that wants the server to apply a request and then fail.</summary>
    public FakeResponse Respond(RecordedRequest request)
    {
        var path = request.Path[Prefix.Length..];
        var query = ParseQuery(request.PathAndQuery);
        var collection = path is "airports" or "fleet" or "routings" or "routes" or "parameters"
            || (path.StartsWith("fleet/", StringComparison.Ordinal) && path.EndsWith("/aircraft", StringComparison.Ordinal));
        if (request.Method == "GET")
            return collection ? List(path, query) : Single(path);
        if (request.Method == "POST")
            return Create(path, JsonNode.Parse(request.Body!)!.AsObject());
        if (request.Method == "PUT")
        {
            if (!Rows.TryGetValue(path, out var row))
                return FakeResponse.WithStatus(404);
            foreach (var (key, value) in JsonNode.Parse(request.Body!)!.AsObject())
                row[key] = value?.DeepClone();
            row["updated_at"] = Stamp;
            return Wrap(path, row);
        }
        if (request.Method == "PATCH")
            return Move(path, JsonNode.Parse(request.Body!)!.AsObject());
        if (request.Method == "DELETE")
        {
            if (!Rows.Remove(path))
                return FakeResponse.WithStatus(404);
            return new FakeResponse(HttpStatusCode.NoContent, null);
        }
        throw new InvalidOperationException("Unexpected method " + request.Method);
    }

    static bool IsRoutings(string path) => path.StartsWith("routings/", StringComparison.Ordinal);
    static FakeResponse Wrap(string path, JsonObject row)
        => FakeResponse.Ok((IsRoutings(path) ? row.DeepClone() : new JsonObject { ["data"] = row.DeepClone() }).ToJsonString());

    FakeResponse Single(string path)
        => Rows.TryGetValue(path, out var row) ? Wrap(path, row) : FakeResponse.WithStatus(404);

    FakeResponse List(string path, Dictionary<string, string> query)
    {
        IEnumerable<JsonObject> rows;
        if (path == "parameters")
        {
            var fleets = Rows.Where(p => p.Key.StartsWith("fleet/", StringComparison.Ordinal) && p.Key.Count(c => c == '/') == 1)
                .Select(p => (JsonNode?)JsonValue.Create(p.Value["id"]!.GetValue<int>())).ToArray();
            rows = [new JsonObject { ["id"] = 1, ["airline_id"] = 7, ["icao_code"] = "CAO", ["iata_code"] = "CA", ["approved"] = true, ["restricted"] = false, ["fleet_ids"] = new JsonArray(fleets) }];
        }
        else
        {
            var depth = path.Count(c => c == '/') + 1;
            rows = Rows.Where(p => p.Key.StartsWith(path + "/", StringComparison.Ordinal) && p.Key.Count(c => c == '/') == depth)
                .OrderBy(p => p.Value["id"]!.GetValue<int>()).Select(p => p.Value);
            foreach (var (key, value) in query)
            {
                if (!key.StartsWith("filter[", StringComparison.Ordinal))
                    continue;
                var name = key["filter[".Length..^1];
                rows = name == "fleet_id"
                    ? rows.Where(r => r["fleet_ids"] is JsonArray ids && ids.Any(i => i!.ToString() == value))
                    : rows.Where(r => r[name]?.ToString() == value);
            }
        }
        var all = rows.ToList();
        var size = PageSizes.GetValueOrDefault(path, 100);
        var offset = query.TryGetValue("cursor", out var cursor) ? int.Parse(cursor) : 0;
        var page = all.Skip(offset).Take(size).Select(r => (JsonNode?)r.DeepClone()).ToArray();
        string? next = null;
        if (offset + size < all.Count)
        {
            var kept = string.Join('&', RawQuery(query));
            next = $"{Prefix}{path}?{(kept.Length > 0 ? kept + "&" : "")}cursor={offset + size}";
        }
        return FakeResponse.Ok(new JsonObject { ["data"] = new JsonArray(page), ["meta"] = new JsonObject { ["next_cursor_url"] = next } }.ToJsonString());
    }

    // Keep the original (escaped) filter and page size parameters on the next-page URL, as the real API does.
    static IEnumerable<string> RawQuery(Dictionary<string, string> query)
        => query.Where(p => p.Key != "cursor").Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value));

    static Dictionary<string, string> ParseQuery(string pathAndQuery)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        if (at < 0)
            return result;
        foreach (var pair in pathAndQuery[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            result[Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq])] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return result;
    }

    FakeResponse Create(string collection, JsonObject body)
    {
        var id = nextId++;
        var row = new JsonObject { ["id"] = id };
        string path;
        if (collection == "airports")
        {
            var code = body["icao_iata"]!.GetValue<string>();
            path = $"airports/{id}";
            row["airline_id"] = 7;
            row[code.Length == 4 ? "icao" : "iata"] = code;
        }
        else if (collection == "fleet")
        { path = $"fleet/{id}"; row["airline_id"] = 7; }
        else if (collection.StartsWith("fleet/", StringComparison.Ordinal))
        {
            var fleet = int.Parse(collection.Split('/')[1]);
            path = $"{collection}/{id}";
            row["airline_id"] = 7;
            row["fleet_id"] = fleet;
        }
        else if (collection == "routings")
        { path = $"routings/{id}"; row["airline_id"] = 7; }
        else if (collection == "routes")
            path = $"routes/{id}";
        else
            throw new InvalidOperationException("Cannot create in " + collection);
        foreach (var (key, value) in body)
        {
            if (key == "icao_iata")
                continue;
            // Requests carry parameter ids as strings; the API answers with numbers.
            row[key] = key == "parameter_ids" && value is JsonArray ids
                ? new JsonArray(ids.Select(v => (JsonNode?)JsonValue.Create(int.Parse(v!.ToString()))).ToArray())
                : value?.DeepClone();
        }
        // Live behaviour recorded in docs/API-CONTRACTS.md: a created jumpseat does not keep callsign / flight number or
        // fleets (fleets: 2026-11-12, route 14217362).
        if (collection == "routes" && body["type"]?.ToString() == "jumpseat")
        { row["callsign"] = null; row["flight_number"] = null; row["fleet_ids"] = null; }
        row["created_at"] = Stamp;
        row["updated_at"] = Stamp;
        Rows[path] = row;
        RegisterRow(path);
        return FakeResponse.WithStatus(201, row.ToJsonString());
    }

    FakeResponse Move(string path, JsonObject body)
    {
        var id = path["aircraft/".Length..];
        var old = Rows.Keys.SingleOrDefault(k => k.EndsWith("/aircraft/" + id, StringComparison.Ordinal));
        if (old == null)
            return FakeResponse.WithStatus(404);
        var row = Rows[old];
        Rows.Remove(old);
        row["fleet_id"] = body["fleet_id"]!.DeepClone();
        var moved = $"fleet/{body["fleet_id"]}/aircraft/{id}";
        Rows[moved] = row;
        RegisterRow(moved);
        return FakeResponse.Ok("{}");
    }

    /// <summary>
    /// Airports 101 EGLL/LHR (container 1), 102 EGKK/LGW, 103 EGCC/MAN (unused); fleets 4 and 5 (parameter 1), 6 spare;
    /// aircraft 8 in fleet 4; routing 9; scheduled routes 10 (fleet 4) and 12 (fleets 4,5 with times); jumpseat route 11.
    /// </summary>
    public void AddStandardData()
    {
        var r = this;
        r.AddRow("airports/101", """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"London","container_ids":[1]}""");
        r.AddRow("airports/102", """{"id":102,"airline_id":7,"icao":"EGKK","iata":"LGW","name":"Gatwick","container_ids":[]}""");
        r.AddRow("airports/103", """{"id":103,"airline_id":7,"icao":"EGCC","iata":"MAN","name":"Manchester"}""");
        r.AddRow("fleet/4", """{"id":4,"airline_id":7,"name":"Fleet 4","code":"B738","type":"cargo","max_cargo":10000,"parameter_ids":[1],"updated_at":"2026-01-01T00:00:00Z"}""");
        r.AddRow("fleet/5", """{"id":5,"airline_id":7,"name":"Fleet 5","code":"B738","type":"cargo","max_cargo":10000,"parameter_ids":[1],"updated_at":"2026-01-01T00:00:00Z"}""");
        r.AddRow("fleet/6", """{"id":6,"airline_id":7,"name":"Fleet 6 spare","code":"A320","type":"pax","max_pax":180,"parameter_ids":[1],"updated_at":"2026-01-01T00:00:00Z"}""");
        r.AddRow("fleet/4/aircraft/8", """{"id":8,"airline_id":7,"fleet_id":4,"registration":"B-TEST","name":"Plane","cargo":1000}""");
        r.AddRow("routings/9", """{"id":9,"airline_id":7,"departure_airport_id":101,"arrival_airport_id":102,"route":"DCT","remarks":"original","tag":["a"]}""");
        r.AddRow("routes/10", """{"id":10,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO100","flight_number":"CA100","fleet_ids":[4],"hidden":true,"remarks":"original"}""");
        r.AddRow("routes/11", """{"id":11,"departure_id":101,"arrival_id":102,"type":"jumpseat","callsign":null,"flight_number":null,"remarks":"original jumpseat","hidden":false}""");
        r.AddRow("routes/12", """{"id":12,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO120","flight_number":"CA120","fleet_ids":[4,5],"departure_time":"23:59:37","arrival_time":"01:20:15","hidden":false}""");
    }
}
