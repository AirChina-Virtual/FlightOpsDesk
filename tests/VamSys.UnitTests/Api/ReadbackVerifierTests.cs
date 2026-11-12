using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Api;

/// <summary>
/// Does what the server holds match what was meant to be written (after a write), and does it still match what the
/// change was planned from (before a write)? The answer comes from rows read from the server, never from a write response.
/// </summary>
public class ReadbackVerifierTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    static ApiKit Kit()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        return kit;
    }

    static ReadbackVerification Verify(ApiKit kit, ChangeItem item, DataRow? current)
        => kit.Api.VerifyReadback(item, current, item.After, ApiExpectedFor(kit, item));

    static IReadOnlyDictionary<string, JsonElement> ApiExpectedFor(ApiKit kit, ChangeItem item) => OperationsAdapter.ExpectedValues(kit.Api.Plan(item));

    // ---- after a write -----------------------------------------------------------------------------------------

    [Fact]
    public void A_record_that_carries_the_written_values_passes()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        var current = kit.Map(ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"Heathrow"}""");
        Assert.True(kit.Api.Matches(item, current, item.After, false));
        Assert.Empty(Verify(kit, item, current).Differences);
    }

    [Fact]
    public void A_record_that_does_not_carry_the_written_values_fails()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        var current = kit.Map(ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"Someone else's name"}""");
        Assert.False(kit.Api.Matches(item, current, item.After, false));
    }

    [Fact]
    public void A_value_that_differs_is_listed_with_what_was_expected_and_what_is_there()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        var result = Verify(kit, item, kit.Map(ResourceKind.Airports, ApiKit.Egll));
        Assert.False(result.Passed);
        Assert.Equal(new ApiFieldDifference("name", "\"Heathrow\"", "\"London\""), Assert.Single(result.Differences));
    }

    [Fact]
    public void A_missing_property_counts_as_a_difference()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        var result = Verify(kit, item, kit.Map(ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR"}"""));
        Assert.Equal(new ApiFieldDifference("name", "\"Heathrow\"", null), Assert.Single(result.Differences));
    }

    [Fact]
    public void A_record_that_cannot_be_read_never_passes()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        var result = Verify(kit, item, null);
        Assert.False(result.Passed);
        Assert.Equal(new ApiFieldDifference("record", "101", null), Assert.Single(result.Differences));
        // a row that did not come from the server has no raw record to compare either
        Assert.False(Verify(kit, item, new DataRow()).Passed);
    }

    [Fact]
    public void A_created_airport_must_answer_to_the_code_it_was_created_with_and_it_is_listed_once()
    {
        var kit = Kit();
        var item = ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "Stansted"));
        var current = kit.Map(ResourceKind.Airports, """{"id":105,"airline_id":7,"icao":"EGLC","iata":"LCY","name":"Stansted"}""");
        var result = Verify(kit, item, current);
        Assert.Equal(new ApiFieldDifference("ICAO/IATA", "EGSS", "EGLC,LCY"), Assert.Single(result.Differences));
        var ok = kit.Map(ResourceKind.Airports, """{"id":105,"airline_id":7,"icao":"EGSS","iata":"STN","name":"Stansted"}""");
        Assert.True(Verify(kit, item, ok).Passed);
        // the IATA code is as good as the ICAO code
        var asIata = ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "STN"), ("Name", "Stansted"));
        Assert.True(Verify(kit, asIata, ok).Passed);
    }

    [Fact]
    public void A_created_aircraft_must_sit_in_the_requested_fleet()
    {
        var kit = Kit();
        var item = ApiKit.Create(ResourceKind.Aircraft, ("Name", "P"), ("Registration", "QA-1"), ("Fleet ID", "4"));
        var elsewhere = kit.Map(ResourceKind.Aircraft, """{"id":9,"airline_id":7,"fleet_id":5,"registration":"QA-1","name":"P"}""");
        Assert.Equal(new ApiFieldDifference("fleet_id", "4", "5"), Assert.Single(Verify(kit, item, elsewhere).Differences));
        var there = kit.Map(ResourceKind.Aircraft, """{"id":9,"airline_id":7,"fleet_id":4,"registration":"QA-1","name":"P"}""");
        Assert.True(Verify(kit, item, there).Passed);
    }

    [Fact]
    public void Id_lists_are_compared_as_sets_and_the_server_may_answer_with_string_ids()
    {
        var kit = Kit();
        var route = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        var item = ApiKit.Update(ResourceKind.Routes, route, ("Fleet IDs", "4,5"));
        string With(string ids) => $$"""{"id":10,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO100","flight_number":"CA100","fleet_ids":{{ids}}}""";
        Assert.True(Verify(kit, item, kit.Map(ResourceKind.Routes, With("[5,4]"))).Passed);
        Assert.True(Verify(kit, item, kit.Map(ResourceKind.Routes, With("[\"5\",\"4\"]"))).Passed);
        Assert.False(Verify(kit, item, kit.Map(ResourceKind.Routes, With("[4]"))).Passed);
        Assert.False(Verify(kit, item, kit.Map(ResourceKind.Routes, With("[4,4,5]"))).Passed);
        Assert.False(Verify(kit, item, kit.Map(ResourceKind.Routes, With("4"))).Passed);
    }

    [Fact]
    public void Tag_lists_ignore_order_but_not_content()
    {
        var kit = Kit();
        var routing = kit.Map(ResourceKind.Routings, ApiKit.Routing9);
        var item = ApiKit.Update(ResourceKind.Routings, routing, ("Tags", "a,b"));
        string With(string tags) => $$"""{"id":9,"airline_id":7,"departure_airport_id":101,"arrival_airport_id":102,"route":"DCT","tag":{{tags}}}""";
        Assert.True(Verify(kit, item, kit.Map(ResourceKind.Routings, With("[\"b\",\"a\"]"))).Passed);
        Assert.False(Verify(kit, item, kit.Map(ResourceKind.Routings, With("[\"a\"]"))).Passed);
    }

    [Fact]
    public void Dates_are_compared_as_instants()
    {
        var kit = Kit();
        var route = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        var item = ApiKit.Update(ResourceKind.Routes, route, ("Start Date", "2026-10-01 00:00:00"));
        string With(string date) => $$"""{"id":10,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO100","flight_number":"CA100","start_date":{{date}}}""";
        Assert.True(Verify(kit, item, kit.Map(ResourceKind.Routes, With("\"2026-10-01T02:00:00+02:00\""))).Passed);
        Assert.False(Verify(kit, item, kit.Map(ResourceKind.Routes, With("\"2026-10-01T02:00:00+00:00\""))).Passed);
        Assert.False(Verify(kit, item, kit.Map(ResourceKind.Routes, With("null"))).Passed);
    }

    [Fact]
    public void A_created_jumpseat_may_come_back_without_callsign_and_flight_number()
    {
        var kit = Kit();
        var item = ApiKit.Create(ResourceKind.Routes, ("Type", "jumpseat"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"));
        var current = kit.Map(ResourceKind.Routes, """{"id":90,"departure_id":101,"arrival_id":102,"type":"jumpseat","callsign":null,"flight_number":null}""");
        Assert.True(Verify(kit, item, current).Passed);
        // an update never gets that allowance
        var update = ApiKit.Update(ResourceKind.Routes, kit.Map(ResourceKind.Routes, ApiKit.Jumpseat11), ("Remarks", "x"));
        Assert.False(Verify(kit, update, kit.Map(ResourceKind.Routes, ApiKit.Jumpseat11)).Passed);
    }

    // 2026-11-12, route 14217362: a jumpseat sent with fleets came back with "fleet_ids": null. A task saved with that
    // request settles on the read; new jumpseats are no longer sent with fleets (planner and validation block them).
    [Fact]
    public void A_created_jumpseat_sent_with_fleets_may_come_back_without_them()
    {
        var kit = Kit();
        var item = ApiKit.Create(ResourceKind.Routes, ("Type", "jumpseat"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"));
        var expected = ApiExpectedFor(kit, item).ToDictionary();
        expected["fleet_ids"] = ApiKit.Json("[4]");
        var current = kit.Map(ResourceKind.Routes, """{"id":90,"departure_id":101,"arrival_id":102,"type":"jumpseat","callsign":null,"flight_number":null,"fleet_ids":null}""");
        Assert.True(kit.Api.VerifyReadback(item, current, item.After, expected).Passed);
        // a scheduled route that loses its fleets is still a mismatch
        var scheduled = ApiKit.Create(ResourceKind.Routes, ("Type", "scheduled"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"), ("Fleet IDs", "4"));
        var lost = kit.Map(ResourceKind.Routes, """{"id":91,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO1234","flight_number":"CA1234","fleet_ids":null}""");
        Assert.False(Verify(kit, scheduled, lost).Passed);
    }

    [Fact]
    public void A_recovered_jumpseat_must_be_new_and_created_inside_the_request_window()
    {
        var kit = Kit();
        var started = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        ChangeItem Recovered(params string[] before)
        {
            var item = ApiKit.Create(ResourceKind.Routes, ("Type", "jumpseat"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"));
            item.State = ItemState.Unknown;
            item.PreCreateCandidateIds = before.ToList();
            item.ApiSteps = [new BatchStep { Method = "POST", Path = "routes", StartedAt = started, FinishedAt = started.AddSeconds(5) }];
            return item;
        }
        DataRow Row(string created) => kit.Map(ResourceKind.Routes, $$"""{"id":90,"departure_id":101,"arrival_id":102,"type":"jumpseat","callsign":null,"flight_number":null,"created_at":"{{created}}"}""");

        Assert.True(Verify(kit, Recovered("1", "2"), Row("2026-10-01T10:00:03Z")).Passed);
        Assert.Contains(Verify(kit, Recovered("90"), Row("2026-10-01T10:00:03Z")).Differences, d => d.Field == "pre_create_candidates" && d.Expected == "new record");
        Assert.Contains(Verify(kit, Recovered("1"), Row("2026-10-01T09:50:00Z")).Differences, d => d.Field == "created_at");
        Assert.Contains(Verify(kit, Recovered("1"), Row("2026-10-01T10:05:00Z")).Differences, d => d.Field == "created_at");

        var unrecorded = Recovered("1");
        unrecorded.PreCreateCandidateIds = null;
        unrecorded.ApiSteps = [];
        var reasons = Verify(kit, unrecorded, Row("2026-10-01T10:00:03Z")).Differences.Select(d => d.Field).ToArray();
        Assert.Equal(["pre_create_candidates", "request_window"], reasons);
    }

    [Fact]
    public void Matching_after_a_write_uses_the_expected_values_saved_with_the_item_before_the_plan()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        item.ApiExpectedValues = new() { ["name"] = JsonSerializer.SerializeToElement("Saved name") };
        var current = kit.Map(ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"Saved name"}""");
        Assert.True(kit.Api.Matches(item, current, item.After, false));
    }

    [Fact]
    public void Rows_of_another_airline_are_refused_in_both_directions()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        var foreign = item.After.WithIdentity(new("101", "9"));
        Assert.Equal("ApiWrongVa", ApiKit.BlockedWith(() => kit.Api.Matches(item, foreign, item.After, false)));
        Assert.Equal("ApiWrongVa", ApiKit.BlockedWith(() => kit.Api.Matches(item, item.After, foreign, true)));
    }

    // ---- before a write ----------------------------------------------------------------------------------------

    [Fact]
    public void A_record_that_still_reads_as_planned_is_unchanged()
    {
        var kit = Kit();
        var before = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        var item = ApiKit.Update(ResourceKind.Airports, before, ("Name", "Heathrow"));
        Assert.True(kit.Api.Matches(item, kit.Map(ResourceKind.Airports, ApiKit.Egll), before, true));
    }

    [Fact]
    public void Only_the_fields_the_change_touches_decide_whether_the_record_changed()
    {
        var kit = Kit();
        var before = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        var item = ApiKit.Update(ResourceKind.Airports, before, ("Name", "Heathrow"));
        var renamed = kit.Map(ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"Somebody else renamed it","container_ids":[1]}""");
        Assert.False(kit.Api.Matches(item, renamed, before, true));
        var categoryOnly = kit.Map(ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"London","category":"large","container_ids":[1]}""");
        Assert.True(kit.Api.Matches(item, categoryOnly, before, true));
    }

    [Fact]
    public void A_delete_compares_every_api_field()
    {
        var kit = Kit();
        var before = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        var item = ApiKit.Delete(ResourceKind.Airports, before);
        var changed = kit.Map(ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"London","category":"large","container_ids":[1]}""");
        Assert.False(kit.Api.Matches(item, changed, before, true));
        Assert.True(kit.Api.Matches(item, kit.Map(ResourceKind.Airports, ApiKit.Egll), before, true));
    }

    [Fact]
    public void Set_valued_columns_ignore_order_and_spacing_before_a_write_too()
    {
        var kit = Kit();
        var before = kit.Map(ResourceKind.Routes, ApiKit.Scheduled12);
        var item = ApiKit.Update(ResourceKind.Routes, before, ("Fleet IDs", "4"));
        var reordered = kit.Map(ResourceKind.Routes, """{"id":12,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO120","flight_number":"CA120","fleet_ids":[5,4]}""");
        Assert.True(kit.Api.Matches(item, reordered, before, true));
    }

    [Fact]
    public void Seconds_the_grid_hides_still_count_as_a_change_of_a_time()
    {
        var kit = Kit();
        var before = kit.Map(ResourceKind.Routes, ApiKit.Scheduled12);
        var item = ApiKit.Update(ResourceKind.Routes, before, ("Departure Time (HH:MM)", "00:10"));
        string With(string time) => $$"""{"id":12,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO120","flight_number":"CA120","fleet_ids":[4,5],"departure_time":"{{time}}"}""";
        Assert.True(kit.Api.Matches(item, kit.Map(ResourceKind.Routes, With("23:59:37")), before, true));
        Assert.False(kit.Api.Matches(item, kit.Map(ResourceKind.Routes, With("23:59:38")), before, true));
        Assert.False(kit.Api.Matches(item, kit.Map(ResourceKind.Routes, With("22:00:37")), before, true));
    }

    [Fact]
    public void A_moved_endpoint_is_a_change()
    {
        var kit = Kit();
        var before = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        var item = ApiKit.Update(ResourceKind.Routes, before, (Dep, "EGLL"));
        item.Fields[Dep] = new(FieldIntent.Set, "EGLL");
        var moved = kit.Map(ResourceKind.Routes, """{"id":10,"departure_id":102,"arrival_id":102,"type":"scheduled","callsign":"CAO100","flight_number":"CA100","fleet_ids":[4]}""");
        Assert.False(kit.Api.Matches(item, moved, before, true));
        Assert.True(kit.Api.Matches(item, kit.Map(ResourceKind.Routes, ApiKit.Scheduled10), before, true));
    }

    [Fact]
    public void An_aircraft_in_another_fleet_is_a_change()
    {
        var kit = Kit();
        var before = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        var item = ApiKit.Update(ResourceKind.Aircraft, before, ("Name", "Renamed"));
        var moved = kit.Map(ResourceKind.Aircraft, """{"id":8,"airline_id":7,"fleet_id":5,"registration":"B-TEST","name":"Plane","cargo":1000}""");
        Assert.False(kit.Api.Matches(item, moved, before, true));
    }

    // ---- display evidence --------------------------------------------------------------------------------------

    [Fact]
    public void Before_differences_list_every_api_field_that_changed()
    {
        var kit = Kit();
        var before = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        var item = ApiKit.Update(ResourceKind.Airports, before, ("Name", "Heathrow"));
        var current = kit.Map(ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"London 2","category":"large","container_ids":[1]}""");
        var differences = kit.Api.BeforeDifferences(item, current);
        Assert.Equal(new[] { new ApiFieldDifference("Name", "London", "London 2"), new ApiFieldDifference("Category", "", "large") }, differences);
    }

    [Fact]
    public async Task Readback_evidence_keeps_the_last_response_and_the_reasons_it_failed()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Get, "airports/101", ApiKit.Data(ApiKit.Egll));
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        item.ApiExpectedValues = OperationsAdapter.ExpectedValues(kit.Api.Plan(item));
        var current = await kit.Api.FindRow(ResourceKind.Airports, item.After, "101", TestContext.Current.CancellationToken);
        kit.Api.RecordReadback(item, current);
        Assert.Equal("Verifying", item.ReadbackDetails!.Phase);
        Assert.Equal(200, item.ReadbackDetails.Status);
        Assert.Equal(new ApiFieldDifference("name", "\"Heathrow\"", "\"London\""), Assert.Single(item.VerificationDifferences));
    }

    [Fact]
    public void Preflight_evidence_keeps_both_timestamps_and_the_fields_that_differ()
    {
        var kit = Kit();
        var before = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        var item = ApiKit.Update(ResourceKind.Fleets, before, ("Name", "Renamed"));
        var current = kit.Map(ResourceKind.Fleets, """{"id":4,"airline_id":7,"name":"Fleet 4 (edited elsewhere)","code":"B738","type":"cargo","max_cargo":10000,"updated_at":"2026-02-02T00:00:00Z"}""");
        kit.Api.RecordPreflight(item, current);
        var comparison = item.PreflightComparison!;
        Assert.Equal("2026-01-01T00:00:00Z", comparison.PreviousUpdatedAt);
        Assert.Equal("2026-02-02T00:00:00Z", comparison.CurrentUpdatedAt);
        Assert.Contains(new ApiFieldDifference("Name", "Fleet 4", "Fleet 4 (edited elsewhere)"), comparison.BusinessFields);
        Assert.Contains(comparison.BusinessFields, d => d.Field == "Parameter IDs");
    }

    [Theory]
    [InlineData("Fleet IDs", "1, 2", "2,1", true)]
    [InlineData("Container IDs", "3,3,4", "4,3", true)]
    [InlineData("Tags", "a,b", " b ,a", true)]
    [InlineData("Service Days", "monday,friday", "friday,monday", true)]
    [InlineData("Parameter IDs", "1,2", "2,1", false)]
    [InlineData("Name", "a ", "a", false)]
    [InlineData("Fleet IDs", "1", "1,2", false)]
    public void Only_set_valued_columns_compare_as_sets(string field, string a, string b, bool equal)
        => Assert.Equal(equal, OperationsAdapter.Equivalent(field, a, b));
}
