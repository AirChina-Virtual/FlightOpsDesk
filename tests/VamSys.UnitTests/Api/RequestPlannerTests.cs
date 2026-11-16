using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Api;

/// <summary>Change -> request steps, one resource at a time (the RequestPlanner of each resource).</summary>
public class RequestPlannerTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    static ApiKit Kit()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        return kit;
    }

    static ApiStep Only(List<ApiStep> steps) => Assert.Single(steps);

    static void Is(ApiStep step, HttpMethod method, string path, string body)
    {
        Assert.Equal(method, step.Method);
        Assert.Equal(path, step.Path);
        Assert.Equal(body, ApiKit.Body(step));
    }

    // ---- shared rules ----------------------------------------------------------------------------------------

    [Fact]
    public void A_delete_is_a_single_DELETE_without_a_body()
    {
        var kit = Kit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        Is(Only(kit.Api.Plan(ApiKit.Delete(ResourceKind.Airports, airport))), HttpMethod.Delete, "airports/101", "{}");
    }

    [Fact]
    public void An_update_of_a_row_that_never_came_from_the_server_needs_a_refresh_first()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "x" } }, ("Name", "y"));
        Assert.Equal("ApiRefreshFirst", ApiKit.BlockedWith(() => kit.Api.Plan(item)));
    }

    [Fact]
    public void A_create_that_names_an_id_without_a_remote_id_needs_a_refresh_first()
    {
        var kit = Kit();
        Assert.Equal("ApiRefreshFirst", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Create(ResourceKind.Fleets, ("ID", "4"), ("Name", "x")))));
    }

    [Fact]
    public void A_row_of_another_airline_is_never_planned()
    {
        var kit = Kit();
        var foreign = new DataRow { Identity = new("101", "9"), Fields = new Dictionary<string, string> { ["Name"] = "x" } };
        Assert.Equal("ApiWrongVa", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, foreign, ("Name", "y")))));
    }

    [Fact]
    public void An_update_without_changes_is_refused()
    {
        var kit = Kit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        var item = ApiKit.Update(ResourceKind.Airports, airport);
        Assert.Equal("ApiNoChanges", ApiKit.BlockedWith(() => kit.Api.Plan(item)));
    }

    [Fact]
    public void Fields_marked_unspecified_and_the_delete_marker_are_not_sent()
    {
        var kit = Kit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        var item = ApiKit.Update(ResourceKind.Airports, airport, ("Name", "Heathrow"));
        item.Fields["Category"] = new(FieldIntent.Unspecified, null);
        item.Fields["_delete"] = new(FieldIntent.Set, "FALSE");
        Is(Only(kit.Api.Plan(item)), HttpMethod.Put, "airports/101", """{"name":"Heathrow"}""");
    }

    [Fact]
    public void The_id_column_can_never_be_sent()
    {
        var kit = Kit();
        var fleet = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        Assert.Equal("ApiImmutable", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, ("ID", "9")))));
    }

    [Fact]
    public void A_column_without_an_api_field_blocks_but_an_unknown_column_is_ignored()
    {
        var kit = Kit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        Assert.Equal("ApiUnsupported", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, airport, ("Pax LF ID", "3")))));
        Assert.Equal("ApiNoChanges", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, airport, ("Mystery column", "3")))));
    }

    [Fact]
    public void Clearing_a_field_is_refused_unless_the_field_clears_to_an_empty_array()
    {
        var kit = Kit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        Assert.Equal("ApiClear", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, airport, ("Name", "")))));
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, airport, ("Container IDs", "")))), HttpMethod.Put, "airports/101", """{"container_ids":[]}""");
    }

    [Fact]
    public void Empty_values_of_a_create_are_left_out()
    {
        var kit = Kit();
        var steps = kit.Api.Plan(ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "Stansted"), ("Category", ""), ("Container IDs", "")));
        Is(Only(steps), HttpMethod.Post, "airports", """{"icao_iata":"EGSS","name":"Stansted"}""");
    }

    [Fact]
    public void Id_lists_are_trimmed_distinct_integers_and_must_be_positive()
    {
        var kit = Kit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, airport, ("Container IDs", "2, 3,3")))), HttpMethod.Put, "airports/101", """{"container_ids":[2,3]}""");
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, airport, ("Container IDs", "0")))));
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, airport, ("Container IDs", "x")))));
    }

    // ---- airports --------------------------------------------------------------------------------------------

    [Fact]
    public void Airport_create_sends_the_code_as_icao_iata()
    {
        var kit = Kit();
        Is(Only(kit.Api.Plan(ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "Stansted")))), HttpMethod.Post, "airports", """{"icao_iata":"EGSS","name":"Stansted"}""");
    }

    [Fact]
    public void Airport_code_cannot_change_after_creation()
    {
        var kit = Kit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        Assert.Equal("ApiImmutable", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, airport, ("ICAO/IATA", "EGLC")))));
        Assert.Equal("ICAO/IATA", ApiKit.BlockedField(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, airport, ("ICAO/IATA", "EGLC")))));
    }

    [Fact]
    public void Airport_update_sends_only_the_changed_fields()
    {
        var kit = Kit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, airport, ("Name", "Heathrow")))), HttpMethod.Put, "airports/101", """{"name":"Heathrow"}""");
    }

    // ---- fleets ----------------------------------------------------------------------------------------------

    static ChangeItem NewFleet(params (string, string)[] extra)
        => ApiKit.Create(ResourceKind.Fleets, [("Name", "New fleet"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"), .. extra]);

    [Fact]
    public void An_online_fleet_create_must_carry_an_airline_parameter()
    {
        var kit = Kit();
        var item = NewFleet();
        item.RequiresAirlineParameter = true;
        Assert.Equal("ApiParameterRequired", ApiKit.BlockedWith(() => kit.Api.Plan(item)));
        var offline = NewFleet();
        Is(Only(kit.Api.Plan(offline)), HttpMethod.Post, "fleet", """{"name":"New fleet","code":"B738","type":"pax","max_pax":189}""");
    }

    [Fact]
    public void The_airline_parameter_is_sent_as_a_one_element_string_array()
    {
        var kit = Kit();
        var item = NewFleet(("Parameter IDs", "1"));
        item.RequiresAirlineParameter = true;
        Is(Only(kit.Api.Plan(item)), HttpMethod.Post, "fleet", """{"name":"New fleet","code":"B738","type":"pax","max_pax":189,"parameter_ids":["1"]}""");
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(NewFleet(("Parameter IDs", "1,2")))));
    }

    [Fact]
    public void The_airline_parameter_cannot_change_on_an_existing_fleet()
    {
        var kit = Kit();
        var fleet = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        Assert.Equal("ApiParameterCreateOnly", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, ("Parameter IDs", "2")))));
    }

    [Theory]
    [InlineData("Max Passengers", "abc")]
    [InlineData("Max Passengers", "-1")]
    [InlineData("Max Freight", "100.5")]
    [InlineData("Hide in Phoenix", "maybe")]
    public void Numbers_and_booleans_that_do_not_parse_are_refused_with_their_column(string column, string value)
    {
        var kit = Kit();
        var fleet = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, (column, value)))));
        Assert.Equal(column, ApiKit.BlockedField(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, (column, value)))));
    }

    [Fact]
    public void Numbers_are_sent_as_numbers_and_booleans_as_booleans()
    {
        var kit = Kit();
        var fleet = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, ("Max Passengers", "180"), ("Hide in Phoenix", "TRUE")))), HttpMethod.Put, "fleet/4", """{"max_pax":180,"hide_in_phoenix":true}""");
    }

    [Fact]
    public void A_fleet_value_outside_the_contract_never_leaves_the_machine()
    {
        var kit = Kit();
        var fleet = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        // type must be one of the documented values
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, ("Type (pax/cargo/...)", "submarine")))));
    }

    // ---- aircraft --------------------------------------------------------------------------------------------

    [Fact]
    public void Aircraft_create_goes_to_the_fleet_and_keeps_the_fleet_out_of_the_body()
    {
        var kit = Kit();
        var steps = kit.Api.Plan(ApiKit.Create(ResourceKind.Aircraft, ("Name", "Plane Two"), ("Registration", "QA-TEST"), ("Fleet ID", "4"), ("Freight", "2205")));
        Is(Only(steps), HttpMethod.Post, "fleet/4/aircraft", """{"name":"Plane Two","registration":"QA-TEST","cargo":2205}""");
    }

    [Fact]
    public void Aircraft_create_needs_a_positive_fleet_id()
    {
        var kit = Kit();
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Create(ResourceKind.Aircraft, ("Name", "P"), ("Registration", "QA-1"), ("Fleet ID", "abc")))));
    }

    [Fact]
    public void Aircraft_update_addresses_the_aircraft_under_its_fleet()
    {
        var kit = Kit();
        var aircraft = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Name", "Renamed")))), HttpMethod.Put, "fleet/4/aircraft/8", """{"name":"Renamed"}""");
    }

    [Fact]
    public void Changing_the_fleet_of_an_aircraft_is_a_patch_with_nothing_else()
    {
        var kit = Kit();
        var aircraft = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", "5")))), HttpMethod.Patch, "aircraft/8", """{"fleet_id":5}""");
        Assert.Equal("ApiMoveSeparate", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", "5"), ("Name", "Renamed")))));
    }

    [Fact]
    public void A_move_to_the_same_fleet_changes_nothing()
    {
        var kit = Kit();
        var aircraft = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        Assert.Equal("ApiNoChanges", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", "4")))));
    }

    [Fact]
    public void A_move_needs_a_target_fleet_that_is_known_and_not_deleted()
    {
        var kit = Kit();
        var aircraft = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        Assert.Equal("ApiMoveTarget", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", "99")))));
        var deleted = kit.Workspace.Resources[ResourceKind.Fleets].Draft.Single(r => r.Identity?.RemoteId == "5").With("_delete", "TRUE");
        kit.Workspace.Resources[ResourceKind.Fleets].ReplaceDraftRows([deleted]);
        Assert.Equal("ApiMoveTarget", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", "5")))));
    }

    [Fact]
    public void A_move_whose_fleet_field_disagrees_with_the_row_needs_a_fix_before_planning()
    {
        var kit = Kit();
        var aircraft = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        var item = ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", "5"));
        item.Fields["Fleet ID"] = new(FieldIntent.Set, "6");
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(item)));
        Assert.Equal("Fleet ID", ApiKit.BlockedField(() => kit.Api.Plan(item)));
    }

    [Fact]
    public void A_preview_stands_a_placeholder_in_for_a_fleet_that_does_not_exist_yet()
    {
        var kit = Kit();
        var aircraft = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        var local = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "New fleet" } };
        kit.Workspace.Resources[ResourceKind.Fleets].Draft.Add(local);
        var item = ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", LocalReference.Format(local.LocalId)));
        Is(Only(kit.Api.PreviewPlan(item)), HttpMethod.Patch, "aircraft/8", """{"fleet_id":1}""");
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(item)));
    }

    // ---- routings ---------------------------------------------------------------------------------------------

    [Fact]
    public void Routing_create_resolves_airport_codes_to_remote_ids()
    {
        var kit = Kit();
        var steps = kit.Api.Plan(ApiKit.Create(ResourceKind.Routings, (Dep, "EGLL"), (Arr, "lgw"), ("Route String", "DCT"), ("Tags", "a, b,a")));
        Is(Only(steps), HttpMethod.Post, "routings", """{"departure_airport_id":101,"arrival_airport_id":102,"route":"DCT","tag":["a","b"]}""");
    }

    [Fact]
    public void An_airport_code_must_name_exactly_one_airport()
    {
        var kit = Kit();
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Create(ResourceKind.Routings, (Dep, "ZZZZ"), (Arr, "EGKK"), ("Route String", "DCT")))));
        Assert.Equal("ZZZZ", ApiKit.BlockedField(() => kit.Api.Plan(ApiKit.Create(ResourceKind.Routings, (Dep, "ZZZZ"), (Arr, "EGKK"), ("Route String", "DCT")))));
        kit.Map(ResourceKind.Airports, """{"id":104,"airline_id":7,"icao":"EGLC","iata":"LHR","name":"London City"}""");
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Create(ResourceKind.Routings, (Dep, "LHR"), (Arr, "EGKK"), ("Route String", "DCT")))));
        Is(Only(kit.Api.Plan(ApiKit.Create(ResourceKind.Routings, (Dep, "104"), (Arr, "EGKK"), ("Route String", "DCT")))), HttpMethod.Post, "routings", """{"departure_airport_id":104,"arrival_airport_id":102,"route":"DCT"}""");
    }

    [Fact]
    public void Routing_airports_cannot_change_after_creation()
    {
        var kit = Kit();
        var routing = kit.Map(ResourceKind.Routings, ApiKit.Routing9);
        Assert.Equal("ApiImmutable", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routings, routing, (Dep, "EGKK")))));
        Assert.Equal(Dep, ApiKit.BlockedField(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routings, routing, (Dep, "EGKK")))));
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Routings, routing, ("Remarks", "x")))), HttpMethod.Put, "routings/9", """{"remarks":"x"}""");
    }

    // ---- routes ----------------------------------------------------------------------------------------------

    [Fact]
    public void Scheduled_route_create_sends_airports_fleets_and_times_in_hh_mm_ss()
    {
        var kit = Kit();
        var item = ApiKit.Create(ResourceKind.Routes, ("Type", "scheduled"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO200"), ("Flight Number", "CA200"),
            ("Fleet IDs", "4,5"), ("Departure Time (HH:MM)", "08:30"), ("Arrival Time (HH:MM)", "10:15"));
        Is(Only(kit.Api.Plan(item)), HttpMethod.Post, "routes",
            """{"type":"scheduled","departure_id":101,"arrival_id":102,"callsign":"CAO200","flight_number":"CA200","fleet_ids":[4,5],"departure_time":"08:30:00","arrival_time":"10:15:00"}""");
    }

    [Fact]
    public void Jumpseat_create_needs_no_fleets()
    {
        var kit = Kit();
        // The contract asks for a callsign and flight number on every route type, jumpseats included.
        var steps = kit.Api.Plan(ApiKit.Create(ResourceKind.Routes, ("Type", "jumpseat"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"), ("Remarks", "seat")));
        Is(Only(steps), HttpMethod.Post, "routes", """{"type":"jumpseat","departure_id":101,"arrival_id":102,"callsign":"CAO1234","flight_number":"CA1234","remarks":"seat"}""");
        // the server drops a jumpseat's fleets (2026-11-12): they are refused instead of sent
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Create(ResourceKind.Routes, ("Type", "jumpseat"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"), ("Fleet IDs", "4")))));
    }

    // The create schema requires callsign and flight number; the server keeps neither for a jumpseat. Left empty, they go
    // out as placeholders: the letters most of the airline's routes start that field with, then "000".
    [Fact]
    public void A_jumpseat_created_without_callsign_and_flight_number_sends_placeholders()
    {
        var kit = Kit();
        var item = ApiKit.Create(ResourceKind.Routes, ("Type", "jumpseat"), (Dep, "EGLL"), (Arr, "EGKK"));
        Is(Only(kit.Api.Plan(item)), HttpMethod.Post, "routes", """{"type":"jumpseat","departure_id":101,"arrival_id":102,"callsign":"JMP000","flight_number":"JMP000"}""");
        foreach (var json in new[] { ApiKit.Scheduled10, ApiKit.Scheduled12, ApiKit.Jumpseat11,
            """{"id":13,"departure_id":101,"arrival_id":102,"type":"cargo","callsign":"CKK200","flight_number":"KK200","fleet_ids":[4]}""" })
            kit.Workspace.Resources[ResourceKind.Routes].Snapshot.Add(kit.Map(ResourceKind.Routes, json));
        Is(Only(kit.Api.Plan(item)), HttpMethod.Post, "routes", """{"type":"jumpseat","departure_id":101,"arrival_id":102,"callsign":"CAO000","flight_number":"CA000"}""");
        // one that is filled in is sent as it is; other types still need both
        Is(Only(kit.Api.Plan(ApiKit.Create(ResourceKind.Routes, ("Type", "jumpseat"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO1234")))),
            HttpMethod.Post, "routes", """{"type":"jumpseat","departure_id":101,"arrival_id":102,"callsign":"CAO1234","flight_number":"CA000"}""");
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Create(ResourceKind.Routes, ("Type", "scheduled"), (Dep, "EGLL"), (Arr, "EGKK"), ("Fleet IDs", "4")))));
    }

    [Fact]
    public void A_scheduled_create_without_fleets_is_refused()
    {
        var kit = Kit();
        var item = ApiKit.Create(ResourceKind.Routes, ("Type", "scheduled"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO200"), ("Flight Number", "CA200"));
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(item)));
        Assert.Equal("Fleet IDs", ApiKit.BlockedField(() => kit.Api.Plan(item)));
    }

    [Theory]
    [InlineData("Type", "ferry", "Type")]
    [InlineData("Callsign", "CA1", "Callsign")]
    [InlineData("Callsign", "CAO12345", "Callsign")]
    [InlineData("Flight Number", "C", "Flight Number")]
    [InlineData("Flight Number", "CA12345", "Flight Number")]
    [InlineData("Cost Index", "cheap", "Cost Index")]
    public void Route_values_outside_the_documented_limits_are_refused(string column, string value, string reported)
    {
        var kit = Kit();
        var fields = new Dictionary<string, string>
        {
            ["Type"] = "scheduled", [Dep] = "EGLL", [Arr] = "EGKK", ["Callsign"] = "CAO200", ["Flight Number"] = "CA200", ["Fleet IDs"] = "4"
        };
        fields[column] = value;
        var item = ApiKit.Create(ResourceKind.Routes, fields.Select(p => (p.Key, p.Value)).ToArray());
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(item)));
        Assert.Equal(reported, ApiKit.BlockedField(() => kit.Api.Plan(item)));
    }

    [Fact]
    public void An_end_date_must_be_after_the_start_date_and_dates_go_out_as_iso_8601_utc()
    {
        var kit = Kit();
        (string, string)[] Fields(string start, string end) =>
            [("Type", "scheduled"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO200"), ("Flight Number", "CA200"), ("Fleet IDs", "4"), ("Start Date", start), ("End Date", end)];
        Assert.Equal("End Date", ApiKit.BlockedField(() => kit.Api.Plan(ApiKit.Create(ResourceKind.Routes, Fields("2026-10-02 00:00:00", "2026-10-01 00:00:00")))));
        var steps = kit.Api.Plan(ApiKit.Create(ResourceKind.Routes, Fields("2026-10-01 00:00:00", "2026-10-02 12:30:00")));
        Assert.Contains("\"start_date\":\"2026-10-01T00:00:00\\u002B00:00\",\"end_date\":\"2026-10-02T12:30:00\\u002B00:00\"", ApiKit.Body(Only(steps)), StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduled_fleet_sets_can_be_replaced_but_not_on_jumpseats_or_when_the_type_changes()
    {
        var kit = Kit();
        var scheduled = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, scheduled, ("Fleet IDs", "5")))), HttpMethod.Put, "routes/10", """{"fleet_ids":[5]}""");
        var jumpseat = kit.Map(ResourceKind.Routes, ApiKit.Jumpseat11);
        Assert.Equal("ApiFleetSet", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, jumpseat, ("Fleet IDs", "4")))));
        Assert.Equal("ApiFleetSet", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, scheduled, ("Fleet IDs", "5"), ("Type", "jumpseat")))));
    }

    [Fact]
    public void A_jumpseat_keeps_its_identity_and_its_type()
    {
        var kit = Kit();
        var jumpseat = kit.Map(ResourceKind.Routes, ApiKit.Jumpseat11);
        Assert.Equal("ApiJumpseatIdentity", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, jumpseat, ("Callsign", "CAO999")))));
        Assert.Equal("ApiJumpseatIdentity", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, jumpseat, ("Flight Number", "CA999")))));
        Assert.Equal("ApiJumpseat", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, jumpseat, ("Type", "scheduled")))));
        var scheduled = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        Assert.Equal("ApiJumpseat", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, scheduled, ("Type", "jumpseat")))));
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, jumpseat, ("Remarks", "changed")))), HttpMethod.Put, "routes/11", """{"remarks":"changed"}""");
    }

    [Fact]
    public void Route_airports_cannot_change_after_creation()
    {
        var kit = Kit();
        var scheduled = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        Assert.Equal("ApiImmutable", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, scheduled, (Arr, "EGLL")))));
    }

    [Fact]
    public void A_time_edited_in_hh_mm_keeps_the_seconds_the_server_stored()
    {
        var kit = Kit();
        var route = kit.Map(ResourceKind.Routes, ApiKit.Scheduled12);
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, route, ("Departure Time (HH:MM)", "00:10")))), HttpMethod.Put, "routes/12", """{"departure_time":"00:10:37"}""");
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, route, ("Departure Time (HH:MM)", "00:10:15")))), HttpMethod.Put, "routes/12", """{"departure_time":"00:10:15"}""");
        var without = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, without, ("Departure Time (HH:MM)", "10:00")))), HttpMethod.Put, "routes/10", """{"departure_time":"10:00:00"}""");
    }

    [Fact]
    public void A_date_edited_keeps_the_fraction_of_a_second_the_server_stored()
    {
        var kit = Kit();
        var route = kit.Map(ResourceKind.Routes, ApiKit.Scheduled12);
        var steps = kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, route, ("Start Date", "2026-11-01 00:00:00")));
        Is(Only(steps), HttpMethod.Put, "routes/12", """{"start_date":"2026-11-01T00:00:00.1234567\u002B00:00"}""");
    }

    [Theory]
    [InlineData("Departure Time (HH:MM)", "25:00")]
    [InlineData("Departure Time (HH:MM)", "noon")]
    public void A_time_that_does_not_parse_is_refused_with_its_column(string column, string value)
    {
        var kit = Kit();
        var route = kit.Map(ResourceKind.Routes, ApiKit.Scheduled12);
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, route, (column, value)))));
        Assert.Equal(column, ApiKit.BlockedField(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, route, (column, value)))));
    }

    [Fact]
    public void Local_time_routes_cannot_send_times_or_dates()
    {
        var kit = Kit();
        kit.Workspace.LocalRouteTimes = true;
        var route = kit.Map(ResourceKind.Routes, ApiKit.Scheduled12);
        Assert.Equal("ApiUtc", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, route, ("Departure Time (HH:MM)", "10:00")))));
        Assert.Equal("ApiUtc", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, route, ("Start Date", "2026-11-01 00:00:00")))));
        Assert.Equal("ApiUtc", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, route, ("Start Date", "not a date")))));
    }

    // ---- columns that belong to another resource ---------------------------------------------------------------

    [Fact]
    public void A_special_column_of_another_resource_keeps_the_treatment_it_always_had()
    {
        // An unknown CSV column can carry the name of another resource's key, parent or endpoint column.
        var kit = Kit();
        var fleet = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        Assert.Equal("ApiImmutable", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, ("ICAO/IATA", "EGLL")))));
        Assert.Equal("ApiMoveFleet", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, ("Fleet ID", "5")))));
        Assert.Equal("ApiImmutable", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, (Dep, "EGLL")))));
        Assert.Equal(Dep, ApiKit.BlockedField(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, (Dep, "EGLL")))));
        // On a create the extra column reaches the body, and the contract check rejects the unknown property.
        var create = NewFleet(("ICAO/IATA", "EGLL"));
        Assert.Equal("ApiUnsupported", ApiKit.BlockedWith(() => kit.Api.Plan(create)));
    }

    // ---- saved plans -----------------------------------------------------------------------------------------

    [Fact]
    public void Expected_values_are_the_union_of_the_steps_with_the_last_value_winning()
    {
        var steps = new List<ApiStep>
        {
            new(HttpMethod.Post, "fleet", new() { ["name"] = "a", ["max_pax"] = 1L }),
            new(HttpMethod.Put, "{created}", new() { ["name"] = "b", ["tag"] = new[] { "x" } })
        };
        var expected = OperationsAdapter.ExpectedValues(steps);
        Assert.Equal("b", expected["name"].GetString());
        Assert.Equal(1, expected["max_pax"].GetInt64());
        Assert.Equal("x", expected["tag"][0].GetString());
    }

    [Fact]
    public void A_resumed_item_is_only_trusted_when_its_saved_steps_still_match_its_plan()
    {
        var kit = Kit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        var item = ApiKit.Update(ResourceKind.Airports, airport, ("Name", "Heathrow"));
        OperationsAdapter.PrepareSteps(item, kit.Api.Plan(item));
        // unchanged plan: the saved steps are returned
        var saved = Only(kit.Api.ExecutionPlan(item));
        Is(saved, HttpMethod.Put, "airports/101", """{"name":"Heathrow"}""");
        // the row was edited since: the saved plan is stale
        var edited = ApiKit.Update(ResourceKind.Airports, airport, ("Name", "Another"));
        edited.ApiSteps = item.ApiSteps;
        Assert.Equal("ApiRefreshFirst", ApiKit.BlockedWith(() => kit.Api.ExecutionPlan(edited)));
        // a saved path that no longer matches the record
        item.ApiSteps[0].Path = "airports/999";
        Assert.Equal("ApiRefreshFirst", ApiKit.BlockedWith(() => kit.Api.ExecutionPlan(item)));
    }

    [Fact]
    public void A_saved_step_with_values_the_plan_no_longer_has_is_stale()
    {
        var kit = Kit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        var item = ApiKit.Update(ResourceKind.Airports, airport, ("Name", "Heathrow"));
        OperationsAdapter.PrepareSteps(item, kit.Api.Plan(item));
        item.ApiSteps[0].Body["weight_unit"] = JsonSerializer.SerializeToElement("lb");
        Assert.Equal("ApiRefreshFirst", ApiKit.BlockedWith(() => kit.Api.ExecutionPlan(item)));
    }

    [Fact]
    public void Preview_plans_follow_the_same_rules_as_plans()
    {
        var kit = Kit();
        var item = ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "Stansted"));
        Is(Only(kit.Api.PreviewPlan(item)), HttpMethod.Post, "airports", """{"icao_iata":"EGSS","name":"Stansted"}""");
        Assert.Equal("ApiNoChanges", ApiKit.BlockedWith(() => kit.Api.PreviewPlan(ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll)))));
    }

    // ---- C11: assertions of the C3 scenarios that were only matched by test name ----------------------------------

    [Fact]
    public void An_aircraft_create_for_a_fleet_that_exists_only_locally_previews_but_is_not_planned()
    {
        var kit = Kit();
        var local = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "New", ["Type Code"] = "B738", ["Type (pax/cargo/...)"] = "pax" } };
        kit.Workspace.Resources[ResourceKind.Fleets].Draft.Add(local);
        var item = ApiKit.Create(ResourceKind.Aircraft, ("Name", "Plane"), ("Registration", "G-TEST"), ("Fleet ID", LocalReference.Format(local.LocalId)));
        Assert.Single(kit.Api.PreviewPlan(item));
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(item)));
    }

    [Fact]
    public void A_create_leaves_out_a_column_no_resource_knows()
    {
        var kit = Kit();
        var item = NewFleet(("unknown", "keep"));
        var step = Only(kit.Api.Plan(item));
        Assert.Equal(HttpMethod.Post, step.Method);
        Assert.Equal("fleet", step.Path);
        Assert.Equal(["name", "code", "type", "max_pax"], step.Body.Keys);
        Assert.Equal(189L, step.Body["max_pax"]);
    }

    [Fact]
    public void A_fleet_type_code_outside_the_contract_never_leaves_the_machine()
    {
        var kit = Kit();
        var fleet = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, ("Type Code", "BAD")))));
        // the contract check names the API property, not the column
        Assert.Equal("code", ApiKit.BlockedField(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Fleets, fleet, ("Type Code", "BAD")))));
    }

    [Fact]
    public void An_airport_renamed_to_the_code_of_another_airport_is_still_addressed_by_its_id()
    {
        var kit = Kit();
        var airport = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        Is(Only(kit.Api.Plan(ApiKit.Update(ResourceKind.Airports, airport, ("Name", "LGW")))), HttpMethod.Put, "airports/101", """{"name":"LGW"}""");
    }

    [Fact]
    public void The_departure_airport_of_a_route_cannot_change_after_creation()
    {
        var kit = Kit();
        var scheduled = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        Assert.Equal("ApiImmutable", ApiKit.BlockedWith(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, scheduled, (Dep, "EGKK")))));
        Assert.Equal(Dep, ApiKit.BlockedField(() => kit.Api.Plan(ApiKit.Update(ResourceKind.Routes, scheduled, (Dep, "EGKK")))));
    }
}
