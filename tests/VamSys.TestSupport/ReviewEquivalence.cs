using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.TestSupport;

// Writes everything a review decides for fixed workspaces (row IDs derived from their position, so two builds of the
// program see the same rows): the content key, each entry's handling and reason, the issues, the validation issues,
// the submission items as saved (planner IDs replaced by the row they belong to) and the requests each submitted item
// would send. Run with the program before and after a change and compare the files: they must be identical.
// The unit tests pin the hashes of the small cases, as the program before 2026-11-23 (6292ec1) computed them; the
// benchmark entry --review-equivalence writes all cases, large ones included, for comparing two builds.
// (2026-11-23, stage 7 item 2: the review plan copies less.)
public static class ReviewEquivalence
{
    const string Va = "eq-va";

    static Guid Id(ResourceKind kind, int n) => new((int)kind * 1_000_000 + n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    static DataRow Row(ResourceKind kind, int n, bool identity, params (string Field, string Value)[] fields)
    {
        var builder = new DataRow.Builder();
        foreach (var (field, value) in fields) builder[field] = value;
        var row = builder.ToRow().WithLocalId(Id(kind, n));
        return identity ? row.WithIdentity(new ResourceIdentity((1000 + n).ToString(), Va)) : row;
    }

    // Snapshot rows (identified when online) that are also the draft rows, so unchanged rows are shared as when read.
    static void Seed(Workspace w, ResourceKind kind, IEnumerable<DataRow> rows)
    {
        var data = w.Resources[kind];
        foreach (var row in rows) { data.Snapshot.Add(row); data.Draft.Add(row); }
    }
    static void Edit(Workspace w, ResourceKind kind, int n, params (string Field, string Value)[] fields)
    {
        var data = w.Resources[kind];
        var at = data.Draft.FindIndex(r => r.LocalId == Id(kind, n));
        data.Draft[at] = data.Draft[at].With(fields.Select(f => new KeyValuePair<string, string>(f.Field, f.Value)));
    }
    static void Create(Workspace w, ResourceKind kind, int n, params (string Field, string Value)[] fields)
        => w.Resources[kind].Draft.Add(Row(kind, n, false, fields));

    static (string, string)[] Route(int n, string type = "scheduled", string fleets = "1") =>
    [
        ("ID", n.ToString()), ("Departure Airport (ICAO/IATA)", "ZBAA"), ("Arrival Airport (ICAO/IATA)", "ZSPD"), ("Type", type),
        ("Callsign", "EQ" + n.ToString("D4")), ("Flight Number", n.ToString("D4")), ("Fleet IDs", type == "jumpseat" ? "" : fleets),
        ("Start Date", "2026-10-01 00:00:00"), ("End Date", "2027-10-01 00:00:00"), ("Departure Time (HH:MM)", "10:00"),
        ("Arrival Time (HH:MM)", "12:00"), ("Remarks", "row " + n), ("Tags", "eq"), ("Is Hidden", "FALSE"), ("Altitude", "33000"),
        ("Cost Index", "AUTO"), ("_delete", "FALSE")
    ];

    // Every rule the review and validation have: creates without a key, duplicate keys, keys that need trimming and upper
    // case, references to local rows (dependencies), lists of references with spaces, unknown references, deletes, a
    // conflict, an open online task item, sampling of unverified operations and jumpseat signatures.
    public static Workspace Mixed(RunMode mode)
    {
        var w = new Workspace { Id = new Guid(7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), Name = "mixed", Mode = mode, AirlineId = Va };
        var online = mode == RunMode.Online;
        Seed(w, ResourceKind.Airports, [Row(ResourceKind.Airports, 1, online, ("ICAO/IATA", "ZBAA"), ("Name", "Beijing")),
            Row(ResourceKind.Airports, 2, online, ("ICAO/IATA", " zspd "), ("Name", "Shanghai"))]);
        Edit(w, ResourceKind.Airports, 2, ("Name", "Shanghai Pudong"));
        Create(w, ResourceKind.Airports, 3, ("ICAO/IATA", "ZGGG"), ("Name", "Guangzhou"));
        Create(w, ResourceKind.Airports, 4, ("ICAO/IATA", ""), ("Name", "No code"));
        Create(w, ResourceKind.Airports, 5, ("ICAO/IATA", "zbaa"), ("Name", "Duplicate"));
        Seed(w, ResourceKind.Fleets, [Row(ResourceKind.Fleets, 1, online, ("ID", "1"), ("Name", "Boeing 737-800"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189")),
            Row(ResourceKind.Fleets, 3, online, ("ID", "3"), ("Name", "Old"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "150"))]);
        Create(w, ResourceKind.Fleets, 2, ("ID", ""), ("Name", "New fleet"), ("Type Code", "A321"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "200"));
        Edit(w, ResourceKind.Fleets, 3, ("_delete", "TRUE"));
        var localFleet = LocalReference.Format(Id(ResourceKind.Fleets, 2));
        Seed(w, ResourceKind.Aircraft, [Row(ResourceKind.Aircraft, 1, online, ("ID", "11"), ("Registration", "B-1234"), ("Name", "One"), ("Fleet ID", "1"))]);
        Edit(w, ResourceKind.Aircraft, 1, ("Name", "One renamed"));
        Create(w, ResourceKind.Aircraft, 2, ("ID", ""), ("Registration", "B-5678"), ("Name", "Two"), ("Fleet ID", localFleet));
        Create(w, ResourceKind.Aircraft, 3, ("ID", ""), ("Registration", "B-9999"), ("Name", "Three"), ("Fleet ID", "3"));
        Seed(w, ResourceKind.Routes, Enumerable.Range(1, 12).Select(n => Row(ResourceKind.Routes, n, online, Route(n, n % 3 == 0 ? "jumpseat" : "scheduled"))));
        for (var n = 1; n <= 6; n++)
            Edit(w, ResourceKind.Routes, n, ("Remarks", "edited " + n));
        Edit(w, ResourceKind.Routes, 7, ("Fleet IDs", " 1 , 2,1 "));
        Edit(w, ResourceKind.Routes, 8, ("Fleet IDs", "99"));
        Edit(w, ResourceKind.Routes, 9, ("Fleet IDs", "1"));
        Edit(w, ResourceKind.Routes, 10, ("_delete", "TRUE"));
        Edit(w, ResourceKind.Routes, 11, ("Fleet IDs", "1," + localFleet), ("Type", "charter"));
        Edit(w, ResourceKind.Routes, 12, ("Callsign", "X"));
        Create(w, ResourceKind.Routes, 13, [.. Route(13, "jumpseat").Select(p => p.Item1 == "ID" ? (p.Item1, "") : p)]);
        Create(w, ResourceKind.Routes, 14, [.. Route(14, "jumpseat").Select(p => p.Item1 == "ID" ? (p.Item1, "") : p)]);
        Create(w, ResourceKind.Routes, 15, [.. Route(15).Select(p => p.Item1 == "ID" ? (p.Item1, "") : p)]);
        Create(w, ResourceKind.Routes, 16, [.. Route(16, fleets: localFleet).Select(p => p.Item1 == "ID" ? (p.Item1, "") : p)]);
        Create(w, ResourceKind.Routes, 17, [.. Route(17).Select(p => p.Item1 == "ID" ? (p.Item1, "2") : p)]);
        w.Resources[ResourceKind.Routes].Conflicts.Add(new(Id(ResourceKind.Routes, 5), "Remarks", "row 5", "edited 5", "remote 5"));
        var open = w.Resources[ResourceKind.Routes].Draft.First(r => r.LocalId == Id(ResourceKind.Routes, 6));
        w.Jobs.Add(new BatchJob { Id = new Guid(8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), Mode = RunMode.Online, Items = [new ChangeItem { Id = new Guid(9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), Resource = ResourceKind.Routes, Kind = ChangeKind.Update, After = open, Before = open, State = ItemState.Pending }] });
        return w;
    }

    // The window baseline's workspace (all jumpseat routes with every field), with the first routes' Remarks edited.
    public static Workspace Large(RunMode mode, int rows, int changes)
    {
        var w = new Workspace { Id = new Guid(6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), Name = "large", Mode = mode, AirlineId = Va };
        var online = mode == RunMode.Online;
        Seed(w, ResourceKind.Fleets, [Row(ResourceKind.Fleets, 1, online, ("ID", "1"), ("Name", "Boeing 737-800"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"))]);
        Seed(w, ResourceKind.Airports, [Row(ResourceKind.Airports, 1, online, ("ICAO/IATA", "ZBAA"), ("Name", "Beijing")), Row(ResourceKind.Airports, 2, online, ("ICAO/IATA", "ZSPD"), ("Name", "Shanghai"))]);
        var full = Schemas.All[ResourceKind.Routes].Columns.ToArray();
        Seed(w, ResourceKind.Routes, Enumerable.Range(0, rows).Select(n => Row(ResourceKind.Routes, n, online,
            [.. full.Select(f => (f, "")).Where(p => !Route(n, "jumpseat").Any(r => r.Item1 == p.f)), .. Route(n, "jumpseat")])));
        var data = w.Resources[ResourceKind.Routes];
        for (var n = 0; n < changes; n++)
            data.Draft[n] = data.Draft[n].With("Remarks", "review " + n);
        return w;
    }

    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A review sent a request: " + request.RequestUri);
    }
    // One client for every review here; it never sends, so it never needs disposing.
    static readonly HttpClient NoRequests = new(new NoNetwork());

    public static JsonNode Describe(Workspace w, bool connected, bool detail)
    {
        var mode = w.Mode;
        var tokens = new TokenProvider(_ => Task.FromResult(new AccessToken("eq", DateTimeOffset.UtcNow.AddHours(1))), TimeProvider.System);
        var api = new OperationsAdapter(new OperationsTransport(NoRequests, OperationsAdapter.BaseUri, Va, tokens, InstantTime.Requests()), w);
        Action<ChangeItem>? preview = connected ? c => api.PreviewPlan(c) : null;
        var verified = new HashSet<string>(StringComparer.Ordinal) { "Airports:Update", "Fleets:Create" };
        var plan = ReviewPlanBuilder.BuildForWorkspace(new ChangePlanner(), w, mode, connected, verified, preview);
        var rowOf = plan.Entries.ToDictionary(e => e.Planned.Id, e => $"{e.Planned.Resource}:{e.Planned.After.LocalId}");
        string Ref(Guid id) => rowOf.TryGetValue(id, out var row) ? row : "missing:" + id;
        var entries = new JsonArray();
        foreach (var e in plan.Entries)
        {
            var p = e.Planned;
            var fields = new JsonArray();
            foreach (var f in p.Fields()) fields.Add($"{f.Key}|{f.Value.Intent}|{f.Value.Value}");
            entries.Add(new JsonObject
            {
                ["row"] = $"{p.Resource}:{p.Kind}:{p.After.LocalId}", ["before"] = p.Before?.LocalId.ToString(), ["disposition"] = e.Disposition.ToString(),
                ["reason"] = e.Reason is null ? null : JsonSerializer.Serialize(e.Reason), ["fields"] = fields,
                ["after"] = JsonSerializer.Serialize(p.After.Fields), ["airlineParameter"] = p.RequiresAirlineParameter,
                ["dependencies"] = new JsonArray([.. p.DependencyIds.Select(Ref).Order(StringComparer.Ordinal).Select(d => (JsonNode)d)])
            });
        }
        var items = new JsonArray();
        var requests = new JsonArray();
        foreach (var item in plan.SubmitItems)
        {
            var saved = JsonSerializer.SerializeToNode(item)!.AsObject();
            saved["Id"] = Ref(item.Id);
            saved["Dependencies"] = new JsonArray([.. item.Dependencies.Select(Ref).Select(d => (JsonNode)d)]);
            items.Add(saved);
            if (!connected) continue;
            try { requests.Add(JsonSerializer.Serialize(api.PreviewPlan(item).Select(s => new { Method = s.Method.Method, s.Path, s.Body }))); }
            catch (Exception error) { requests.Add("blocked: " + JsonSerializer.Serialize(MessageErrors.Describe(error))); }
        }
        var validation = new JsonObject();
        var pass = new ValidationPass(w);
        foreach (var kind in Enum.GetValues<ResourceKind>())
            validation[kind.ToString()] = JsonSerializer.Serialize(Schemas.Validate(w, kind, pass).Select(i => new { i.RowId, i.Field, Description = JsonSerializer.Serialize(i.Description) }));
        var chosen = new HashSet<ResourceKind> { ResourceKind.Routes, ResourceKind.Aircraft };
        var result = new JsonObject
        {
            ["mode"] = mode.ToString(), ["connected"] = connected, ["contentKey"] = plan.ContentKey(), ["submitCount"] = plan.SubmitCount,
            ["blockers"] = JsonSerializer.Serialize(plan.Blockers), ["issues"] = JsonSerializer.Serialize(plan.Issues),
            ["submitFor"] = new JsonArray([.. plan.SubmitItemsFor(chosen).Select(i => (JsonNode)Ref(i.Id))]),
        };
        if (detail)
        { result["entries"] = entries; result["items"] = items; result["requests"] = requests; result["validation"] = validation; }
        else
        {
            result["entriesSha256"] = Hash(entries); result["itemsSha256"] = Hash(items); result["requestsSha256"] = Hash(requests); result["validationSha256"] = Hash(validation);
            result["entryCount"] = entries.Count;
        }
        return result;
    }

    public static string Hash(JsonNode node) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString())));

    static readonly (RunMode Mode, bool Connected)[] MixedReviews = [(RunMode.Demo, false), (RunMode.Online, true), (RunMode.Online, false), (RunMode.Offline, false)];

    /// <summary>The cases the unit tests pin: the mixed workspace in every mode, and a small baseline workspace.</summary>
    public static IEnumerable<(string Name, Func<JsonNode> Describe)> SmallCases()
    {
        foreach (var (mode, connected) in MixedReviews)
            yield return ($"mixed {mode}{(connected ? " connected" : "")}", () => Describe(Mixed(mode), connected, true));
        foreach (var (mode, connected) in new[] { (RunMode.Demo, false), (RunMode.Online, true) })
            foreach (var changes in new[] { 0, 50, 500 })
                yield return ($"large 500 rows {changes} changes {mode}", () => Describe(Large(mode, 500, changes), connected, true));
    }

    public static void Run(string path)
    {
        var cases = new JsonArray();
        foreach (var (name, describe) in SmallCases())
        {
            var review = describe();
            cases.Add(new JsonObject { ["workspace"] = name, ["sha256"] = Hash(review), ["review"] = review });
        }
        foreach (var (mode, connected) in new[] { (RunMode.Demo, false), (RunMode.Online, true) })
            foreach (var changes in new[] { 0, 1000, 20000 })
            {
                var review = Describe(Large(mode, 20000, changes), connected, false);
                cases.Add(new JsonObject { ["workspace"] = $"large 20000 rows, {changes} changes, {mode}", ["sha256"] = Hash(review), ["review"] = review });
            }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, new JsonObject { ["cases"] = cases }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (var c in cases)
            Console.WriteLine($"{c!["sha256"]}  {c["workspace"]}");
        Console.WriteLine("Review equivalence: " + path);
    }
}
