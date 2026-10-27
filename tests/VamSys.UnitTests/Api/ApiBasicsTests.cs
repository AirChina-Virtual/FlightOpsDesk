using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Api;

/// <summary>Behaviours of the API layer that no narrower test pins down: the frozen contract, airport aliases and route field rules.</summary>
public class ApiBasicsTests
{
    static DataRow Row(params (string, string)[] fields) => new() { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };

    [Fact]
    public void The_official_schema_shipped_with_the_program_is_the_frozen_one()
    {
        using var stream = typeof(OperationsAdapter).Assembly.GetManifestResourceStream("Operations.OpenApi.json")!;
        Assert.Equal(ApiContracts.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)));
    }

    [Fact]
    public void An_airport_csv_import_finds_the_row_by_either_code_and_refuses_two_rows_for_one_airport_atomically()
    {
        var kit = new ApiKit();
        var airport = kit.Map(ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"London"}""");
        var data = new ResourceData { Snapshot = [airport], Draft = [airport] };
        CsvAdapter.Import(ResourceKind.Airports, data, [Row(("ICAO/IATA", "LHR"), ("Name", "Updated"))], false);
        Assert.Single(data.Draft);
        Assert.Equal("EGLL", data.Draft[0].Get("ICAO/IATA"));
        Assert.Equal("Updated", data.Draft[0].Get("Name"));

        var undo = data.Undo.Count;
        Assert.Throws<FormatException>(() => CsvAdapter.Import(ResourceKind.Airports, data, [Row(("ICAO/IATA", "LHR")), Row(("ICAO/IATA", "EGLL"))], false));
        Assert.Equal(undo, data.Undo.Count);
    }

    [Fact]
    public void A_refresh_binds_a_row_saved_under_the_iata_code_to_the_airport_with_that_alias()
    {
        var kit = new ApiKit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        var old = Row(("ICAO/IATA", "LHR"), ("Name", "London"));
        var data = new ResourceData { Snapshot = [old], Draft = [old] };
        SnapshotMerger.Merge(ResourceKind.Airports, data, [airport]);
        Assert.Single(data.Draft);
        Assert.Empty(data.Conflicts);
        Assert.Equal("101", data.Draft[0].Identity!.RemoteId);
    }

    [Fact]
    public void The_raw_api_record_of_a_row_survives_saving_the_workspace()
    {
        var kit = new ApiKit();
        var fleet = kit.Map(ResourceKind.Fleets, """{"id":4,"airline_id":7,"name":"Old","code":"B738","type":"pax"}""");
        var saved = Workspace.Deserialize(new Workspace { Resources = { [ResourceKind.Fleets] = new() { Draft = [fleet] } } }.Serialize());
        Assert.Equal(fleet.RawApiJson, saved.Resources[ResourceKind.Fleets].Draft[0].RawApiJson);
    }

    [Fact]
    public void Route_columns_are_validated_like_the_store_request_does()
    {
        var w = new Workspace();
        w.Resources[ResourceKind.Routes].Draft.Add(Row(("Type", "jumpseat"), ("Departure Airport (ICAO/IATA)", "EGLL"), ("Arrival Airport (ICAO/IATA)", "EGKK"), ("Cost Index", "1000")));
        var issues = Schemas.Validate(w, ResourceKind.Routes).Select(i => i.Field).ToHashSet();
        Assert.Contains("Callsign", issues);
        Assert.Contains("Flight Number", issues);
        Assert.Contains("Cost Index", issues);
        Assert.DoesNotContain("Fleet IDs", issues);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("AUTO", true)]
    [InlineData("0", true)]
    [InlineData("999", true)]
    [InlineData("auto", false)]
    [InlineData("1000", false)]
    [InlineData("-1", false)]
    [InlineData("5.5", false)]
    public void A_cost_index_is_auto_or_a_whole_number_up_to_999(string value, bool valid)
        => Assert.Equal(valid, Schemas.ValidCostIndex(value));

    [Fact]
    public void A_route_create_sends_iso_dates_and_the_auto_cost_index_and_refuses_an_out_of_range_one()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        (string, string)[] Route(params (string, string)[] extra) =>
            new[] { ("Departure Airport (ICAO/IATA)", "LHR"), ("Arrival Airport (ICAO/IATA)", "LGW"), ("Type", "scheduled"), ("Callsign", "ACA123"), ("Flight Number", "AC123"), ("Fleet IDs", "4") }.Concat(extra).ToArray();
        var body = kit.Api.Plan(ApiKit.Create(ResourceKind.Routes, Route(("Start Date", "2025-01-01 00:00:00"), ("Cost Index", "AUTO"))))[0].Body;
        Assert.Equal("2025-01-01T00:00:00+00:00", (string?)body["start_date"]);
        Assert.Equal("AUTO", (string?)body["cost_index"]);
        Assert.ThrowsAny<InvalidOperationException>(() => kit.Api.Plan(ApiKit.Create(ResourceKind.Routes, Route(("Cost Index", "1000")))));
    }
}
