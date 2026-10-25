using VamSys.Core;
using static VamSys.UnitTests.Domain.TestRows;

namespace VamSys.UnitTests.Domain;

/// <summary>
/// F17 / I04: deleting drafts. New drafts disappear locally, existing rows are only marked, a row that other rows still
/// reference cannot be deleted (local reference check), and restoring brings the edited row back.
/// </summary>
public class DeletionServiceTests
{
    static readonly DeletionService Service = new();
    static readonly CsvAdapter Csv = new();
    static readonly ChangePlanner Planner = new();

    static IEnumerable<Guid> All(ResourceData data) => data.Draft.Select(r => r.LocalId);

    // ---- what a deletion does ----

    [Fact]
    public void New_drafts_are_removed_existing_rows_are_marked_and_both_undo_together()
    {
        var w = new Workspace();
        var data = w.Resources[ResourceKind.Fleets];
        CsvAdapter.Import(ResourceKind.Fleets, data, [Row(("ID", "001"), ("Name", "before"))], true);
        var existing = data.Draft.Single();
        data.Edit([existing.LocalId], () => data.ReplaceDraftRows([existing.With("Name", "edited")]));
        data.Edit([], () => data.Draft.Add(Row(("Name", "new"))), structural: true);
        var plan = Service.Preview(w, ResourceKind.Fleets, All(data));

        Assert.Single(plan.RemoveDrafts);
        Assert.Equal([existing.LocalId], plan.MarkExisting);
        Assert.Empty(plan.Issues);

        Service.Apply(w, plan);

        Assert.True(Schemas.Deleted(data.Draft.Single()));
        Assert.Equal("edited", data.Draft.Single().Get("Name"));
        var change = Planner.Plan(ResourceKind.Fleets, data).Single();
        Assert.Equal(ChangeKind.Delete, change.Kind);
        var file = Reimport(Csv, Csv.Export(ResourceKind.Fleets, [change]).Single()).Single();
        Assert.Equal("001", file.Get("ID"));
        Assert.Equal("TRUE", file.Get("_delete"));

        Service.Restore(data, [data.Draft.Single().LocalId]);
        Assert.False(Schemas.Deleted(data.Draft.Single()));
        Assert.Equal("edited", data.Draft.Single().Get("Name"));

        data.UndoEdit();
        Assert.True(Schemas.Deleted(data.Draft.Single()));
        data.UndoEdit();
        Assert.Equal(2, data.Draft.Count);
        Assert.False(Schemas.Deleted(data.Draft[0]));
    }

    [Fact]
    public void Apply_keeps_the_positions_of_the_remaining_rows()
    {
        var w = new Workspace();
        var data = w.Resources[ResourceKind.Fleets];
        CsvAdapter.Import(ResourceKind.Fleets, data, [Row(("ID", "1")), Row(("ID", "2")), Row(("ID", "3"))], true);
        data.Edit([], () => data.Draft.Insert(1, Row(("Name", "draft only"))), structural: true);
        var ids = data.Draft.Select(r => r.LocalId).ToArray();

        Service.Apply(w, Service.Preview(w, ResourceKind.Fleets, [ids[1], ids[2]]));

        Assert.Equal([ids[0], ids[2], ids[3]], data.Draft.Select(r => r.LocalId));
        Assert.Equal(["FALSE", "TRUE", "FALSE"], data.Draft.Select(r => Schemas.Deleted(r) ? "TRUE" : "FALSE"));
    }

    [Fact]
    public void Preview_ignores_unknown_ids_and_does_not_mark_a_row_that_is_already_marked()
    {
        var w = new Workspace();
        var data = w.Resources[ResourceKind.Fleets];
        CsvAdapter.Import(ResourceKind.Fleets, data, [Row(("ID", "1")), Row(("ID", "2"), ("_delete", "TRUE"))], true);

        var plan = Service.Preview(w, ResourceKind.Fleets, [data.Draft[0].LocalId, data.Draft[1].LocalId, Guid.NewGuid()]);

        Assert.Equal([data.Draft[0].LocalId], plan.MarkExisting);
        Assert.Empty(plan.RemoveDrafts);
    }

    [Fact]
    public void Applying_a_plan_with_nothing_to_do_records_no_step()
    {
        var w = new Workspace();
        var data = w.Resources[ResourceKind.Fleets];
        CsvAdapter.Import(ResourceKind.Fleets, data, [Row(("ID", "1"), ("_delete", "TRUE"))], true);

        Service.Apply(w, Service.Preview(w, ResourceKind.Fleets, All(data)));

        Assert.Empty(data.Undo);
    }

    [Fact]
    public void Restore_unmarks_only_rows_that_are_marked_in_one_step_and_ignores_the_others()
    {
        var w = new Workspace();
        var data = w.Resources[ResourceKind.Fleets];
        CsvAdapter.Import(ResourceKind.Fleets, data, [Row(("ID", "1"), ("_delete", "TRUE")), Row(("ID", "2"))], true);

        Service.Restore(data, [data.Draft[1].LocalId]);
        Assert.Empty(data.Undo);

        Service.Restore(data, All(data));
        Assert.All(data.Draft, r => Assert.False(Schemas.Deleted(r)));
        Assert.Single(data.Undo);
    }

    [Fact]
    public void Every_resource_exports_its_own_key_column_with_the_delete_flag()
    {
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var w = new Workspace();
            var data = w.Resources[kind];
            var key = Schemas.All[kind].Key;
            CsvAdapter.Import(kind, data, [Row((key, kind == ResourceKind.Airports ? "ZBAA" : "001"), ("Name", "original"))], true);

            Service.Apply(w, Service.Preview(w, kind, All(data)));
            var output = Reimport(Csv, Csv.Export(kind, Planner.Plan(kind, data)).Single()).Single();

            Assert.Equal(data.Draft.Single().Get(key), output.Get(key));
            Assert.Equal("TRUE", output.Get("_delete"));
        }
    }

    // ---- the reference check (I04) ----

    static Workspace FleetWorkspace(out DataRow fleet)
    {
        fleet = Row(("ID", "1"), ("Name", "fleet"));
        var w = new Workspace();
        CsvAdapter.Import(ResourceKind.Fleets, w.Resources[ResourceKind.Fleets], [fleet], true);
        return w;
    }

    [Theory]
    [InlineData("1")]
    [InlineData(" 1 ")]
    [InlineData("local")]
    [InlineData("unresolved:1")]
    public void A_fleet_is_in_use_under_any_name_an_aircraft_may_use_for_it(string reference)
    {
        var w = FleetWorkspace(out var fleet);
        var token = reference == "local" ? LocalReference.Format(fleet.LocalId) : reference;
        if (reference == "unresolved:1")
            w.Resources[ResourceKind.Fleets].Draft = [w.Resources[ResourceKind.Fleets].Draft[0].WithIdentity("1")];
        w.Resources[ResourceKind.Aircraft].Draft = [Row(("ID", "2"), ("Fleet ID", token))];

        var issues = Service.Preview(w, ResourceKind.Fleets, [w.Resources[ResourceKind.Fleets].Draft[0].LocalId]).Issues;

        var issue = Assert.Single(issues);
        Assert.Equal("_delete", issue.Field);
        Assert.Equal(fleet.LocalId, issue.RowId);
    }

    [Fact]
    public void Each_reference_in_a_route_fleet_list_is_one_issue_and_other_fleets_do_not_count()
    {
        var w = FleetWorkspace(out var fleet);
        w.Resources[ResourceKind.Routes].Draft = [Row(("ID", "9"), ("Type", "scheduled"), ("Fleet IDs", "1,2")), Row(("ID", "8"), ("Type", "scheduled"), ("Fleet IDs", "2"))];

        Assert.Single(Service.Preview(w, ResourceKind.Fleets, [fleet.LocalId]).Issues);
    }

    [Theory]
    [InlineData(ResourceKind.Routes, "Departure Airport (ICAO/IATA)")]
    [InlineData(ResourceKind.Routes, "Arrival Airport (ICAO/IATA)")]
    [InlineData(ResourceKind.Routings, "Departure Airport (ICAO/IATA)")]
    [InlineData(ResourceKind.Routings, "Arrival Airport (ICAO/IATA)")]
    public void An_airport_is_in_use_by_a_route_or_routing_endpoint(ResourceKind source, string field)
    {
        var w = new Workspace();
        var airport = Row(("ICAO/IATA", "ZBAA"), ("Name", "Beijing"));
        CsvAdapter.Import(ResourceKind.Airports, w.Resources[ResourceKind.Airports], [airport], true);
        w.Resources[source].Draft = [Row(("ID", "1"), (field, "zbaa"))];

        var issues = Service.Preview(w, ResourceKind.Airports, [airport.LocalId]).Issues;

        Assert.Equal(airport.LocalId, Assert.Single(issues).RowId);
    }

    [Theory]
    [InlineData("EGLL")]
    [InlineData("LHR")]
    [InlineData("101")]
    [InlineData("unresolved:101")]
    public void An_airport_is_in_use_under_its_codes_its_remote_id_and_its_api_aliases(string reference)
    {
        var w = new Workspace();
        var airport = Row(("ICAO/IATA", "EGLL"), ("Name", "Heathrow"))
            .WithIdentity(new("101", "7"))
            .WithRawApiJson("""{"id":101,"icao":"EGLL","iata":"LHR"}""");
        CsvAdapter.Import(ResourceKind.Airports, w.Resources[ResourceKind.Airports], [airport], true);
        w.Resources[ResourceKind.Routes].Draft = [Row(("ID", "1"), ("Departure Airport (ICAO/IATA)", reference))];

        Assert.Single(Service.Preview(w, ResourceKind.Airports, [airport.LocalId]).Issues);
    }

    [Fact]
    public void Imported_info_columns_of_an_airport_also_count_as_names_for_it()
    {
        var w = new Workspace();
        var airport = Row(("ICAO/IATA", "ZBAA"), ("Info-Airport ID", "555"), ("Info-Airport ICAO", "ZBAA"), ("Info-Airport IATA", "PEK"));
        CsvAdapter.Import(ResourceKind.Airports, w.Resources[ResourceKind.Airports], [airport], true);

        foreach (var reference in new[] { "555", "PEK" })
        {
            w.Resources[ResourceKind.Routings].Draft = [Row(("ID", "1"), ("Arrival Airport (ICAO/IATA)", reference))];
            Assert.Single(Service.Preview(w, ResourceKind.Airports, [airport.LocalId]).Issues);
        }
    }

    [Fact]
    public void Rows_that_are_themselves_marked_deleted_or_are_deleted_together_do_not_block_the_deletion()
    {
        var w = FleetWorkspace(out var fleet);
        var plane = Row(("ID", "2"), ("Fleet ID", "1"));
        w.Resources[ResourceKind.Aircraft].Draft = [plane];
        Assert.Single(Service.Preview(w, ResourceKind.Fleets, [fleet.LocalId]).Issues);

        w.Resources[ResourceKind.Aircraft].ReplaceDraftRows([plane.With("_delete", "TRUE")]);
        Assert.Empty(Service.Preview(w, ResourceKind.Fleets, [fleet.LocalId]).Issues);
        Assert.Empty(Service.CheckReferences(w, ResourceKind.Fleets, new HashSet<Guid> { fleet.LocalId }));
    }

    [Fact]
    public void Other_resources_are_not_checked_so_a_route_aircraft_or_routing_can_always_be_deleted()
    {
        var w = new Workspace();
        var route = Row(("ID", "1"), ("Fleet IDs", "1"), ("Departure Airport (ICAO/IATA)", "ZBAA"));
        CsvAdapter.Import(ResourceKind.Routes, w.Resources[ResourceKind.Routes], [route], true);

        Assert.Empty(Service.Preview(w, ResourceKind.Routes, [route.LocalId]).Issues);
    }

    [Fact]
    public void A_preview_made_before_a_new_reference_appeared_cannot_be_applied()
    {
        var w = FleetWorkspace(out var fleet);
        var plan = Service.Preview(w, ResourceKind.Fleets, [fleet.LocalId]);
        Assert.Empty(plan.Issues);
        w.Resources[ResourceKind.Aircraft].Draft = [Row(("ID", "2"), ("Fleet ID", LocalReference.Format(fleet.LocalId)))];

        Assert.Single(Service.Preview(w, ResourceKind.Fleets, [fleet.LocalId]).Issues);
        var error = Assert.Throws<InvalidOperationException>(() => Service.Apply(w, plan));

        Assert.NotNull(MessageErrors.Describe(error).Code);
        Assert.False(Schemas.Deleted(w.Resources[ResourceKind.Fleets].Draft.Single()));
        Assert.Empty(w.Resources[ResourceKind.Fleets].Undo);
    }

    [Fact]
    public void A_deleted_row_that_is_still_in_use_is_also_reported_by_validation_on_its_delete_column()
    {
        var w = FleetWorkspace(out var fleet);
        w.Resources[ResourceKind.Aircraft].Draft = [Row(("ID", "2"), ("Name", "P"), ("Registration", "B-1"), ("Fleet ID", "1"))];
        w.Resources[ResourceKind.Fleets].ReplaceDraftRows([fleet.With("_delete", "TRUE")]);

        Assert.Contains(Schemas.Validate(w, ResourceKind.Fleets), i => i.Field == "_delete" && i.RowId == fleet.LocalId);
    }
}

static class DeletionTestExtensions
{
    public static DataRow WithIdentity(this DataRow row, string remoteId) => row.WithIdentity(new ResourceIdentity(remoteId, "7"));
}
