using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Api;

/// <summary>Would a new record duplicate one that exists, and may a create go ahead while another write's outcome is unknown?</summary>
public class DuplicateFinderTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    static ApiKit Kit()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        return kit;
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the duplicate rule of each resource --------------------------------------------------------------------

    [Fact]
    public void An_airport_is_a_duplicate_when_the_code_is_its_icao_or_its_iata_in_any_case()
    {
        var kit = Kit();
        var existing = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        bool Dup(string code) => kit.Api.IsDuplicate(ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", code), ("Name", "x")), existing);
        Assert.True(Dup("EGLL"));
        Assert.True(Dup("lhr"));
        Assert.False(Dup("EGSS"));
        Assert.False(Dup(""));
    }

    [Fact]
    public void A_fleet_is_a_duplicate_when_the_name_matches_ignoring_case()
    {
        var kit = Kit();
        var existing = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        Assert.True(kit.Api.IsDuplicate(ApiKit.Create(ResourceKind.Fleets, ("Name", "FLEET 4")), existing));
        Assert.False(kit.Api.IsDuplicate(ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet 44")), existing));
    }

    [Fact]
    public void An_aircraft_is_a_duplicate_when_the_registration_matches_ignoring_case()
    {
        var kit = Kit();
        var existing = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        Assert.True(kit.Api.IsDuplicate(ApiKit.Create(ResourceKind.Aircraft, ("Registration", "b-test"), ("Fleet ID", "4")), existing));
        Assert.False(kit.Api.IsDuplicate(ApiKit.Create(ResourceKind.Aircraft, ("Registration", "B-OTHER"), ("Fleet ID", "4")), existing));
    }

    [Fact]
    public void A_routing_is_a_duplicate_on_the_same_airports_with_the_same_route_string()
    {
        var kit = Kit();
        var existing = kit.Map(ResourceKind.Routings, ApiKit.Routing9);
        Assert.True(kit.Api.IsDuplicate(ApiKit.Create(ResourceKind.Routings, (Dep, "EGLL"), (Arr, "LGW"), ("Route String", "dct")), existing));
        Assert.False(kit.Api.IsDuplicate(ApiKit.Create(ResourceKind.Routings, (Dep, "EGLL"), (Arr, "LGW"), ("Route String", "UL9")), existing));
        Assert.False(kit.Api.IsDuplicate(ApiKit.Create(ResourceKind.Routings, (Dep, "EGKK"), (Arr, "LHR"), ("Route String", "DCT")), existing));
    }

    [Fact]
    public void A_scheduled_route_is_a_duplicate_on_the_same_airports_with_the_same_flight_number()
    {
        var kit = Kit();
        var existing = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        ChangeItem New(string number, string from = "EGLL") => ApiKit.Create(ResourceKind.Routes, ("Type", "scheduled"), (Dep, from), (Arr, "EGKK"), ("Callsign", "CAO100"), ("Flight Number", number), ("Fleet IDs", "4"));
        Assert.True(kit.Api.IsDuplicate(New("ca100"), existing));
        Assert.False(kit.Api.IsDuplicate(New("CA101"), existing));
        Assert.False(kit.Api.IsDuplicate(New("CA100", from: "EGKK"), existing));
    }

    [Fact]
    public void A_jumpseat_has_no_flight_number_so_its_times_and_dates_decide()
    {
        var kit = Kit();
        var existing = kit.Map(ResourceKind.Routes, """{"id":11,"departure_id":101,"arrival_id":102,"type":"jumpseat","departure_time":"10:00:00","callsign":null,"flight_number":null}""");
        ChangeItem New(params (string, string)[] extra) => ApiKit.Create(ResourceKind.Routes, [("Type", "jumpseat"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"), .. extra]);
        // nothing to tell them apart: the same route
        Assert.True(kit.Api.IsDuplicate(New(), existing));
        // a time that is filled on both sides and differs: another route
        Assert.False(kit.Api.IsDuplicate(New(("Departure Time (HH:MM)", "11:00")), existing));
        Assert.True(kit.Api.IsDuplicate(New(("Departure Time (HH:MM)", "10:00")), existing));
        // a scheduled route is never the same as a jumpseat
        var scheduled = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        Assert.False(kit.Api.IsDuplicate(New(), scheduled));
    }

    [Fact]
    public void Records_of_another_airline_are_not_compared()
    {
        var kit = Kit();
        var foreign = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4).WithIdentity(new("4", "9"));
        Assert.Equal("ApiWrongVa", ApiKit.BlockedWith(() => kit.Api.IsDuplicate(ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet 4")), foreign)));
    }

    // ---- reading the candidates ---------------------------------------------------------------------------------

    static string List(params string[] records) => "{\"data\":[" + string.Join(',', records) + "],\"meta\":{\"next_cursor_url\":null}}";

    static async Task<List<string>> Candidates(ApiKit kit, ChangeItem item, CandidateQuerySession session, int? stopAfter = null)
    {
        var ids = new List<string>();
        await foreach (var row in kit.Api.ReadDuplicateCandidatesAsync(item, session, Ct))
        {
            ids.Add(row.Identity!.RemoteId);
            if (ids.Count == stopAfter)
                break;
        }
        return ids;
    }

    [Fact]
    public async Task A_resource_is_read_completely_once_per_session_and_found_by_signature()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(List(ApiKit.Fleet4, ApiKit.Fleet5)));
        var session = new CandidateQuerySession();
        Assert.Equal(["4"], await Candidates(kit, ApiKit.Create(ResourceKind.Fleets, ("Name", "fleet 4")), session));
        Assert.Equal(["5"], await Candidates(kit, ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet 5")), session));
        Assert.Empty(await Candidates(kit, ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet 6")), session));
        Assert.Single(kit.Server.Requests, r => r.Path.EndsWith("/fleet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_airport_is_found_by_either_of_its_codes()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Get, "airports", FakeResponse.Ok(List(ApiKit.Egll, ApiKit.Egkk)));
        var session = new CandidateQuerySession();
        Assert.Equal(["101"], await Candidates(kit, ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "lhr"), ("Name", "x")), session));
        Assert.Equal(["102"], await Candidates(kit, ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "EGKK"), ("Name", "x")), session));
    }

    [Fact]
    public async Task Routes_are_read_per_airport_pair_and_a_complete_pair_is_cached()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Get, "routes", FakeResponse.Ok(List(ApiKit.Scheduled10)));
        var session = new CandidateQuerySession();
        ChangeItem New(string number) => ApiKit.Create(ResourceKind.Routes, ("Type", "scheduled"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO100"), ("Flight Number", number), ("Fleet IDs", "4"));
        Assert.Equal(["10"], await Candidates(kit, New("CA101"), session));
        Assert.Equal(["10"], await Candidates(kit, New("CA102"), session));
        var request = Assert.Single(kit.Server.Requests, r => r.Path.EndsWith("/routes", StringComparison.Ordinal));
        Assert.Contains("filter%5Bdeparture_id%5D=101", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("filter%5Barrival_id%5D=102", request.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pair_that_was_not_read_to_the_end_is_not_cached()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Get, "routes", FakeResponse.Ok(List(ApiKit.Scheduled10, ApiKit.Scheduled12)));
        var session = new CandidateQuerySession();
        var item = ApiKit.Create(ResourceKind.Routes, ("Type", "scheduled"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO100"), ("Flight Number", "CA999"), ("Fleet IDs", "4"));
        Assert.Single(await Candidates(kit, item, session, stopAfter: 1));
        Assert.Equal(["10", "12"], await Candidates(kit, item, session));
        Assert.Equal(["10", "12"], await Candidates(kit, item, session));
        Assert.Equal(2, kit.Server.Requests.Count(r => r.Path.EndsWith("/routes", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_record_confirmed_earlier_in_the_run_is_found_without_a_request()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(List(ApiKit.Fleet4)));
        var session = new CandidateQuerySession();
        var created = ApiKit.Create(ResourceKind.Fleets, ("Name", "Brand new"));
        created.RemoteId = "77";
        kit.Api.ConfirmCandidate(created, kit.Map(ResourceKind.Fleets, """{"id":77,"airline_id":7,"name":"Brand new","code":"A320","type":"pax"}"""), session);
        Assert.Equal(["77"], await Candidates(kit, ApiKit.Create(ResourceKind.Fleets, ("Name", "brand NEW")), session));
        // a confirmed delete takes the record out again
        var deleted = ApiKit.Delete(ResourceKind.Fleets, kit.Map(ResourceKind.Fleets, ApiKit.Fleet4));
        kit.Api.ConfirmCandidate(deleted, null, session);
        Assert.Empty(await Candidates(kit, ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet 4")), session));
    }

    [Fact]
    public async Task A_session_belongs_to_one_workspace_and_one_airline()
    {
        var first = Kit();
        var second = Kit();
        first.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(List()));
        var session = new CandidateQuerySession();
        await Candidates(first, ApiKit.Create(ResourceKind.Fleets, ("Name", "x")), session);
        await Assert.ThrowsAnyAsync<Exception>(() => Candidates(second, ApiKit.Create(ResourceKind.Fleets, ("Name", "x")), session));
    }

    [Fact]
    public async Task A_failing_page_publishes_nothing()
    {
        var kit = Kit();
        kit.Server.Enqueue(HttpMethod.Get, "fleet", FakeResponse.WithStatus(500, "{}"));
        var session = new CandidateQuerySession();
        await Assert.ThrowsAnyAsync<Exception>(() => Candidates(kit, ApiKit.Create(ResourceKind.Fleets, ("Name", "x")), session));
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(List(ApiKit.Fleet4)));
        Assert.Equal(["4"], await Candidates(kit, ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet 4")), session));
    }

    // ---- reservations ------------------------------------------------------------------------------------------

    [Fact]
    public void A_create_waits_while_an_unknown_write_could_be_the_same_record()
    {
        var kit = Kit();
        var session = new CandidateQuerySession();
        var pending = ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet X"));
        kit.Api.ReserveCandidate(pending, session);
        Assert.Equal("ApiIdentityPending", kit.Api.CandidateBlock(ApiKit.Create(ResourceKind.Fleets, ("Name", "fleet x")), session));
        Assert.Null(kit.Api.CandidateBlock(ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet Y")), session));
        Assert.Null(kit.Api.CandidateBlock(ApiKit.Create(ResourceKind.Aircraft, ("Registration", "Fleet X"), ("Fleet ID", "4")), session));
    }

    [Fact]
    public void While_an_airport_create_is_unresolved_no_airport_create_goes_ahead()
    {
        var kit = Kit();
        var session = new CandidateQuerySession();
        kit.Api.ReserveCandidate(ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "Stansted")), session);
        // another code could be the same airport (ICAO versus IATA), so every airport create waits
        Assert.Equal("ApiAirportCreatePending", kit.Api.CandidateBlock(ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "STN"), ("Name", "Stansted")), session));
        Assert.Null(kit.Api.CandidateBlock(ApiKit.Create(ResourceKind.Fleets, ("Name", "x")), session));
    }

    [Fact]
    public void An_item_whose_identity_cannot_be_derived_blocks_every_create_of_its_resource()
    {
        var kit = Kit();
        var session = new CandidateQuerySession();
        // a legacy row without the server's raw record: its aliases are unknown
        var legacy = new DataRow { Identity = new("500", "7"), Fields = new Dictionary<string, string> { ["ICAO/IATA"] = "EGXX", ["Name"] = "x" } };
        kit.Api.ReserveCandidate(ApiKit.Update(ResourceKind.Airports, legacy, ("Name", "y")), session);
        Assert.Equal("ApiIdentityPending", kit.Api.CandidateBlock(ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "EGZZ"), ("Name", "z")), session));
        Assert.Null(kit.Api.CandidateBlock(ApiKit.Create(ResourceKind.Fleets, ("Name", "any")), session));
    }
    [Fact]
    public void Only_uncertain_writes_are_reserved_when_a_run_starts()
    {
        var kit = Kit();
        ChangeItem Fleet(string name, ItemState state, bool created = false, bool accepted = false)
        {
            var item = ApiKit.Create(ResourceKind.Fleets, ("Name", name));
            item.State = state;
            item.CreateCompleted = created;
            item.WriteAccepted = accepted;
            return item;
        }
        var job = new BatchJob
        {
            Mode = RunMode.Online,
            Items = [Fleet("unknown", ItemState.Unknown), Fleet("running", ItemState.Running), Fleet("created", ItemState.Pending, created: true), Fleet("accepted", ItemState.Pending, accepted: true),
                Fleet("pending", ItemState.Pending), Fleet("done", ItemState.Succeeded, accepted: true), Fleet("manual", ItemState.ManuallyConfirmed, created: true)]
        };
        var session = new CandidateQuerySession();
        kit.Api.SeedCandidates(job, session);
        foreach (var name in new[] { "unknown", "running", "created", "accepted" })
            Assert.Equal("ApiIdentityPending", kit.Api.CandidateBlock(ApiKit.Create(ResourceKind.Fleets, ("Name", name)), session));
        foreach (var name in new[] { "pending", "done", "manual" })
            Assert.Null(kit.Api.CandidateBlock(ApiKit.Create(ResourceKind.Fleets, ("Name", name)), session));
    }

    [Fact]
    public void A_confirmed_write_ends_its_reservation()
    {
        var kit = Kit();
        var session = new CandidateQuerySession();
        var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet X"));
        kit.Api.ReserveCandidate(item, session);
        item.RemoteId = "77";
        kit.Api.ConfirmCandidate(item, kit.Map(ResourceKind.Fleets, """{"id":77,"airline_id":7,"name":"Fleet X"}"""), session);
        Assert.Null(kit.Api.CandidateBlock(ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet X")), session));
    }

    [Fact]
    public void A_confirmation_needs_an_id_to_know_which_record_it_is_about()
    {
        var kit = Kit();
        Assert.Equal("ApiRefreshFirst", ApiKit.BlockedWith(() => kit.Api.ConfirmCandidate(ApiKit.Create(ResourceKind.Fleets, ("Name", "x")), null, new CandidateQuerySession())));
    }
}
