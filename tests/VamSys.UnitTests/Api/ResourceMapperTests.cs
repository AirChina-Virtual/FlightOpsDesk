using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Api;

/// <summary>JSON from the server -> grid rows, one resource at a time (the ResourceMapper of each resource).</summary>
public class ResourceMapperTests
{
    // ---- airports --------------------------------------------------------------------------------------------

    [Fact]
    public void Airport_is_keyed_by_its_icao_code_and_has_no_id_column()
    {
        var kit = new ApiKit();
        var row = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        Assert.Equal("EGLL", row.Get("ICAO/IATA"));
        Assert.Equal("London", row.Get("Name"));
        Assert.Equal("1", row.Get("Container IDs"));
        Assert.Equal("FALSE", row.Get("_delete"));
        Assert.False(row.Fields.ContainsKey("ID"));
        Assert.False(row.Fields.ContainsKey("Category"));
        Assert.Equal(new ResourceIdentity("101", "7", null), row.Identity);
        Assert.Equal(ApiKit.Egll, row.RawApiJson);
    }

    [Fact]
    public void Airport_without_an_icao_code_is_keyed_by_its_iata_code()
    {
        var kit = new ApiKit();
        Assert.Equal("MAN", kit.Map(ResourceKind.Airports, """{"id":103,"airline_id":7,"iata":"MAN","name":"Manchester"}""").Get("ICAO/IATA"));
        Assert.Equal("MAN", kit.Map(ResourceKind.Airports, """{"id":104,"airline_id":7,"icao":null,"iata":"MAN","name":"Manchester"}""").Get("ICAO/IATA"));
    }

    // ---- fleets and aircraft ----------------------------------------------------------------------------------

    [Fact]
    public void Fleet_maps_every_wire_field_in_the_field_table_and_keeps_the_id()
    {
        var kit = new ApiKit();
        var row = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        Assert.Equal("4", row.Get("ID"));
        Assert.Equal("Fleet 4", row.Get("Name"));
        Assert.Equal("B738", row.Get("Type Code"));
        Assert.Equal("cargo", row.Get("Type (pax/cargo/...)"));
        Assert.Equal("10000", row.Get("Max Freight"));
        Assert.Equal("1", row.Get("Parameter IDs"));
        Assert.Equal(new ResourceIdentity("4", "7", null), row.Identity);
    }

    [Fact]
    public void Fleet_booleans_are_upper_case_and_nulls_are_empty()
    {
        var kit = new ApiKit();
        var row = kit.Map(ResourceKind.Fleets, """{"id":6,"airline_id":7,"name":"F","hide_in_phoenix":true,"max_pax":null}""");
        Assert.Equal("TRUE", row.Get("Hide in Phoenix"));
        Assert.Equal("", row.Get("Max Passengers"));
    }

    [Fact]
    public void Aircraft_carries_its_fleet_in_the_identity_and_in_the_fleet_column()
    {
        var kit = new ApiKit();
        var row = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        Assert.Equal("4", row.Identity!.ParentFleetId);
        Assert.Equal("4", row.Get("Fleet ID"));
        Assert.Equal("B-TEST", row.Get("Registration"));
        Assert.Equal("1000", row.Get("Freight"));
        Assert.Equal("8", row.Get("ID"));
    }

    // ---- routings and routes: endpoint columns ------------------------------------------------------------------

    [Fact]
    public void Routing_shows_the_codes_of_airports_that_are_known()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        var row = kit.Map(ResourceKind.Routings, ApiKit.Routing9);
        Assert.Equal("EGLL", row.Get("Departure Airport (ICAO/IATA)"));
        Assert.Equal("EGKK", row.Get("Arrival Airport (ICAO/IATA)"));
        Assert.Equal("DCT", row.Get("Route String"));
        Assert.Equal("a", row.Get("Tags"));
    }

    [Fact]
    public void Unknown_airports_show_as_unresolved_with_their_id()
    {
        var kit = new ApiKit();
        var row = kit.Map(ResourceKind.Routings, ApiKit.Routing9);
        Assert.Equal("unresolved:101", row.Get("Departure Airport (ICAO/IATA)"));
        Assert.Equal("unresolved:102", row.Get("Arrival Airport (ICAO/IATA)"));
    }

    [Fact]
    public void Route_shows_times_without_seconds_and_dates_in_utc_grid_format()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        var row = kit.Map(ResourceKind.Routes, ApiKit.Scheduled12);
        Assert.Equal("23:59", row.Get("Departure Time (HH:MM)"));
        Assert.Equal("01:20", row.Get("Arrival Time (HH:MM)"));
        Assert.Equal("2026-10-01 00:00:00", row.Get("Start Date"));
        Assert.Equal("4,5", row.Get("Fleet IDs"));
        Assert.Equal("FALSE", row.Get("Is Hidden"));
        Assert.Equal("EGLL", row.Get("Departure Airport (ICAO/IATA)"));
        Assert.Contains("23:59:37", row.RawApiJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_with_an_offset_is_converted_to_utc()
    {
        var kit = new ApiKit();
        var row = kit.Map(ResourceKind.Routes, """{"id":13,"departure_id":1,"arrival_id":2,"type":"scheduled","start_date":"2026-10-01T05:30:00+05:30"}""");
        Assert.Equal("2026-10-01 00:00:00", row.Get("Start Date"));
    }

    [Fact]
    public void A_time_that_is_not_in_hh_mm_ss_form_is_shown_as_the_server_sent_it()
    {
        var kit = new ApiKit();
        var row = kit.Map(ResourceKind.Routes, """{"id":13,"departure_id":1,"arrival_id":2,"type":"scheduled","departure_time":"10:00","start_date":"not a date"}""");
        Assert.Equal("10:00", row.Get("Departure Time (HH:MM)"));
        Assert.Equal("not a date", row.Get("Start Date"));
    }

    // ---- which airline a record belongs to -------------------------------------------------------------------

    [Fact]
    public void The_first_record_binds_an_unbound_workspace_to_its_airline()
    {
        var kit = new ApiKit(airline: null);
        kit.Map(ResourceKind.Airports, ApiKit.Egll);
        Assert.Equal("7", kit.Workspace.AirlineId);
        Assert.Equal("7", kit.Api.SessionAirlineId);
    }

    [Fact]
    public void A_record_of_another_airline_is_refused()
    {
        var kit = new ApiKit();
        Assert.Equal("ApiWrongVa", ApiKit.BlockedWith(() => kit.Map(ResourceKind.Fleets, """{"id":4,"airline_id":8,"name":"Other"}""")));
        Assert.Null(kit.Api.SessionAirlineId);
    }

    [Fact]
    public void A_route_has_no_airline_in_its_record_and_belongs_to_the_connected_airline()
    {
        var kit = new ApiKit();
        var row = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        Assert.Equal("7", row.Identity!.ConnectionId);
    }

    [Fact]
    public void A_route_of_a_workspace_without_an_airline_needs_a_connection_first()
    {
        var kit = new ApiKit(airline: null);
        Assert.Equal("ApiConnectFirst", ApiKit.BlockedWith(() => kit.Map(ResourceKind.Routes, ApiKit.Scheduled10)));
    }

    [Fact]
    public void A_route_of_a_session_that_already_read_an_airline_uses_it()
    {
        var kit = new ApiKit(airline: null);
        kit.Map(ResourceKind.Airports, ApiKit.Egll);
        Assert.Equal("7", kit.Map(ResourceKind.Routes, ApiKit.Scheduled10).Identity!.ConnectionId);
    }

    [Theory]
    [InlineData(ResourceKind.Airports)]
    [InlineData(ResourceKind.Fleets)]
    [InlineData(ResourceKind.Aircraft)]
    [InlineData(ResourceKind.Routings)]
    public void Resources_other_than_routes_must_name_their_airline(ResourceKind kind)
    {
        var kit = new ApiKit();
        var json = kind switch
        {
            ResourceKind.Aircraft => """{"id":8,"fleet_id":4}""",
            ResourceKind.Routings => """{"id":9,"departure_airport_id":1,"arrival_airport_id":2}""",
            _ => """{"id":1,"icao":"EGLL","iata":"LHR"}"""
        };
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Map(kind, json)));
        Assert.Equal("airline_id", ApiKit.BlockedField(() => kit.Map(kind, json)));
    }

    [Fact]
    public void A_null_airline_counts_as_missing()
    {
        var kit = new ApiKit();
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Map(ResourceKind.Fleets, """{"id":4,"airline_id":null,"name":"F"}""")));
    }

    // ---- mapped rows become known ------------------------------------------------------------------------------

    [Fact]
    public async Task Mapped_records_are_known_to_later_lookups_and_Forget_removes_them()
    {
        var kit = new ApiKit();
        kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        // An aircraft can only be looked up after it was read: its fleet is part of the address.
        kit.Server.On(HttpMethod.Get, "fleet/4/aircraft/8", ApiKit.Data(ApiKit.Aircraft8));
        Assert.NotNull(await kit.Api.FindAsync(ResourceKind.Aircraft, "8", TestContext.Current.CancellationToken));
        kit.Api.Forget(ResourceKind.Aircraft, "8");
        await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.FindAsync(ResourceKind.Aircraft, "8", TestContext.Current.CancellationToken));
    }
}
