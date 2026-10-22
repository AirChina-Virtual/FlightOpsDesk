using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.App;
using VamSys.Core;

namespace VamSys.UnitTests.Review;

/// <summary>
/// A small deterministic workspace with every kind of change the review has to treat: creates, updates and deletes of
/// several resources, local references (one resolved to a key, two creating dependencies), more same-type changes than the
/// Online sample allows and an unchanged row. Row identities are fixed, so two builds of the fixture have the same content key.
/// </summary>
static class ReviewPlanFixture
{
    public static readonly ChangePlanner Planner = new();

    public static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public static int Number(Guid id) => BitConverter.ToInt32(id.ToByteArray(), 0);

    public static DataRow Row(int n, params (string Field, string Value)[] fields)
    {
        var row = new DataRow { LocalId = Id(n) };
        foreach (var (field, value) in fields)
            row.Fields[field] = value;
        return row;
    }
    public static DataRow With(DataRow row, params (string Field, string Value)[] fields)
    {
        var copy = row.Copy();
        foreach (var (field, value) in fields)
            copy.Fields[field] = value;
        return copy;
    }
    public static string Token(int n) => LocalReference.Format(Id(n));

    static void Put(Workspace w, ResourceKind kind, IEnumerable<DataRow> snapshot, IEnumerable<DataRow> draft)
    {
        var data = w.Resources[kind];
        data.Snapshot = [.. snapshot];
        data.Draft = [.. draft];
    }

    public static Workspace Mixed(RunMode mode = RunMode.Online)
    {
        var w = new Workspace { Name = "review", Mode = mode, AirlineId = "7" };

        var zbaa = Row(1, ("ICAO/IATA", "ZBAA"), ("Name", "Beijing"), ("_delete", "FALSE"));
        var zspd = Row(2, ("ICAO/IATA", "ZSPD"), ("Name", "Shanghai"), ("_delete", "FALSE"));
        var zggg = Row(3, ("ICAO/IATA", "ZGGG"), ("Name", "Guangzhou"), ("_delete", "FALSE"));
        Put(w, ResourceKind.Airports, [zbaa, zspd, zggg],
            [With(zbaa, ("Name", "Beijing Capital")), With(zspd, ("_delete", "TRUE")), zggg.Copy(), Row(4, ("ICAO/IATA", "ZUUU"), ("Name", "Chengdu"), ("_delete", "FALSE"))]);

        var alpha = Row(10, ("ID", "1"), ("Name", "Alpha"), ("Type (pax/cargo/...)", "pax"), ("_delete", "FALSE"));
        Put(w, ResourceKind.Fleets, [alpha],
        [
            alpha.Copy(),
            Row(11, ("Name", "Beta 1"), ("Type (pax/cargo/...)", "pax"), ("_delete", "FALSE")),
            Row(12, ("Name", "Beta 2"), ("Type (pax/cargo/...)", "pax"), ("_delete", "FALSE")),
            Row(13, ("Name", "Beta 3"), ("Type (pax/cargo/...)", "pax"), ("_delete", "FALSE")),
            Row(14, ("Name", "Beta 4"), ("Type (pax/cargo/...)", "pax"), ("_delete", "FALSE"))
        ]);

        var ac1 = Row(20, ("Registration", "B-0001"), ("Fleet ID", "1"), ("ID", "501"), ("_delete", "FALSE"));
        var ac2 = Row(21, ("Registration", "B-0002"), ("Fleet ID", "1"), ("ID", "502"), ("_delete", "FALSE"));
        Put(w, ResourceKind.Aircraft, [ac1, ac2], [With(ac1, ("Fleet ID", "2")), With(ac2, ("Name", "renamed"))]);

        DataRow Route(int n, string fleets) => Row(n, ("ID", (900 + n).ToString()), ("Callsign", "QA" + n.ToString("D4")), ("Flight Number", n.ToString("D4")), ("Type", "scheduled"),
            ("Remarks", "r" + n), ("Altitude", "33000"), ("Fleet IDs", fleets), ("_delete", "FALSE"));
        var routes = Enumerable.Range(30, 6).Select(n => Route(n, "1")).ToArray();
        Put(w, ResourceKind.Routes, routes,
        [
            With(routes[0], ("Remarks", "edited 30")), With(routes[1], ("Remarks", "edited 31")), With(routes[2], ("Remarks", "edited 32")), With(routes[3], ("Remarks", "edited 33")),
            With(routes[4], ("Fleet IDs", "1,2")), With(routes[5], ("_delete", "TRUE")),
            With(Route(36, Token(11) + "," + Token(10)), ("ID", "")),
            With(Route(37, "1"), ("ID", ""), ("Type", "jumpseat")),
            With(Route(38, Token(14)), ("ID", ""))
        ]);
        return w;
    }

    public static void Edit(Workspace w, ResourceKind kind, int n, bool snapshot, params (string Field, string Value)[] fields)
    {
        var data = w.Resources[kind];
        var list = snapshot ? data.Snapshot : data.Draft;
        var at = list.FindIndex(r => r.LocalId == Id(n));
        list[at] = With(list[at], fields);
    }
    public static void Replace(Workspace w, ResourceKind kind, int n, bool snapshot, Func<DataRow, DataRow> change)
    {
        var data = w.Resources[kind];
        var list = snapshot ? data.Snapshot : data.Draft;
        var at = list.FindIndex(r => r.LocalId == Id(n));
        list[at] = change(list[at]);
    }

    public static WorkspaceReviewPlan Build(Workspace w, bool connected = true, ISet<string>? verified = null, Action<ChangeItem>? preview = null)
        => ReviewPlanBuilder.BuildForWorkspace(Planner, w, w.Mode, connected, verified ?? new HashSet<string>(), w.Mode == RunMode.Online && connected ? preview ?? (_ => { }) : null);

    /// <summary>One line per entry: resource, kind, row number, disposition, reason code, dependencies (as row numbers).</summary>
    public static string Describe(WorkspaceReviewPlan plan)
    {
        var numbers = plan.Entries.ToDictionary(e => e.Change.Id, e => Number(e.Change.After.LocalId));
        var lines = plan.Entries.Select(e =>
        {
            var c = e.Change;
            var dependencies = string.Join("+", c.Dependencies.Select(d => numbers[d]).OrderBy(x => x));
            var fields = string.Join(",", c.Fields.Select(f => f.Key + "=" + f.Value.Intent + ":" + f.Value.Value));
            return $"{c.Resource} {c.Kind} #{Number(c.After.LocalId)} {e.Disposition} {e.Reason?.Code ?? "-"} deps[{dependencies}] airlineParam={c.RequiresAirlineParameter} fields[{fields}]";
        });
        return string.Join("\n", lines.Append("blockers[" + string.Join(",", plan.Blockers.Select(b => b.Code)) + "]"));
    }

    /// <summary>The persisted form of items, with the random item IDs replaced by the row they plan, so two plans of the same rows compare equal.</summary>
    public static List<string> Persisted(IReadOnlyList<ChangeItem> items, IReadOnlyList<ChangeItem> universe)
    {
        var labels = universe.ToDictionary(i => i.Id, i => $"item-{i.Resource}-{Number(i.After.LocalId)}");
        return items.Select(item =>
        {
            var node = JsonSerializer.SerializeToNode(item)!.AsObject();
            node["Id"] = labels[item.Id];
            node["Dependencies"] = new JsonArray([.. item.Dependencies.Select(d => (JsonNode?)JsonValue.Create(labels[d]))]);
            return node.ToJsonString();
        }).ToList();
    }
}
