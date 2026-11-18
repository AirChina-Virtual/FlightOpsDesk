using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Infrastructure;

// 5.1 (improvement plan): the read-only checks behind E4, E8, G7 and E10, against the real vAMSYS Operations API.
// Only GET requests through the product transport (rate budget, origin policy, retrying reads), plus OAuth token
// requests; a guard handler refuses anything else before it leaves the machine. The credentials come from
// VAMSYS_CLIENT_ID / VAMSYS_CLIENT_SECRET or are typed at the prompt; they and the tokens are never written out.
// The report (IDs, counts, the sample values probed, statuses) goes to the JSON file given and to the console.
public static class ReadOnlyVerification
{
    // Meta: what the page said about paging (per_page, the meta and links fields present, the kind of each cursor field).
    sealed record Page(int Requested, int Returned, bool NextKeepsSize, string Meta);
    sealed record Read(string Resource, int Size, List<long> Ids, List<Page> Pages, string? Error)
    {
        public List<JsonElement> Rows { get; } = [];
    }

    public static async Task<int> RunAsync(string reportPath, HttpMessageHandler? inner = null, Func<(string Id, string Secret)>? credentials = null, Uri? origin = null,
        RequestCoordinator? coordinator = null, TextWriter? output = null)
    {
        var (clientId, secret) = (credentials ?? Prompt)();
        origin ??= OperationsEndpoints.Origin;
        var api = new Uri(origin, "api/v3/operations/");
        var tokenUri = new Uri(origin, "oauth/token");
        using var owned = inner is null ? new HttpClientHandler { AllowAutoRedirect = false } : null;
        using var guard = new GetOnly(inner ?? owned!, tokenUri);
        using var client = new HttpClient(guard, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FlightOpsDesk-readonly-check");
        var report = new JsonObject { ["startedUtc"] = DateTime.UtcNow.ToString("O"), ["origin"] = origin.AbsoluteUri };

        // E10 first: which scope requests the server accepts, and whether a token without "*" can read.
        var scopes = new JsonArray();
        foreach (var scope in new string?[] { "*", null, "" })
            scopes.Add(await ScopeAsync(client, tokenUri, api, clientId, secret, scope, coordinator));
        report["E10_scopes"] = scopes;

        var transport = new OperationsTransport(client, api, "operations-shared", Tokens(client, tokenUri, clientId, secret, "*"), coordinator);

        // E4: every list read at 100 and at 500 rows a page; the routes (the longest list) also at 1000 to find a hidden cap.
        var reads = new JsonArray();
        var full = new Dictionary<string, Read>(StringComparer.Ordinal);
        foreach (var resource in new[] { "airports", "fleet", "routings", "routes" })
            foreach (var size in resource == "routes" ? new[] { 100, 500, 1000 } : new[] { 100, 500 })
            {
                var read = await ReadAllAsync(transport, api, resource, size);
                reads.Add(Describe(read));
                full[$"{resource}@{size}"] = read;
            }
        report["E4_reads"] = reads;
        report["E4_compare"] = Compare(full, "airports", "fleet", "routings", "routes");

        // Aircraft are read per fleet: every fleet at 500 (the counts), then the largest fleet again at 100 to compare.
        var fleetRows = full["fleet@500"].Rows;
        var aircraft = new Dictionary<long, List<JsonElement>>();
        foreach (var fleet in fleetRows)
        {
            var id = fleet.GetProperty("id").GetInt64();
            aircraft[id] = (await ReadAllAsync(transport, api, $"fleet/{id}/aircraft", 500)).Rows;
        }
        var largest = aircraft.OrderByDescending(p => p.Value.Count).FirstOrDefault();
        if (largest.Value is { Count: > 0 })
        {
            var small = await ReadAllAsync(transport, api, $"fleet/{largest.Key}/aircraft", 100);
            var big = await ReadAllAsync(transport, api, $"fleet/{largest.Key}/aircraft", 500);
            report["E4_aircraft"] = new JsonObject
            {
                ["fleets"] = aircraft.Count,
                ["aircraft"] = aircraft.Values.Sum(v => v.Count),
                ["largestFleet"] = largest.Key,
                ["at100"] = Describe(small),
                ["at500"] = Describe(big),
                ["sameIds"] = small.Ids.Order().SequenceEqual(big.Ids.Order())
            };
        }

        // E8: how filter[icao], filter[iata] (airports) and filter[name] (fleets) match: exact, other case, part of the value.
        var airports = full["airports@500"].Rows;
        var probes = new JsonArray();
        if (airports.FirstOrDefault(a => Text(a, "icao").Length == 4 && Text(a, "iata").Length == 3) is { ValueKind: JsonValueKind.Object } airport)
        {
            var id = airport.GetProperty("id").GetInt64();
            var icao = Text(airport, "icao");
            var iata = Text(airport, "iata");
            probes.Add(await ProbeAsync(transport, api, "airports", "icao", icao, id, airports, "icao"));
            probes.Add(await ProbeAsync(transport, api, "airports", "icao", icao.ToLowerInvariant(), id, airports, "icao"));
            probes.Add(await ProbeAsync(transport, api, "airports", "icao", icao[..3], id, airports, "icao"));
            probes.Add(await ProbeAsync(transport, api, "airports", "iata", iata, id, airports, "iata"));
            probes.Add(await ProbeAsync(transport, api, "airports", "iata", iata.ToLowerInvariant(), id, airports, "iata"));
            probes.Add(await ProbeAsync(transport, api, "airports", "iata", iata[..2], id, airports, "iata"));
        }
        if (fleetRows.FirstOrDefault(f => Text(f, "name").Length >= 4) is { ValueKind: JsonValueKind.Object } fleetRow)
        {
            var id = fleetRow.GetProperty("id").GetInt64();
            var name = Text(fleetRow, "name");
            probes.Add(await ProbeAsync(transport, api, "fleet", "name", name, id, fleetRows, "name"));
            probes.Add(await ProbeAsync(transport, api, "fleet", "name", Swap(name), id, fleetRows, "name"));
            probes.Add(await ProbeAsync(transport, api, "fleet", "name", name[1..^1], id, fleetRows, "name"));
        }
        report["E8_filters"] = probes;

        // G7: filter[registration] inside the aircraft's own fleet (exact, other case, part), and in another fleet.
        var g7 = new JsonArray();
        if (largest.Value is { Count: > 0 } rows && rows.FirstOrDefault(r => Text(r, "registration").Length >= 4) is { ValueKind: JsonValueKind.Object } plane)
        {
            var id = plane.GetProperty("id").GetInt64();
            var registration = Text(plane, "registration");
            var path = $"fleet/{largest.Key}/aircraft";
            g7.Add(await ProbeAsync(transport, api, path, "registration", registration, id, rows, "registration"));
            g7.Add(await ProbeAsync(transport, api, path, "registration", Swap(registration), id, rows, "registration"));
            g7.Add(await ProbeAsync(transport, api, path, "registration", registration[1..^1], id, rows, "registration"));
            if (aircraft.Keys.FirstOrDefault(k => k != largest.Key) is var other && other != 0)
                g7.Add(await ProbeAsync(transport, api, $"fleet/{other}/aircraft", "registration", registration, id, aircraft[other], "registration"));
        }
        report["G7_registration"] = g7;

        report["finishedUtc"] = DateTime.UtcNow.ToString("O");
        report["requests"] = guard.Sent;
        var json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, json);
        await (output ?? Console.Out).WriteLineAsync(json);
        return 0;
    }

    internal static (string, string) Prompt()
    {
        var id = Environment.GetEnvironmentVariable("VAMSYS_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable("VAMSYS_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(id))
        {
            Console.Write("Client ID: ");
            id = Console.ReadLine()?.Trim();
        }
        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Write("Client secret (not shown): ");
            var typed = new System.Text.StringBuilder();
            for (var key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
                if (key.Key == ConsoleKey.Backspace) { if (typed.Length > 0) typed.Length--; }
                else if (!char.IsControl(key.KeyChar)) typed.Append(key.KeyChar);
            Console.WriteLine();
            secret = typed.ToString();
        }
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Client ID and secret are required.");
        return (id, secret);
    }

    internal static TokenProvider Tokens(HttpClient client, Uri tokenUri, string clientId, string secret, string? scope) => new(async ct =>
    {
        var (status, token, _) = await RequestTokenAsync(client, tokenUri, clientId, secret, scope, ct);
        return token ?? throw new InvalidOperationException($"Token request with scope '{scope}' failed: HTTP {(int)status}.");
    });

    static async Task<(HttpStatusCode Status, AccessToken? Token, string? Error)> RequestTokenAsync(HttpClient client, Uri tokenUri, string clientId, string secret, string? scope, CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret };
        if (scope is not null)
            form["scope"] = scope;
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new("application/json"));
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (response.IsSuccessStatusCode && root.TryGetProperty("access_token", out var token))
                return (response.StatusCode, new AccessToken(token.GetString()!, DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt64())), null);
            // Only the OAuth error code (e.g. invalid_scope) is kept: never the body, which could echo what was sent.
            return (response.StatusCode, null, root.TryGetProperty("error", out var error) ? error.GetString() : null);
        }
        catch (JsonException)
        {
            return (response.StatusCode, null, "non-JSON response");
        }
    }

    static async Task<JsonObject> ScopeAsync(HttpClient client, Uri tokenUri, Uri api, string clientId, string secret, string? scope, RequestCoordinator? coordinator)
    {
        var result = new JsonObject { ["scope"] = scope is null ? "(omitted)" : scope == "" ? "(empty)" : scope };
        var (status, token, error) = await RequestTokenAsync(client, tokenUri, clientId, secret, scope, CancellationToken.None);
        result["tokenStatus"] = (int)status;
        result["tokenError"] = error;
        if (token is null)
            return result;
        var transport = new OperationsTransport(client, api, "operations-shared", new TokenProvider(_ => Task.FromResult(token)), coordinator);
        try
        {
            using var page = await transport.GetAsync(new Uri(api, "airports?page%5Bsize%5D=1"), CancellationToken.None);
            result["readStatus"] = 200;
        }
        catch (ApiResponseException e)
        {
            result["readStatus"] = (int)e.Status;
        }
        return result;
    }

    static async Task<Read> ReadAllAsync(OperationsTransport transport, Uri api, string resource, int size)
    {
        var ids = new List<long>();
        var pages = new List<Page>();
        var rows = new List<JsonElement>();
        Uri? next = new(api, $"{resource}?page%5Bsize%5D={size}");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (next is not null && visited.Add(next.AbsoluteUri))
            {
                using var json = await transport.GetAsync(next, CancellationToken.None);
                var data = json.RootElement.GetProperty("data");
                foreach (var row in data.EnumerateArray())
                {
                    ids.Add(row.GetProperty("id").GetInt64());
                    rows.Add(row.Clone());
                }
                next = null;
                var keeps = false;
                var described = DescribeMeta(json.RootElement);
                if (json.RootElement.TryGetProperty("meta", out var meta)
                    && (meta.TryGetProperty("next_cursor_url", out var cursor) && cursor.ValueKind == JsonValueKind.String
                        || meta.TryGetProperty("next_page_url", out cursor))
                    && cursor.ValueKind == JsonValueKind.String && cursor.GetString() is { Length: > 0 } link)
                {
                    next = new Uri(api, link);
                    if (next.Host != api.Host)
                        throw new InvalidOperationException("Cursor outside the API host: " + next.Host);
                    keeps = Uri.UnescapeDataString(next.Query).Contains("page[size]", StringComparison.Ordinal);
                    if (!keeps)
                        next = new UriBuilder(next) { Query = (next.Query.Length > 1 ? next.Query[1..] + "&" : "") + "page%5Bsize%5D=" + size }.Uri;
                }
                pages.Add(new(size, data.GetArrayLength(), keeps, described));
            }
            return Filled(new(resource, size, ids, pages, null), rows);
        }
        catch (ApiResponseException e)
        {
            return Filled(new(resource, size, ids, pages, $"HTTP {(int)e.Status}"), rows);
        }
    }

    static Read Filled(Read read, List<JsonElement> rows)
    {
        read.Rows.AddRange(rows);
        return read;
    }

    static JsonObject Describe(Read read) => new()
    {
        ["resource"] = read.Resource,
        ["pageSize"] = read.Size,
        ["rows"] = read.Ids.Count,
        ["distinct"] = read.Ids.Distinct().Count(),
        ["pages"] = read.Pages.Count,
        ["pageLengths"] = new JsonArray(read.Pages.Select(p => (JsonNode)p.Returned).ToArray()),
        ["firstPageMeta"] = read.Pages.FirstOrDefault()?.Meta,
        ["lastPageMeta"] = read.Pages.Count > 1 ? read.Pages[^1].Meta : null,
        ["cursorKeepsPageSize"] = read.Pages.Count > 1 ? read.Pages.Take(read.Pages.Count - 1).All(p => p.NextKeepsSize) : null,
        ["error"] = read.Error
    };

    static JsonArray Compare(Dictionary<string, Read> reads, params string[] resources)
    {
        var result = new JsonArray();
        foreach (var resource in resources)
        {
            var baseline = reads[$"{resource}@100"].Ids.ToHashSet();
            foreach (var (key, read) in reads.Where(p => p.Key.StartsWith(resource + "@", StringComparison.Ordinal) && p.Key != $"{resource}@100"))
                result.Add(new JsonObject
                {
                    ["resource"] = resource,
                    ["pageSize"] = read.Size,
                    ["sameIdsAs100"] = baseline.SetEquals(read.Ids) && read.Ids.Count == read.Ids.Distinct().Count(),
                    ["missing"] = baseline.Except(read.Ids).Count(),
                    ["extra"] = read.Ids.Except(baseline).Count(),
                    ["duplicates"] = read.Ids.Count - read.Ids.Distinct().Count()
                });
        }
        return result;
    }

    // One filtered GET; what came back compared with the full list: which rows hold the value exactly, in another case, or as a part.
    static async Task<JsonObject> ProbeAsync(OperationsTransport transport, Uri api, string path, string filter, string value, long sample, List<JsonElement> all, string field)
    {
        var uri = new Uri(api, $"{path}?page%5Bsize%5D=100&filter%5B{filter}%5D={Uri.EscapeDataString(value)}");
        var result = new JsonObject { ["path"] = path, ["filter"] = filter, ["value"] = value };
        try
        {
            using var json = await transport.GetAsync(uri, CancellationToken.None);
            var ids = json.RootElement.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToList();
            result["status"] = 200;
            result["returned"] = ids.Count;
            result["containsSample"] = ids.Contains(sample);
            result["exactMatchesInList"] = all.Count(r => Text(r, field) == value);
            result["caseInsensitiveMatchesInList"] = all.Count(r => string.Equals(Text(r, field), value, StringComparison.OrdinalIgnoreCase));
            result["containsMatchesInList"] = all.Count(r => Text(r, field).Contains(value, StringComparison.OrdinalIgnoreCase));
        }
        catch (ApiResponseException e)
        {
            result["status"] = (int)e.Status;
        }
        return result;
    }

    // Names and kinds only (cursor values are opaque and not kept): "per_page=100; meta: next_cursor_url=string, ...; links: ...".
    static string DescribeMeta(JsonElement root)
    {
        static string Kinds(JsonElement node) => node.ValueKind == JsonValueKind.Object
            ? string.Join(", ", node.EnumerateObject().Select(p => $"{p.Name}={(p.Value.ValueKind is JsonValueKind.Number ? p.Value.GetRawText() : p.Value.ValueKind.ToString().ToLowerInvariant())}"))
            : node.ValueKind.ToString().ToLowerInvariant();
        var parts = new List<string> { "top: " + string.Join(", ", root.EnumerateObject().Select(p => p.Name)) };
        if (root.TryGetProperty("meta", out var meta))
            parts.Add("meta: " + Kinds(meta));
        if (root.TryGetProperty("links", out var links))
            parts.Add("links: " + Kinds(links));
        return string.Join("; ", parts);
    }

    static string Text(JsonElement row, string field) => row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    static string Swap(string text) => text.Any(char.IsUpper) ? text.ToLowerInvariant() : text.ToUpperInvariant();

    // Refuses every request but GET, and POST to the token endpoint, before it is sent.
    sealed class GetOnly(HttpMessageHandler inner, Uri tokenUri) : DelegatingHandler(inner)
    {
        public int Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var allowed = request.Method == HttpMethod.Get || (request.Method == HttpMethod.Post && request.RequestUri == tokenUri);
            if (!allowed)
                throw new InvalidOperationException($"Read-only check refused {request.Method} {request.RequestUri}");
            Sent++;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
