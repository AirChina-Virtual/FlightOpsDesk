using System.Text.Json;
using VamSys.Core;

namespace VamSys.UnitTests.Domain;

/// <summary>2.4: the fleets a route was refused for, read from the server's 422.</summary>
public class PrefixRejectionTests
{
    // The answer to the first POST /routes of 2026-11-06 (CCA088 / CA88, 31 fleets), as saved in the task record.
    const string Recorded = """
        {"message":"Fleet 'Airbus A330-200P2F' (ID: 26725) is not approved for callsign prefix 'CCA' and flight number prefix 'CA'. (and 5 more errors)","errors":{"fleet_ids.6":["Fleet 'Airbus A330-200P2F' (ID: 26725) is not approved for callsign prefix 'CCA' and flight number prefix 'CA'."],"fleet_ids.15":["Fleet 'Boeing 737-800 For Beijing Airlines' (ID: 26718) is not approved for callsign prefix 'CCA' and flight number prefix 'CA'."],"fleet_ids.16":["Fleet 'Boeing 737-800 For China Air Force' (ID: 27930) is not approved for callsign prefix 'CCA' and flight number prefix 'CA'."],"fleet_ids.17":["Fleet 'Boeing 737-800 For Dalian Airlines' (ID: 26719) is not approved for callsign prefix 'CCA' and flight number prefix 'CA'."],"fleet_ids.20":["Fleet 'Boeing 747-400F' (ID: 26724) is not approved for callsign prefix 'CCA' and flight number prefix 'CA'."],"fleet_ids.27":["Fleet 'Boeing 777-F' (ID: 26722) is not approved for callsign prefix 'CCA' and flight number prefix 'CA'."]}}
        """;

    static ChangeItem Refused(string body, int status = 422, string[]? sent = null, ResourceKind resource = ResourceKind.Routes)
    {
        using var json = JsonDocument.Parse(body);
        var errors = json.RootElement.TryGetProperty("errors", out var e)
            ? e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToArray())
            : [];
        var item = new ChangeItem { Resource = resource, Kind = ChangeKind.Create, State = ItemState.Failed, DiagnosticDetails = new() { Status = status, FieldErrors = errors } };
        if (sent != null)
            item.ApiSteps.Add(new BatchStep { Method = "POST", Path = "routes", Body = new() { ["fleet_ids"] = JsonSerializer.SerializeToElement(sent) } });
        return item;
    }

    [Fact]
    public void The_recorded_answer_names_all_six_fleets_in_order()
        => Assert.Equal(["26725", "26718", "27930", "26719", "26724", "26722"], PrefixRejection.DeniedFleetIds(Refused(Recorded)));

    [Fact]
    public void Without_an_id_in_the_message_the_fleet_is_taken_from_the_position_in_the_request()
    {
        const string body = """{"errors":{"fleet_ids.1":["Fleet 'X' is not approved for callsign prefix 'CCA' and flight number prefix 'CA'."]}}""";
        Assert.Equal(["8"], PrefixRejection.DeniedFleetIds(Refused(body, sent: ["7", "8"])));
        Assert.Empty(PrefixRejection.DeniedFleetIds(Refused(body, sent: ["7"])));
    }

    [Theory]
    [InlineData("""{"errors":{"fleet_ids.0":["Fleet 'X' (ID: 4) is not approved for callsign prefix 'CCA' and flight number prefix 'CA'."],"callsign":["The callsign is invalid."]}}""", 422)]
    [InlineData("""{"errors":{"fleet_ids.0":["The selected fleet_ids.0 is invalid."]}}""", 422)]
    [InlineData("""{"errors":{"fleet_ids.0":["Fleet 'X' (ID: 4) is not approved for callsign prefix 'CCA' and flight number prefix 'CA'."]}}""", 500)]
    [InlineData("""{"message":"Server Error"}""", 422)]
    public void An_answer_that_is_not_only_about_prefixes_proposes_nothing(string body, int status)
        => Assert.Empty(PrefixRejection.DeniedFleetIds(Refused(body, status)));

    [Fact]
    public void Only_routes_are_read()
        => Assert.Empty(PrefixRejection.DeniedFleetIds(Refused(Recorded, resource: ResourceKind.Fleets)));
}
