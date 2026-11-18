using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Api;

/// <summary>
/// G4 (plan 6.1 / 6.3): the read-back after a write accepts a value the server rewrote only where the live write evidence
/// (artifacts/write-evidence/report.json, run FODESK-WE 20261118T001624Z) showed that rewrite for that very field. Every
/// accepted pair below is a sent / read-back pair from that report; every refused pair is a rewrite the evidence did not
/// show (or showed the server does not do), so it still leaves the item Unknown.
/// </summary>
public class ServerNormalisationTests
{
    static ApiKit Kit()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        return kit;
    }

    static (string Json, string Field) Base(ResourceKind kind) => kind switch
    {
        ResourceKind.Fleets => (ApiKit.Fleet4, "Name"),
        ResourceKind.Aircraft => (ApiKit.Aircraft8, "Name"),
        ResourceKind.Routings => (ApiKit.Routing9, "Remarks"),
        ResourceKind.Routes => (ApiKit.Scheduled10, "Remarks"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    // An update whose expected values are exactly what was sent, read back from a record that holds what the server kept.
    static bool Accepts(ResourceKind kind, string wire, string sent, string readback)
    {
        var kit = Kit();
        var (json, field) = Base(kind);
        var item = ApiKit.Update(kind, kit.Map(kind, json), (field, "x"));
        item.ApiExpectedValues = new() { [wire] = JsonDocument.Parse(sent).RootElement.Clone() };
        var record = JsonNode.Parse(json)!.AsObject();
        record[wire] = JsonNode.Parse(readback);
        var current = kit.Map(kind, record.ToJsonString());
        return kit.Api.Matches(item, current, item.After, false);
    }

    // ---- rewrites the evidence showed ---------------------------------------------------------------------------

    [Theory]
    // trimmed: fleet name, aircraft name, registration, routing remarks, route remarks
    [InlineData(ResourceKind.Fleets, "name", "\"  FODESK-WE 20261118T001624Z fleet t1  \"", "\"FODESK-WE 20261118T001624Z fleet t1\"")]
    [InlineData(ResourceKind.Aircraft, "name", "\"  FODESK-WE 20261118T001624Z aircraft t1  \"", "\"FODESK-WE 20261118T001624Z aircraft t1\"")]
    [InlineData(ResourceKind.Aircraft, "registration", "\"  FDW001624C  \"", "\"FDW001624C\"")]
    [InlineData(ResourceKind.Routings, "remarks", "\"  FODESK-WE 20261118T001624Z r1  \"", "\"FODESK-WE 20261118T001624Z r1\"")]
    [InlineData(ResourceKind.Routes, "remarks", "\"  FODESK-WE 20261118T001624Z r1  \"", "\"FODESK-WE 20261118T001624Z r1\"")]
    // upper-cased: fleet code, registration, selcal, hexcode, route callsign and flight number
    [InlineData(ResourceKind.Fleets, "code", "\"a319\"", "\"A319\"")]
    [InlineData(ResourceKind.Aircraft, "registration", "\"fdw001624b\"", "\"FDW001624B\"")]
    [InlineData(ResourceKind.Aircraft, "selcal", "\"ab-cd\"", "\"AB-CD\"")]
    [InlineData(ResourceKind.Aircraft, "hexcode", "\"abc123\"", "\"ABC123\"")]
    [InlineData(ResourceKind.Routes, "callsign", "\"cca9998\"", "\"CCA9998\"")]
    [InlineData(ResourceKind.Routes, "flight_number", "\"ca9998\"", "\"CA9998\"")]
    // a number sent as a string reads back as the number
    [InlineData(ResourceKind.Fleets, "max_pax", "\"120\"", "120")]
    [InlineData(ResourceKind.Fleets, "max_cargo", "\"12000\"", "12000")]
    [InlineData(ResourceKind.Aircraft, "passengers", "\"110\"", "110")]
    [InlineData(ResourceKind.Aircraft, "cargo", "\"1100\"", "1100")]
    [InlineData(ResourceKind.Routes, "altitude", "\"35000\"", "35000")]
    // an empty string reads back as null
    [InlineData(ResourceKind.Aircraft, "fin_number", "\"\"", "null")]
    [InlineData(ResourceKind.Routings, "remarks", "\"\"", "null")]
    [InlineData(ResourceKind.Routes, "remarks", "\"\"", "null")]
    // weekday lists come back lower-case (and sorted Monday..Sunday)
    [InlineData(ResourceKind.Routings, "days_of_operation", "[\"Wednesday\"]", "[\"wednesday\"]")]
    [InlineData(ResourceKind.Routes, "service_days", "[\"Wednesday\"]", "[\"wednesday\"]")]
    // HH:MM reads back as HH:MM:SS
    [InlineData(ResourceKind.Routes, "departure_time", "\"08:05\"", "\"08:05:00\"")]
    // a date alone is midnight UTC; sub-second parts are dropped
    [InlineData(ResourceKind.Routes, "start_date", "\"2027-01-18\"", "\"2027-01-18T00:00:00+00:00\"")]
    [InlineData(ResourceKind.Routes, "end_date", "\"2027-02-15T00:00:00.500000+00:00\"", "\"2027-02-15T00:00:00+00:00\"")]
    public void A_rewrite_the_evidence_showed_for_the_field_passes(ResourceKind kind, string wire, string sent, string readback)
        => Assert.True(Accepts(kind, wire, sent, readback));

    // These pairs from the same report already passed before 6.3; they stay locked so the rules above cannot loosen them.
    [Theory]
    [InlineData(ResourceKind.Fleets, "max_pax", "130.0", "130")]
    [InlineData(ResourceKind.Fleets, "parameter_ids", "[\"9671\"]", "[9671]")]
    [InlineData(ResourceKind.Routes, "fleet_ids", "[\"26071\"]", "[26071]")]
    [InlineData(ResourceKind.Routings, "days_of_operation", "[\"tuesday\",\"monday\"]", "[\"monday\",\"tuesday\"]")]
    [InlineData(ResourceKind.Routes, "service_days", "[\"tuesday\",\"monday\"]", "[\"monday\",\"tuesday\"]")]
    [InlineData(ResourceKind.Routes, "start_date", "\"2027-01-19T00:00:00Z\"", "\"2027-01-19T00:00:00+00:00\"")]
    [InlineData(ResourceKind.Routes, "cost_index", "\"auto\"", "\"auto\"")]
    [InlineData(ResourceKind.Routes, "cost_index", "\"050\"", "\"050\"")]
    [InlineData(ResourceKind.Routes, "tag", "[\"b-tag\",\"a-tag\"]", "[\"b-tag\",\"a-tag\"]")]
    [InlineData(ResourceKind.Fleets, "name", "\"fodesk-we 20261118t001624z fleet c2\"", "\"fodesk-we 20261118t001624z fleet c2\"")]
    [InlineData(ResourceKind.Fleets, "name", "\"FODESK-WE 20261118T001624Z  fleet   w3\"", "\"FODESK-WE 20261118T001624Z  fleet   w3\"")]
    public void A_value_the_server_kept_as_sent_still_passes(ResourceKind kind, string wire, string sent, string readback)
        => Assert.True(Accepts(kind, wire, sent, readback));

    // ---- rewrites the evidence did not show stay differences ----------------------------------------------------

    [Theory]
    // a fleet name keeps its case and its inner spaces
    [InlineData(ResourceKind.Fleets, "name", "\"fleet c2\"", "\"FLEET C2\"")]
    [InlineData(ResourceKind.Fleets, "name", "\"fleet  w3\"", "\"fleet w3\"")]
    // trimming was shown only for the fields listed; the fleet code was only shown upper-cased
    [InlineData(ResourceKind.Fleets, "code", "\" a319 \"", "\"A319\"")]
    // upper-casing was not shown for the fin number
    [InlineData(ResourceKind.Aircraft, "fin_number", "\"ab1\"", "\"AB1\"")]
    // the route string was refused (422) in every other form, so no rewrite of it is accepted
    [InlineData(ResourceKind.Routings, "route", "\"dct\"", "\"DCT\"")]
    // null stands only for an empty string, never for a value that was lost
    [InlineData(ResourceKind.Routes, "remarks", "\"x\"", "null")]
    [InlineData(ResourceKind.Aircraft, "name", "\"\"", "null")]
    // the internal remarks of a created aircraft read back null: not relaxed (see VERIFIED-API-SCOPE)
    [InlineData(ResourceKind.Aircraft, "internal_remarks", "\"FODESK-WE 20261118T001624Z\"", "null")]
    // a number must keep its value
    [InlineData(ResourceKind.Fleets, "max_pax", "\"120\"", "121")]
    [InlineData(ResourceKind.Fleets, "max_pax", "\"0120\"", "120")]
    // cost index is kept exactly as the string sent
    [InlineData(ResourceKind.Routes, "cost_index", "\"050\"", "\"50\"")]
    [InlineData(ResourceKind.Routes, "cost_index", "\"auto\"", "\"AUTO\"")]
    // only seconds of zero are added to HH:MM; H:MM:SS was refused (422)
    [InlineData(ResourceKind.Routes, "departure_time", "\"08:05\"", "\"08:05:30\"")]
    [InlineData(ResourceKind.Routes, "arrival_time", "\"9:30:00\"", "\"09:30:00\"")]
    // HH:MM was only probed on departure_time, so arrival_time gets no rule
    [InlineData(ResourceKind.Routes, "arrival_time", "\"08:05\"", "\"08:05:00\"")]
    // a bare date was only probed on start_date, a dropped fraction only on end_date
    [InlineData(ResourceKind.Routes, "end_date", "\"2027-01-18\"", "\"2027-01-18T00:00:00+00:00\"")]
    [InlineData(ResourceKind.Routes, "start_date", "\"2027-02-15T00:00:00.500000+00:00\"", "\"2027-02-15T00:00:00+00:00\"")]
    // an offset other than UTC is dropped by the server while the wall clock is kept: a different instant, never masked
    [InlineData(ResourceKind.Routes, "start_date", "\"2027-01-20T08:00:00+08:00\"", "\"2027-01-20T08:00:00+00:00\"")]
    // fractions are only ever dropped, not added or changed
    [InlineData(ResourceKind.Routes, "end_date", "\"2027-02-15T00:00:00+00:00\"", "\"2027-02-15T00:00:00.500000+00:00\"")]
    [InlineData(ResourceKind.Routes, "end_date", "\"2027-02-15T00:00:01.500000+00:00\"", "\"2027-02-15T00:00:00+00:00\"")]
    // weekday case is folded, the days themselves are not
    [InlineData(ResourceKind.Routes, "service_days", "[\"Wednesday\"]", "[\"thursday\"]")]
    // tags are neither upper-cased nor lower-cased
    [InlineData(ResourceKind.Routes, "tag", "[\"A-tag\"]", "[\"a-tag\"]")]
    public void A_rewrite_the_evidence_did_not_show_is_still_a_difference(ResourceKind kind, string wire, string sent, string readback)
        => Assert.False(Accepts(kind, wire, sent, readback));

    // ---- through the real planner ---------------------------------------------------------------------------------

    [Fact]
    public void A_registration_typed_in_lower_case_verifies_against_the_upper_case_record()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Aircraft, kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8), ("Registration", "b-new"));
        var current = kit.Map(ResourceKind.Aircraft, """{"id":8,"airline_id":7,"fleet_id":4,"registration":"B-NEW","name":"Plane","cargo":1000}""");
        Assert.True(kit.Api.Matches(item, current, item.After, false));
        var other = kit.Map(ResourceKind.Aircraft, """{"id":8,"airline_id":7,"fleet_id":4,"registration":"B-NEX","name":"Plane","cargo":1000}""");
        Assert.False(kit.Api.Matches(item, other, item.After, false));
    }

    [Fact]
    public void A_created_aircraft_with_internal_remarks_still_fails_its_readback_when_the_server_drops_them()
    {
        var kit = Kit();
        var item = ApiKit.Create(ResourceKind.Aircraft, ("Name", "P"), ("Registration", "QA-1"), ("Fleet ID", "4"), ("Internal Remarks", "note"));
        var current = kit.Map(ResourceKind.Aircraft, """{"id":9,"airline_id":7,"fleet_id":4,"registration":"QA-1","name":"P","internal_remarks":null}""");
        var result = kit.Api.VerifyReadback(item, current, item.After, OperationsAdapter.ExpectedValues(kit.Api.Plan(item)));
        Assert.Equal(new ApiFieldDifference("internal_remarks", "\"note\"", "null"), Assert.Single(result.Differences));
    }

    // ---- the register itself --------------------------------------------------------------------------------------

    [Fact]
    public void Every_rule_names_its_evidence_and_an_api_field_of_its_resource()
    {
        Assert.NotEmpty(ServerNormalisation.Rules);
        foreach (var ((kind, wire), rule) in ServerNormalisation.Rules)
        {
            Assert.NotNull(FieldSpecs.Get(kind).FindByWire(wire));
            Assert.Contains("FODESK-WE 20261118T001624Z", rule.Evidence, StringComparison.Ordinal);
            Assert.Contains("artifacts/write-evidence/report.json", rule.Evidence, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(rule.Probe));
        }
    }

    [Fact]
    public void Fields_the_evidence_showed_unchanged_or_refused_have_no_rule()
    {
        foreach (var (kind, wire) in new[] { (ResourceKind.Routings, "route"), (ResourceKind.Routings, "tag"), (ResourceKind.Routes, "tag"),
                     (ResourceKind.Routes, "cost_index"), (ResourceKind.Aircraft, "internal_remarks"), (ResourceKind.Fleets, "type") })
            Assert.False(ServerNormalisation.Rules.ContainsKey((kind, wire)), $"{kind} {wire}");
        Assert.False(ServerNormalisation.For(ResourceKind.Fleets, "name").Upper);
    }
}
