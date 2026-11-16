using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class ReviewSummariesTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    static DataRow Row(params (string, string)[] fields) => new() { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };

    static LocalizationService Language(string culture)
    {
        var language = new LocalizationService();
        language.SetLanguage(culture);
        return language;
    }

    static PlannedChange Create(ResourceKind resource, DataRow after)
        => PlannedChange.Wrap(new ChangeItem { Resource = resource, Kind = ChangeKind.Create, After = after });

    // A deletion's draft row is the snapshot row marked for deletion; the summary reads the snapshot.
    static PlannedChange Delete(ResourceKind resource, DataRow before)
        => PlannedChange.Wrap(new ChangeItem { Resource = resource, Kind = ChangeKind.Delete, Before = before, After = before.With("_delete", "TRUE") });

    static PlannedChange Update(ResourceKind resource, DataRow before, string field, string value)
        => PlannedChange.Wrap(new ChangeItem
        {
            Resource = resource, Kind = ChangeKind.Update, Before = before, After = before.With(field, value),
            Fields = new() { [field] = new(FieldIntent.Set, value) }
        });

    static Workspace WithFleets(params DataRow[] fleets)
    {
        var workspace = new Workspace();
        workspace.Resources[ResourceKind.Fleets].Snapshot = [.. fleets];
        workspace.Resources[ResourceKind.Fleets].Draft = [.. fleets];
        return workspace;
    }

    [Theory]
    [InlineData("en-US", "Delete")]
    [InlineData("zh-CN", "删除")]
    public void An_airport_deletion_reads_its_code_and_name(string culture, string kind)
    {
        var language = Language(culture);
        var change = Delete(ResourceKind.Airports, Row(("ICAO/IATA", "zbaa"), ("Name", "Beijing Capital")));
        Assert.Equal(kind, language.Get("Enum_ChangeKind_Delete"));
        Assert.Equal($"{kind} · ZBAA · Beijing Capital\n{language.Get("Text_5ABD08A949")}", ReviewTexts.ChangeText(change, language, ReviewSummaries.Empty));
    }

    [Fact]
    public void A_fleet_reads_its_name_and_type_code_for_each_kind()
    {
        var language = Language("en-US");
        var fleet = Row(("ID", "32329"), ("Name", "Airbus A320neo"), ("Type Code", "A20N"));
        var summaries = new ReviewSummaries(new Workspace());
        Assert.Equal("Delete · 32329 · Airbus A320neo (A20N)", ReviewTexts.Headline(Delete(ResourceKind.Fleets, fleet), language, summaries));
        Assert.Equal("Create · Airbus A320neo (A20N)", ReviewTexts.Headline(Create(ResourceKind.Fleets, fleet.With("ID", "")), language, summaries));
        var update = ReviewTexts.ChangeText(Update(ResourceKind.Fleets, fleet, "Name", "A320neo"), language, summaries);
        Assert.Equal("Update · 32329 · A320neo (A20N)\nName: Airbus A320neo → A320neo", update);
    }

    [Fact]
    public void An_aircraft_reads_its_registration_name_and_fleet_from_the_workspace()
    {
        var known = Row(("ID", "77"), ("Name", "Boeing 737-800"), ("Type Code", "B738"));
        var created = Row(("ID", ""), ("Name", "New fleet"), ("Type Code", "A21N"));
        var summaries = new ReviewSummaries(WithFleets(known, created));
        var aircraft = Row(("ID", "306150"), ("Registration", "B-1234"), ("Name", "Spirit of Beijing"), ("Fleet ID", "77"));

        var english = Language("en-US");
        Assert.Equal("Delete · 306150 · B-1234 · Spirit of Beijing · Fleet: Boeing 737-800", ReviewTexts.Headline(Delete(ResourceKind.Aircraft, aircraft), english, summaries));
        var chinese = Language("zh-CN");
        Assert.Equal("删除 · 306150 · B-1234 · Spirit of Beijing · 机型：Boeing 737-800", ReviewTexts.Headline(Delete(ResourceKind.Aircraft, aircraft), chinese, summaries));

        // A new aircraft of a fleet that is created in the same submission refers to it locally.
        var pending = Row(("Registration", "B-5678"), ("Name", ""), ("Fleet ID", LocalReference.Format(created.LocalId).ToUpperInvariant()));
        Assert.Equal("Create · B-5678 · Fleet: New fleet", ReviewTexts.Headline(Create(ResourceKind.Aircraft, pending), english, summaries));
        // An unknown fleet shows its reference.
        Assert.Equal("Update · 306150 · B-1234 · Spirit · Fleet: 99",
            ReviewTexts.Headline(Update(ResourceKind.Aircraft, aircraft.With("Fleet ID", "99"), "Name", "Spirit"), english, summaries));
    }

    [Fact]
    public void The_fleet_index_is_built_once()
    {
        var workspace = WithFleets(Row(("ID", "77"), ("Name", "Boeing 737-800")));
        var summaries = new ReviewSummaries(workspace);
        var language = Language("en-US");
        var aircraft = Row(("ID", "1"), ("Registration", "B-1"), ("Fleet ID", "77"));
        Assert.EndsWith("Fleet: Boeing 737-800", summaries.Summary(Delete(ResourceKind.Aircraft, aircraft), language), StringComparison.Ordinal);
        // Later workspace edits do not rebuild the index while the same review is shown.
        workspace.Resources[ResourceKind.Fleets].Draft = [Row(("ID", "77"), ("Name", "Renamed"))];
        Assert.EndsWith("Fleet: Boeing 737-800", summaries.Summary(Delete(ResourceKind.Aircraft, aircraft), language), StringComparison.Ordinal);
    }

    [Fact]
    public void A_routing_reads_its_airports_and_route_string()
    {
        var language = Language("en-US");
        var routing = Row(("ID", "56386"), (Dep, "ZBAA"), (Arr, "ZSPD"), ("Route String", "ELKUR W40 YQG"));
        Assert.Equal("Delete · 56386 · ZBAA → ZSPD · ELKUR W40 YQG", ReviewTexts.Headline(Delete(ResourceKind.Routings, routing), language));
        Assert.Equal("Create · ZBAA → ? · ELKUR W40 YQG", ReviewTexts.Headline(Create(ResourceKind.Routings, routing.With("ID", "").With(Arr, "")), language));
    }

    [Fact]
    public void A_route_reads_its_type_callsign_flight_number_and_airports()
    {
        var language = Language("en-US");
        var route = Row(("ID", "14327601"), ("Type", "scheduled"), ("Callsign", "CCA"), ("Flight Number", "1501"), (Dep, "ZBAA"), (Arr, "ZSSS"));
        Assert.Equal("Delete · 14327601 · scheduled · CCA 1501 · ZBAA → ZSSS", ReviewTexts.Headline(Delete(ResourceKind.Routes, route), language));
        Assert.Equal("Update · 14327601 · scheduled · CCA 1502 · ZBAA → ZSSS",
            ReviewTexts.Headline(Update(ResourceKind.Routes, route, "Flight Number", "1502"), language));
        Assert.Equal("Create · charter · CCA 1501 · ZBAA → ZSSS", ReviewTexts.Headline(Create(ResourceKind.Routes, route.With("ID", "").With("Type", "charter")), language));
    }

    [Fact]
    public void A_jumpseat_route_without_callsign_reads_its_type_and_airports()
    {
        var language = Language("zh-CN");
        var jumpseat = Row(("ID", "14327585"), ("Type", "jumpseat"), ("Callsign", ""), ("Flight Number", ""), (Dep, "ZBAA"), (Arr, "ZGGG"));
        Assert.Equal("删除 · 14327585 · jumpseat · ZBAA → ZGGG", ReviewTexts.Headline(Delete(ResourceKind.Routes, jumpseat), language));
    }

    [Theory]
    [InlineData(ResourceKind.Airports)]
    [InlineData(ResourceKind.Fleets)]
    [InlineData(ResourceKind.Aircraft)]
    [InlineData(ResourceKind.Routings)]
    [InlineData(ResourceKind.Routes)]
    public void A_record_with_empty_fields_shows_only_its_kind_and_key(ResourceKind resource)
    {
        var language = Language("en-US");
        var key = Schemas.All[resource].Key;
        var summaries = new ReviewSummaries(new Workspace());
        Assert.Equal("Delete · 42", ReviewTexts.Headline(Delete(resource, Row((key, "42"), ("Name", " "))), language, summaries));
        Assert.Equal("Create", ReviewTexts.Headline(Create(resource, Row()), language, summaries));
    }

    [Fact]
    public void Long_values_are_trimmed_and_kept_on_one_line()
    {
        var language = Language("en-US");
        var route = string.Join(" ", Enumerable.Range(0, 40).Select(n => "WPT" + n));
        var routing = Row(("ID", "1"), (Dep, "ZBAA"), (Arr, "ZSPD"), ("Route String", route + "\r\nDCT"));
        var line = ReviewTexts.Headline(Delete(ResourceKind.Routings, routing), language);
        Assert.DoesNotContain('\n', line);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
        Assert.True(line.Length < 80, line);

        var text = ReviewTexts.ChangeText(Update(ResourceKind.Routings, routing, "Remarks", new string('x', 500)), language);
        Assert.True(text.Length < 200, text);
    }
}
