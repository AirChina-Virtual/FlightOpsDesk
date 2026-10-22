using VamSys.Core;
using static VamSys.UnitTests.Domain.TestRows;

namespace VamSys.UnitTests.Domain;

/// <summary>
/// F09: choosing fleets for routes (replace / add / remove a set) and aircraft (exactly one). The service computes final
/// local comma lists; unresolved references the user did not touch are kept as they were written.
/// </summary>
public class FleetAssignmentServiceTests
{
    static readonly FleetAssignmentService Service = new();
    static readonly CsvAdapter Csv = new();

    static Workspace WithFleets(params string[] ids)
    {
        var w = new Workspace();
        w.Resources[ResourceKind.Fleets].Draft = [.. ids.Select(id => Row(("ID", id), ("Name", "Fleet " + id), ("Type Code", "T" + id)))];
        return w;
    }

    static DataRow Scheduled(string fleetIds) => Row(("ID", "r"), ("Type", "scheduled"), ("Fleet IDs", fleetIds));

    static string[] Selected(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries);

    // current Fleet IDs, selection, mode -> new Fleet IDs; "=" means the row stays exactly as it was.
    [Theory]
    [InlineData("1,2", "3", FleetAssignmentMode.Add, "1,2,3")]
    [InlineData("1,2", "2", FleetAssignmentMode.Add, "=")]
    [InlineData("1,2", "2,3", FleetAssignmentMode.Add, "1,2,3")]
    [InlineData("", "1", FleetAssignmentMode.Add, "1")]
    [InlineData("1,2", "1", FleetAssignmentMode.Remove, "2")]
    [InlineData("1,2,3", "1,3", FleetAssignmentMode.Remove, "2")]
    [InlineData("1", "4", FleetAssignmentMode.Remove, "=")]
    [InlineData("1,2", "3,4", FleetAssignmentMode.Replace, "3,4")]
    [InlineData("1,2", "2,1", FleetAssignmentMode.Replace, "=")]
    [InlineData("1, 2", "1,2", FleetAssignmentMode.Replace, "=")]
    [InlineData("1,1,2", "2", FleetAssignmentMode.Replace, "2")]
    public void A_route_gets_the_final_set_for_each_mode_and_a_set_equal_result_changes_nothing(string current, string selection, FleetAssignmentMode mode, string expected)
    {
        var preview = Service.Preview(WithFleets("1", "2", "3", "4"), ResourceKind.Routes, [Scheduled(current)], Selected(selection), mode);

        Assert.Equal("Fleet IDs", preview.Field);
        if (expected == "=")
            Assert.Empty(preview.Rows);
        else
            Assert.Equal(expected, preview.Rows.Single().Get("Fleet IDs"));
        Assert.Empty(preview.Issues);
    }

    [Fact]
    public void References_that_are_not_fleets_of_the_workspace_are_kept_when_untouched_and_a_noop_keeps_the_exact_text()
    {
        var w = WithFleets("02");
        var route = Row(("ID", "1"), ("Type", "scheduled"), ("Fleet IDs", "missing, 01"));

        var add = Service.Preview(w, ResourceKind.Routes, [route], ["02"], FleetAssignmentMode.Add);
        Assert.Empty(add.Issues);
        Assert.Equal("missing,01,02", add.Rows.Single().Get("Fleet IDs"));

        Assert.Empty(Service.Preview(w, ResourceKind.Routes, [route], ["01", "missing"], FleetAssignmentMode.Replace).Rows);
        Assert.False(Service.Options(w, ["missing"]).Single(o => o.Token == "missing").Available);

        var remove = Service.Preview(w, ResourceKind.Routes, add.Rows, ["missing"], FleetAssignmentMode.Remove);
        Assert.Equal("01,02", remove.Rows.Single().Get("Fleet IDs"));
    }

    [Fact]
    public void Choosing_a_fleet_that_does_not_exist_or_is_deleted_is_an_issue_but_leaving_an_old_one_alone_is_not()
    {
        var w = WithFleets("1", "2");
        w.Resources[ResourceKind.Fleets].ReplaceDraftRows([w.Resources[ResourceKind.Fleets].Draft[1].With("_delete", "TRUE")]);

        Assert.Single(Service.Preview(w, ResourceKind.Routes, [Scheduled("1")], ["9"], FleetAssignmentMode.Add).Issues);
        Assert.Single(Service.Preview(w, ResourceKind.Routes, [Scheduled("1")], ["2"], FleetAssignmentMode.Add).Issues);
        Assert.Empty(Service.Preview(w, ResourceKind.Routes, [Scheduled("1,9")], ["1"], FleetAssignmentMode.Add).Issues);
    }

    [Fact]
    public void An_aircraft_has_exactly_one_fleet_and_only_replace_is_allowed()
    {
        var w = WithFleets("1", "2");
        var plane = Row(("Fleet ID", "1"));

        Assert.Empty(Service.Preview(w, ResourceKind.Aircraft, [plane], ["2"], FleetAssignmentMode.Replace).Issues);
        Assert.NotEmpty(Service.Preview(w, ResourceKind.Aircraft, [plane], ["1", "2"], FleetAssignmentMode.Replace).Issues);
        Assert.NotEmpty(Service.Preview(w, ResourceKind.Aircraft, [plane], [], FleetAssignmentMode.Replace).Issues);
        var add = Assert.Throws<ArgumentException>(() => Service.Preview(w, ResourceKind.Aircraft, [plane], ["2"], FleetAssignmentMode.Add));
        Assert.NotNull(MessageErrors.Describe(add).Code);
        Assert.Throws<ArgumentException>(() => Service.Preview(w, ResourceKind.Aircraft, [plane], ["2"], FleetAssignmentMode.Remove));
    }

    [Theory]
    [InlineData("scheduled", "", true)]
    [InlineData("jumpseat", "", false)]
    [InlineData("scheduled", "1", false)]
    public void A_route_other_than_a_jumpseat_cannot_be_left_without_a_fleet(string type, string selection, bool issue)
    {
        var route = Row(("Type", type), ("Fleet IDs", "1"));

        var preview = Service.Preview(WithFleets("1", "2"), ResourceKind.Routes, [route], Selected(selection), FleetAssignmentMode.Replace);

        Assert.Equal(issue, preview.Issues.Count > 0);
    }

    [Fact]
    public void A_jumpseat_may_not_gain_an_unavailable_fleet_and_a_row_marked_deleted_is_refused()
    {
        var w = WithFleets("1");

        Assert.NotEmpty(Service.Preview(w, ResourceKind.Routes, [Row(("Type", "jumpseat"))], ["missing"], FleetAssignmentMode.Add).Issues);
        var deleted = Service.Preview(w, ResourceKind.Routes, [Row(("_delete", "TRUE"))], ["1"], FleetAssignmentMode.Replace);
        Assert.Single(deleted.Issues);
        Assert.Empty(deleted.Rows);
    }

    [Fact]
    public void Only_fields_for_fleet_references_exist_on_aircraft_and_routes()
    {
        Assert.Equal("Fleet ID", FleetAssignmentService.Field(ResourceKind.Aircraft));
        Assert.Equal("Fleet IDs", FleetAssignmentService.Field(ResourceKind.Routes));
        Assert.Throws<ArgumentException>(() => FleetAssignmentService.Field(ResourceKind.Airports));
        Assert.Throws<ArgumentException>(() => FleetAssignmentService.Field(ResourceKind.Routings));
    }

    [Fact]
    public void Tokens_are_trimmed_unique_ignoring_case_and_without_blanks()
    {
        Assert.Equal(["a", "B", "c"], FleetAssignmentService.Tokens(" a, B ,,A, b,c"));
        Assert.Empty(FleetAssignmentService.Tokens(""));
    }

    // ---- the picker's list ----

    [Fact]
    public void Options_list_fleets_by_name_and_flag_deleted_and_duplicated_ones_as_unavailable()
    {
        var w = new Workspace();
        var fleets = w.Resources[ResourceKind.Fleets];
        var local = Row(("Name", "Zulu new"), ("Type Code", "Z"));
        fleets.Draft = [Row(("ID", "2"), ("Name", "Bravo"), ("Type Code", "B")), Row(("ID", "1"), ("Name", "alpha"), ("Type Code", "A")), Row(("ID", "3"), ("Name", "Dup"), ("Type Code", "D")),
            Row(("ID", "3"), ("Name", "Dup again"), ("Type Code", "D")), Row(("ID", "4"), ("Name", "Gone"), ("Type Code", "G"), ("_delete", "TRUE")), local];

        var options = Service.Options(w);

        Assert.Equal(5, options.Count);
        Assert.True(options.Single(o => o.Token == "1").Available);
        Assert.False(options.Single(o => o.Token == "3").Available);
        Assert.False(options.Single(o => o.Token == "4").Available);
        Assert.True(options.Single(o => o.Token == LocalReference.Format(local.LocalId)).Available);
        Assert.Equal(["alpha", "Bravo", "Dup", "Gone", "Zulu new"], options.Select(o => o.Name.ToString()));
    }

    [Fact]
    public void A_local_token_of_a_row_that_already_has_an_id_stays_representable()
    {
        var w = WithFleets("5");
        var fleet = w.Resources[ResourceKind.Fleets].Draft[0];

        var option = Service.Options(w, [LocalReference.Format(fleet.LocalId)]).Single(o => o.Token == LocalReference.Format(fleet.LocalId));

        Assert.True(option.Available);
        Assert.Equal("Fleet 5", option.Name.ToString());
    }

    // ---- apply ----

    [Fact]
    public void Apply_replaces_the_rows_as_one_undoable_step_and_adds_the_column()
    {
        var w = WithFleets("001", "002");
        var routes = w.Resources[ResourceKind.Routes];
        CsvAdapter.Import(ResourceKind.Routes, routes, [Row(("ID", "01"), ("Type", "scheduled"), ("Fleet IDs", "001"))], true);
        routes.Columns.Remove("Fleet IDs");

        Service.Apply(routes, Service.Preview(w, ResourceKind.Routes, routes.Draft, ["002"], FleetAssignmentMode.Add));

        Assert.Equal("001,002", routes.Draft.Single().Get("Fleet IDs"));
        Assert.Contains("Fleet IDs", routes.Columns);
        Assert.Single(routes.Undo);
        routes.UndoEdit();
        Assert.Equal("001", routes.Draft.Single().Get("Fleet IDs"));
        routes.RedoEdit();
        Assert.Equal("001,002", routes.Draft.Single().Get("Fleet IDs"));
    }

    [Fact]
    public void An_exported_comma_list_keeps_leading_zeros()
    {
        var w = WithFleets("001", "002");
        var routes = w.Resources[ResourceKind.Routes];
        CsvAdapter.Import(ResourceKind.Routes, routes, [Row(("ID", "01"), ("Type", "scheduled"), ("Fleet IDs", "001"))], true);
        Service.Apply(routes, Service.Preview(w, ResourceKind.Routes, routes.Draft, ["002"], FleetAssignmentMode.Add));

        var file = Csv.Export(ResourceKind.Routes, new ChangePlanner().Plan(ResourceKind.Routes, routes)).Single();

        Assert.Equal("001,002", Reimport(Csv, file).Single().Get("Fleet IDs"));
    }

    [Fact]
    public void Apply_refuses_a_preview_with_issues_and_changes_nothing()
    {
        var w = WithFleets("1");
        var routes = w.Resources[ResourceKind.Routes];
        routes.Draft = [Scheduled("1")];
        var before = Snapshot(routes.Draft);

        var preview = Service.Preview(w, ResourceKind.Routes, routes.Draft, ["nope"], FleetAssignmentMode.Add);

        Assert.Throws<InvalidOperationException>(() => Service.Apply(routes, preview));
        Assert.Equal(before, Snapshot(routes.Draft));
        Assert.Empty(routes.Undo);
    }

    [Fact]
    public void Apply_of_an_empty_preview_records_no_step()
    {
        var w = WithFleets("1");
        var routes = w.Resources[ResourceKind.Routes];
        routes.Draft = [Scheduled("1")];

        Service.Apply(routes, Service.Preview(w, ResourceKind.Routes, routes.Draft, ["1"], FleetAssignmentMode.Replace));

        Assert.Empty(routes.Undo);
    }

    [Fact]
    public void A_fleet_that_does_not_exist_remotely_yet_can_be_chosen_locally_but_cannot_leave_through_csv_export()
    {
        var w = new Workspace();
        var fleet = Row(("Name", "New"));
        w.Resources[ResourceKind.Fleets].Draft = [fleet];
        var aircraft = w.Resources[ResourceKind.Aircraft];
        aircraft.Draft = [Row(("Name", "Plane"))];

        Service.Apply(aircraft, Service.Preview(w, ResourceKind.Aircraft, aircraft.Draft, [LocalReference.Format(fleet.LocalId)], FleetAssignmentMode.Replace));

        Assert.Equal(LocalReference.Format(fleet.LocalId), aircraft.Draft.Single().Get("Fleet ID"));
        Assert.Throws<InvalidOperationException>(() => Csv.Export(ResourceKind.Aircraft, new ChangePlanner().Plan(ResourceKind.Aircraft, aircraft)));
    }
}
