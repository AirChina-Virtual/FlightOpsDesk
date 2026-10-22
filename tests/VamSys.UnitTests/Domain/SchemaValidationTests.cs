using VamSys.Core;
using static VamSys.UnitTests.Domain.TestRows;

namespace VamSys.UnitTests.Domain;

/// <summary>F08/F11/F13: draft validation (Schemas.Validate). Field type rules themselves are locked by FieldSpecTests; these tests cover the rule set.</summary>
public class SchemaValidationTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";
    // Message codes of the two rules the legacy suite asserted by text (immutable routing endpoint, unknown reference).
    const string EndpointImmutable = "Text_31E5AEFD06", UnknownReference = "Text_5CDAFFB468";

    static Workspace Base(RunMode mode = RunMode.Offline)
    {
        var w = new Workspace { Mode = mode };
        CsvAdapter.Import(ResourceKind.Airports, w.Resources[ResourceKind.Airports], [Row(("ICAO/IATA", "ZBAA"), ("Name", "A")), Row(("ICAO/IATA", "ZSPD"), ("Name", "B"))], true);
        CsvAdapter.Import(ResourceKind.Fleets, w.Resources[ResourceKind.Fleets], [Row(("ID", "1"), ("Name", "F"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "150"))], true);
        return w;
    }

    static DataRow ValidRoute(params (string Field, string Value)[] overrides)
    {
        var row = Row((Dep, "ZBAA"), (Arr, "ZSPD"), ("Type", "scheduled"), ("Callsign", "ACA123"), ("Flight Number", "AC123"), ("Fleet IDs", "1"));
        foreach (var (field, value) in overrides)
            row.Fields[field] = value;
        return row;
    }

    static List<Issue> Check(Workspace w, ResourceKind kind, params DataRow[] draft)
    {
        w.Resources[kind].Draft = [.. w.Resources[kind].Draft, .. draft];
        return Schemas.Validate(w, kind);
    }

    static string[] Fields(IEnumerable<Issue> issues) => issues.Select(i => i.Field).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void A_complete_new_route_has_no_issues()
    {
        Assert.Empty(Check(Base(), ResourceKind.Routes, ValidRoute()));
    }

    [Theory]
    [InlineData("Type", "weird", true)]
    [InlineData("Type", "jumpseat", false)]
    [InlineData("Callsign", "AB", true)]
    [InlineData("Callsign", "ABCDEFGH", true)]
    [InlineData("Callsign", "ABCD", false)]
    [InlineData("Callsign", "ABCDEFG", false)]
    [InlineData("Callsign", "", true)]
    [InlineData("Flight Number", "A1", true)]
    [InlineData("Flight Number", "ABCDEFG", true)]
    [InlineData("Flight Number", "AB1", false)]
    [InlineData("Flight Number", "ABCDEF", false)]
    [InlineData("Cost Index", "AUTO", false)]
    [InlineData("Cost Index", "0", false)]
    [InlineData("Cost Index", "999", false)]
    [InlineData("Cost Index", "", false)]
    [InlineData("Cost Index", "1000", true)]
    [InlineData("Cost Index", "-1", true)]
    [InlineData("Cost Index", "auto", true)]
    [InlineData("Fleet IDs", "", true)]
    [InlineData("Fleet IDs", "99", true)]
    [InlineData("Fleet IDs", "1, 1", false)]
    [InlineData(Dep, "EGLL", true)]
    [InlineData(Dep, "zbaa", false)]
    [InlineData(Arr, "", true)]
    [InlineData("Start Date", "2026-01-01 00:00:00", false)]
    [InlineData("Start Date", "2026-01-01", true)]
    [InlineData("End Date", "2026-13-01 00:00:00", true)]
    [InlineData("Departure Time (HH:MM)", "23:59", false)]
    [InlineData("Departure Time (HH:MM)", "24:00", true)]
    [InlineData("Arrival Time (HH:MM)", "9:5", true)]
    [InlineData("Is Hidden", "TRUE", false)]
    [InlineData("Is Hidden", "yes", true)]
    [InlineData("Service Days", "monday, friday", false)]
    [InlineData("Service Days", "monday,funday", true)]
    public void A_route_field_is_flagged_exactly_when_its_value_breaks_a_rule(string field, string value, bool flagged)
    {
        var issues = Check(Base(), ResourceKind.Routes, ValidRoute((field, value)));

        Assert.Equal(flagged ? [field] : [], Fields(issues));
    }

    [Fact]
    public void A_jumpseat_route_needs_no_fleet_but_a_scheduled_one_does()
    {
        Assert.Empty(Check(Base(), ResourceKind.Routes, ValidRoute(("Type", "jumpseat"), ("Fleet IDs", ""))));
        Assert.Equal(["Fleet IDs"], Fields(Check(Base(), ResourceKind.Routes, ValidRoute(("Fleet IDs", "")))));
    }

    [Theory]
    [InlineData("pax", "Max Passengers", "100", "")]
    [InlineData("pax", "Max Passengers", "", "Max Passengers")]
    [InlineData("pax", "Max Passengers", "-1", "Max Passengers")]
    [InlineData("pax", "Max Passengers", "abc", "Max Passengers")]
    [InlineData("cargo", "Max Freight", "5000", "")]
    [InlineData("cargo", "Max Freight", "", "Max Freight")]
    [InlineData("cargo", "Max Passengers", "", "")]
    [InlineData("pax-cargo", "Max Freight", "", "Max Freight")]
    [InlineData("pax-cargo", "Max Passengers", "", "Max Passengers")]
    [InlineData("pax-containers", "Container Units", "", "Container Units")]
    [InlineData("cargo-containers", "Container Units", "4", "")]
    [InlineData("boat", "Max Passengers", "1", "Type (pax/cargo/...)")]
    [InlineData("", "Max Passengers", "1", "Type (pax/cargo/...)")]
    public void A_fleet_needs_the_capacity_columns_of_its_type(string type, string field, string value, string flagged)
    {
        var fleet = Row(("Name", "F"), ("Type Code", "B738"), ("Type (pax/cargo/...)", type), ("Max Passengers", "100"), ("Max Freight", "1000"), ("Container Units", "2"));
        fleet.Fields[field] = value;

        var issues = Check(new Workspace(), ResourceKind.Fleets, fleet);

        Assert.Equal(flagged == "" ? [] : [flagged], Fields(issues));
    }

        [Fact]
    public void An_aircraft_needs_exactly_one_known_fleet()
    {
        var w = Base();
        string[] Run(string fleet) => Fields(Check(w, ResourceKind.Aircraft, Row(("Name", "P"), ("Registration", "B-1"), ("Fleet ID", fleet))));
        w.Resources[ResourceKind.Aircraft].Draft = [];

        Assert.Equal([], Run("1"));
        w.Resources[ResourceKind.Aircraft].Draft = [];
        Assert.Equal(["Fleet ID"], Run(""));
        w.Resources[ResourceKind.Aircraft].Draft = [];
        Assert.Equal(["Fleet ID"], Run("1,2"));
        w.Resources[ResourceKind.Aircraft].Draft = [];
        Assert.Equal(["Fleet ID"], Run("9"));
    }

    [Fact]
    public void Required_columns_must_not_be_blank()
    {
        var issues = Check(new Workspace(), ResourceKind.Airports, Row(("ICAO/IATA", "ZBAA"), ("Name", "  ")));

        Assert.Equal(["Name"], Fields(issues));
    }

    [Fact]
    public void Duplicate_keys_are_flagged_on_every_row_ignoring_case_and_padding()
    {
        var w = new Workspace();
        var a = Row(("ICAO/IATA", "ZBAA"), ("Name", "A"));
        var b = Row(("ICAO/IATA", " zbaa "), ("Name", "B"));

        var issues = Check(w, ResourceKind.Airports, a, b);

        Assert.Equal(new[] { a.LocalId, b.LocalId }.Order(), issues.Where(i => i.Field == "ICAO/IATA").Select(i => i.RowId).Order());
    }

    [Fact]
    public void Routing_endpoints_cannot_change_and_must_reference_existing_airports()
    {
        var w = Base();
        CsvAdapter.Import(ResourceKind.Routings, w.Resources[ResourceKind.Routings], [Row(("ID", "1"), (Dep, "ZBAA"), (Arr, "ZSPD"), ("Route String", "DCT"))], true);
        var data = w.Resources[ResourceKind.Routings];
        data.ReplaceDraftRows([data.Draft[0].With(Arr, "EGLL")]);

        var issues = Schemas.Validate(w, ResourceKind.Routings);

        Assert.Contains(issues, i => i.Field == Arr && i.Description.Code == EndpointImmutable);
        Assert.Contains(issues, i => i.Field == Arr && i.Description.Code == UnknownReference);
        Assert.DoesNotContain(issues, i => i.Field == Dep);
    }

    [Fact]
    public void A_new_row_may_not_choose_its_own_numeric_id_and_an_existing_row_may_not_change_it()
    {
        var w = Base();
        var created = Row(("ID", "55"), ("Name", "New"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "cargo"), ("Max Freight", "1"));
        Assert.Equal(["ID"], Fields(Check(w, ResourceKind.Fleets, created)));

        var w2 = Base();
        var fleets = w2.Resources[ResourceKind.Fleets];
        fleets.ReplaceDraftRows([fleets.Draft[0].With("ID", "2")]);
        Assert.Equal(["ID"], Fields(Schemas.Validate(w2, ResourceKind.Fleets)));
    }

    [Fact]
    public void Deleting_is_only_valid_for_rows_that_exist_remotely()
    {
        var w = Base();
        var airports = w.Resources[ResourceKind.Airports];
        airports.Draft = [.. airports.Draft, Row(("ICAO/IATA", "EGLL"), ("Name", "New"), ("_delete", "TRUE"))];
        airports.ReplaceDraftRows([airports.Draft[0].With("_delete", "TRUE")]);

        var issues = Schemas.Validate(w, ResourceKind.Airports);

        Assert.Equal([airports.Draft[2].LocalId], issues.Where(i => i.Field == "ICAO/IATA").Select(i => i.RowId));
    }

    [Fact]
    public void Deleting_a_row_that_other_rows_still_use_is_an_issue_on_the_deleted_row()
    {
        var w = Base();
        var fleets = w.Resources[ResourceKind.Fleets];
        w.Resources[ResourceKind.Aircraft].Draft = [Row(("Name", "P"), ("Registration", "B-1"), ("Fleet ID", "1"))];
        fleets.ReplaceDraftRows([fleets.Draft[0].With("_delete", "TRUE")]);

        var issues = Schemas.Validate(w, ResourceKind.Fleets);

        Assert.Contains(issues, i => i.Field == "_delete" && i.RowId == fleets.Draft[0].LocalId);
    }

    [Theory]
    [InlineData(RunMode.Online, "5000", false)]
    [InlineData(RunMode.Online, "12.5", true)]
    [InlineData(RunMode.Online, "-1", true)]
    [InlineData(RunMode.Online, "99999999999999999999", true)]
    [InlineData(RunMode.Offline, "12.5", false)]
    [InlineData(RunMode.Demo, "12.5", false)]
    public void Cargo_capacity_must_be_whole_kilograms_when_online(RunMode mode, string freight, bool flagged)
    {
        var fleet = Row(("Name", "F"), ("Type Code", "B77F"), ("Type (pax/cargo/...)", "cargo"), ("Max Freight", freight));

        var issues = Check(new Workspace { Mode = mode }, ResourceKind.Fleets, fleet);

        Assert.Equal(flagged, issues.Any(i => i.Field == "Max Freight" && i.Description.Code == "WeightIntegerKg"));
    }

    [Fact]
    public void Two_new_rows_that_look_like_the_same_record_are_flagged_as_possible_duplicates_but_existing_rows_are_not()
    {
        var w = new Workspace();
        var a = Row(("Name", "F"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "cargo"), ("Max Freight", "1"));
        var b = a.Copy();
        b.LocalId = Guid.NewGuid();

        var issues = Check(w, ResourceKind.Fleets, a, b);

        Assert.Equal(new[] { a.LocalId, b.LocalId }.Order(), issues.Where(i => i.Field == "ID").Select(i => i.RowId).Order());

        var existing = Base();
        var fleets = existing.Resources[ResourceKind.Fleets];
        var again = fleets.Draft[0].Without("ID");
        again.LocalId = Guid.NewGuid();
        fleets.Draft = [.. fleets.Draft, again];
        Assert.Equal([again.LocalId], Schemas.Validate(existing, ResourceKind.Fleets).Where(i => i.Field == "ID").Select(i => i.RowId));
    }

    [Fact]
    public void Twenty_thousand_unchanged_routes_validate_and_plan_clean()
    {
        // Correctness at scale only. Speed is measured by `--benchmark-core` (I14), not asserted in a unit test.
        var w = Base();
        var routes = Enumerable.Range(0, 20000).Select(i => Row(("ID", i.ToString()), (Dep, "ZBAA"), (Arr, "ZSPD"), ("Type", "jumpseat"), ("Callsign", "ACA123"), ("Flight Number", "AC123"))).ToList();
        CsvAdapter.Import(ResourceKind.Routes, w.Resources[ResourceKind.Routes], routes, true);

        Assert.Empty(new ChangePlanner().Plan(ResourceKind.Routes, w.Resources[ResourceKind.Routes]));
        Assert.Empty(Schemas.Validate(w, ResourceKind.Routes));
    }
}
