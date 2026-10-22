using System.Text.Json;
using VamSys.Core;
using static VamSys.UnitTests.Domain.TestRows;

namespace VamSys.UnitTests.Domain;

/// <summary>
/// F08/F11/F13: what draft edits turn into (create / update / delete), the three field intents (unspecified / set / clear),
/// and how references to rows that have no remote id yet become dependencies between planned changes.
/// </summary>
public class ChangePlannerTests
{
    static readonly ChangePlanner Planner = new();

    static ResourceData Resource(DataRow? baseline, DataRow row)
    {
        var data = new ResourceData { Draft = [row] };
        if (baseline is not null)
            data.Snapshot = [baseline];
        return data;
    }

    // Baseline and row are spelled "Field=value|Field=value"; "-" is no baseline; "" means nothing is planned.
    [Theory]
    [InlineData("-", "Name=A", "Create")]
    [InlineData("-", "Name=A|_delete=FALSE", "Create")]
    [InlineData("-", "Name=A|_delete=TRUE", "")]
    [InlineData("-", "Name=", "Create")]
    [InlineData("Name=A", "Name=A", "")]
    [InlineData("Name=A", "Name=B", "Update")]
    [InlineData("Name=A", "Name=", "Update")]
    [InlineData("Name=", "Name=A", "Update")]
    [InlineData("Name=A", "Name=A|Extra=x", "Update")]
    [InlineData("Name=A", "Name=A|_delete=FALSE", "")]
    [InlineData("Name=A|_delete=FALSE", "Name=A|_delete=FALSE", "")]
    [InlineData("Name=A", "Name=A|_delete=TRUE", "Delete")]
    [InlineData("Name=A", "Name=A|_delete=true", "Delete")]
    [InlineData("Name=A", "Name=B|_delete=TRUE", "Delete")]
    [InlineData("Name=A|_delete=TRUE", "Name=A|_delete=TRUE", "Delete")]
    [InlineData("Name=A|Extra=x", "Name=A", "")]
    public void A_draft_row_is_classified_by_its_difference_from_the_baseline(string baseline, string row, string expected)
    {
        var before = Parse(baseline);
        var draft = Parse(row)!;
        if (before is not null)
            draft.LocalId = before.LocalId;

        var planned = Planner.Plan(ResourceKind.Fleets, Resource(before, draft));
        var kind = ChangePlanner.GetChangeKind(draft, before);

        Assert.Equal(expected, planned.SingleOrDefault()?.Kind.ToString() ?? "");
        Assert.Equal(expected, kind?.ToString() ?? "");
    }

    [Theory]
    [InlineData("Name=a", "Name=b", "Name:Set")]
    [InlineData("Name=a", "Name=", "Name:Clear")]
    [InlineData("Name=", "Name=a", "Name:Set")]
    [InlineData("Name=a|Other=x", "Name=a|Other=y", "Other:Set")]
    [InlineData("Name=a|Other=x", "Name=b|Other=", "Name:Set,Other:Clear")]
    [InlineData("Name=a", "Name=a|Extra=", "Extra:Clear")]
    [InlineData("Name=a", "Name=a|Extra=v", "Extra:Set")]
    [InlineData("Name=a|Other=x", "Name=b", "Name:Set")]
    [InlineData("-", "Name=a|Other=", "Name:Set,Other:Clear")]
    public void Only_changed_fields_are_planned_and_an_empty_value_is_Clear_not_unspecified(string baseline, string row, string expected)
    {
        var before = Parse(baseline);
        var draft = Parse(row)!;
        if (before is not null)
            draft.LocalId = before.LocalId;

        var change = Planner.Plan(ResourceKind.Fleets, Resource(before, draft)).Single();

        var actual = string.Join(',', change.Fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{p.Value.Intent}"));
        Assert.Equal(expected, actual);
        Assert.All(change.Fields, p => Assert.Equal(draft.Get(p.Key), p.Value.Value));
        Assert.DoesNotContain(change.Fields.Values, f => f.Intent == FieldIntent.Unspecified);
    }

    [Fact]
    public void The_delete_flag_is_never_a_planned_field()
    {
        var before = Row(("Name", "A"), ("_delete", "FALSE"));
        var draft = before.With("_delete", "TRUE");

        var change = Planner.Plan(ResourceKind.Fleets, Resource(before, draft)).Single();

        Assert.Equal(ChangeKind.Delete, change.Kind);
        Assert.DoesNotContain("_delete", change.Fields.Keys);
    }

    [Fact]
    public void Planned_items_carry_detached_copies_of_the_baseline_and_the_draft()
    {
        var before = Row(("Name", "A"));
        var draft = before.With("Name", "B");
        var data = Resource(before, draft);

        var change = Planner.Plan(ResourceKind.Fleets, data).Single();
        change.After.Fields["Name"] = "detached";
        change.Before!.Fields["Name"] = "detached";

        Assert.Equal("B", data.Draft[0].Get("Name"));
        Assert.Equal("A", data.Snapshot[0].Get("Name"));
        Assert.Equal(draft.LocalId, change.After.LocalId);
    }

    [Fact]
    public void Planning_keeps_draft_order_and_ignores_unchanged_rows()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Row(("ID", i.ToString()), ("Name", "n" + i))).ToList();
        var data = new ResourceData { Snapshot = rows.Select(r => r.Copy()).ToList(), Draft = [.. rows] };
        data.ReplaceDraftRows([rows[3].With("Name", "changed"), rows[1].With("Name", "changed")]);

        var kinds = Planner.Plan(ResourceKind.Fleets, data).Select(c => (c.After.Get("ID"), c.Kind)).ToList();

        Assert.Equal([("1", ChangeKind.Update), ("3", ChangeKind.Update)], kinds);
    }

    // The planner replaced an older copy of itself that planned straight from full rows; random drafts must plan identically.
    [Theory]
    [InlineData(20261015)]
    [InlineData(7)]
    public void Planning_matches_a_straightforward_full_row_oracle(int seed)
    {
        var random = new Random(seed);
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var snapshot = Enumerable.Range(0, 300).Select(i => Row(("ID", i.ToString()), ("Name", "n" + i), ("Remarks", "r" + i))).ToList();
            var draft = snapshot.Select(r =>
            {
                var copy = r.Copy();
                switch (random.Next(7))
                {
                    case 0: copy.Fields["Remarks"] = "edited"; break;
                    case 1: copy.Fields["Remarks"] = ""; break;
                    case 2: copy.Fields.Remove("Remarks"); break;
                    case 3: copy.Fields["Unknown CSV"] = "unknown"; break;
                    case 4: copy.Fields["_delete"] = "true"; break;
                }
                return copy;
            }).ToList();
            draft.Add(new());
            draft.Add(Row(("_delete", "TRUE")));
            var data = new ResourceData { Snapshot = snapshot, Draft = draft };

            var actual = Planner.Plan(kind, data);

            Assert.Equal(Describe(Oracle(kind, data)), Describe(actual));
            var baseline = data.Snapshot.ToDictionary(r => r.LocalId);
            var kinds = actual.ToDictionary(c => c.After.LocalId, c => c.Kind);
            foreach (var row in data.Draft)
                Assert.Equal(kinds.TryGetValue(row.LocalId, out var planned) ? planned : (ChangeKind?)null, ChangePlanner.GetChangeKind(row, baseline.GetValueOrDefault(row.LocalId)));
        }
    }

    static string Describe(IEnumerable<ChangeItem> items) => JsonSerializer.Serialize(items.Select(c => new { c.Resource, c.Kind, c.Before, c.After, c.Fields }));

    static List<ChangeItem> Oracle(ResourceKind kind, ResourceData data)
    {
        var baseline = data.Snapshot.ToDictionary(r => r.LocalId);
        var result = new List<ChangeItem>();
        foreach (var row in data.Draft)
        {
            baseline.TryGetValue(row.LocalId, out var before);
            var fields = row.Fields.Where(p => p.Key != "_delete" && (before is null || !before.Fields.TryGetValue(p.Key, out var old) || old != p.Value))
                .ToDictionary(p => p.Key, p => new FieldChange(p.Value == "" ? FieldIntent.Clear : FieldIntent.Set, p.Value));
            var delete = row.Get("_delete").Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            if (before != null && !delete && fields.Count == 0 || before is null && delete)
                continue;
            result.Add(new() { Resource = kind, Kind = delete ? ChangeKind.Delete : before is null ? ChangeKind.Create : ChangeKind.Update, Before = before?.Copy(), After = row.Copy(), Fields = fields });
        }
        return result;
    }

    // ---- workspace planning: references and dependencies ----

    [Fact]
    public void A_known_local_reference_is_replaced_by_the_remote_key_and_remark_text_is_left_alone()
    {
        var fleet = Row(("ID", "1"), ("Name", "fleet"));
        var workspace = WorkspaceWith(ResourceKind.Fleets, fleet);
        workspace.Resources[ResourceKind.Aircraft].Draft.Add(Row(("Fleet ID", LocalReference.Format(fleet.LocalId)), ("Internal Remarks", "local: some note")));

        var item = Planner.PlanWorkspace(workspace).Single();

        Assert.Equal("1", item.After.Get("Fleet ID"));
        Assert.Equal("local: some note", item.After.Get("Internal Remarks"));
        Assert.Equal(new FieldChange(FieldIntent.Set, "1"), item.Fields["Fleet ID"]);
        Assert.Empty(item.Dependencies);
    }

    [Theory]
    [InlineData("local:")]
    [InlineData("LOCAL:")]
    [InlineData("Local:")]
    public void The_local_prefix_is_recognised_in_any_case_when_resolving_and_when_depending(string prefix)
    {
        var known = Row(("ID", "7"), ("Name", "known"));
        var created = Row(("Name", "created"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "pax"));
        var workspace = WorkspaceWith(ResourceKind.Fleets, known);
        workspace.Resources[ResourceKind.Fleets].Draft.Add(created);
        workspace.Resources[ResourceKind.Routes].Draft.Add(Row(("Type", "scheduled"), ("Fleet IDs", $"{prefix}{known.LocalId},{prefix}{created.LocalId}")));

        var items = Planner.PlanWorkspace(workspace);
        var route = items.Single(i => i.Resource == ResourceKind.Routes);
        var fleet = items.Single(i => i.Resource == ResourceKind.Fleets);

        Assert.Equal([fleet.Id], route.Dependencies);
        Assert.Equal($"7,{prefix}{created.LocalId}", route.After.Get("Fleet IDs"));
    }

    [Fact]
    public void Several_references_to_the_same_new_row_make_one_dependency_and_each_kind_adds_its_own()
    {
        var fleet = Row(("Name", "fleet"));
        var airport = Row(("ICAO/IATA", "ZBAA"), ("Name", "Beijing"));
        var workspace = new Workspace();
        workspace.Resources[ResourceKind.Fleets].Draft.Add(fleet);
        workspace.Resources[ResourceKind.Airports].Draft.Add(airport);
        var ref1 = LocalReference.Format(fleet.LocalId);
        workspace.Resources[ResourceKind.Routes].Draft.Add(Row(("Type", "scheduled"), ("Fleet IDs", $"{ref1},{ref1}"), ("Departure Airport (ICAO/IATA)", LocalReference.Format(airport.LocalId)), ("Arrival Airport (ICAO/IATA)", "ZSPD")));

        var items = Planner.PlanWorkspace(workspace);
        var route = items.Single(i => i.Resource == ResourceKind.Routes);

        Assert.Equal(2, route.Dependencies.Count);
        Assert.Contains(items.Single(i => i.Resource == ResourceKind.Fleets).Id, route.Dependencies);
        Assert.Contains(items.Single(i => i.Resource == ResourceKind.Airports).Id, route.Dependencies);
    }

    [Theory]
    [InlineData(RunMode.Online, ResourceKind.Fleets, ChangeKind.Create, true)]
    [InlineData(RunMode.Demo, ResourceKind.Fleets, ChangeKind.Create, false)]
    [InlineData(RunMode.Offline, ResourceKind.Fleets, ChangeKind.Create, false)]
    [InlineData(RunMode.Online, ResourceKind.Fleets, ChangeKind.Update, false)]
    [InlineData(RunMode.Online, ResourceKind.Aircraft, ChangeKind.Create, false)]
    public void Only_online_fleet_creation_needs_the_airline_parameter(RunMode mode, ResourceKind kind, ChangeKind change, bool expected)
    {
        var workspace = new Workspace { Mode = mode };
        var data = workspace.Resources[kind];
        if (change == ChangeKind.Update)
        {
            var before = Row(("ID", "1"), ("Name", "A"));
            data.Snapshot = [before];
            data.Draft = [before.With("Name", "B")];
        }
        else
            data.Draft = [Row(("Name", "new"))];

        var item = Planner.PlanWorkspace(workspace).Single();

        Assert.Equal(change, item.Kind);
        Assert.Equal(expected, item.RequiresAirlineParameter);
    }
}
