using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Api;

/// <summary>
/// The duplicate-candidate index of one run (<see cref="CandidateQuerySession"/>): how reservations and confirmations move
/// records in and out of it, which signatures it keeps, and that a failed build never publishes a half index.
/// </summary>
public class CandidateIndexSessionTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string List(params string[] records) => "{\"data\":[" + string.Join(',', records) + "],\"meta\":{\"next_cursor_url\":null}}";

    static string Fleet(int id, string name) => JsonSerializer.Serialize(new { id, airline_id = 7, name, code = "B738", type = "pax" });

    static async Task<List<DataRow>> Rows(ApiKit kit, ChangeItem item, CandidateQuerySession session)
    {
        var rows = new List<DataRow>();
        await foreach (var row in kit.Api.ReadDuplicateCandidatesAsync(item, session, Ct))
            rows.Add(row);
        return rows;
    }

    static ChangeItem NewFleet(string name) => ApiKit.Create(ResourceKind.Fleets, ("Name", name), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"));

    // ---- reservations and confirmations ---------------------------------------------------------------------------

    [Fact]
    public async Task A_confirmed_rename_or_delete_releases_only_the_changed_id_and_leaves_a_colliding_record_in_the_index()
    {
        var kit = new ApiKit();
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(List(Fleet(4, "Same"), Fleet(5, "SAME"))));
        var session = new CandidateQuerySession();
        var query = NewFleet("same");
        var read = await Rows(kit, query, session);
        Assert.Equal(["4", "5"], read.Select(r => r.Identity!.RemoteId).Order());
        var first = read.Single(r => r.Identity!.RemoteId == "4");
        var second = read.Single(r => r.Identity!.RemoteId == "5");

        // an uncertain rename of 4 keeps its old name reserved until it is confirmed
        var update = new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Update, Before = first, After = first.With("Name", "Renamed") };
        kit.Api.ReserveCandidate(update, session);
        Assert.Equal("ApiIdentityPending", kit.Api.CandidateBlock(query, session));
        kit.Api.ConfirmCandidate(update, kit.Map(ResourceKind.Fleets, Fleet(4, "Renamed")), session);
        Assert.Equal(["5"], (await Rows(kit, query, session)).Select(r => r.Identity!.RemoteId));

        // the same for a delete of the record that collided with it
        var deletion = new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Delete, Before = second, After = second.Dup(), RemoteId = "5" };
        kit.Api.ReserveCandidate(deletion, session);
        Assert.Equal("ApiIdentityPending", kit.Api.CandidateBlock(query, session));
        kit.Api.ConfirmCandidate(deletion, null, session);
        Assert.Empty(await Rows(kit, query, session));

        // the renamed record is found under its new name, in any case, without reading the list again
        Assert.Single(await Rows(kit, NewFleet("RENAMED"), session));
        Assert.Single(kit.Server.Requests);

        // a confirmed creation moves too: once it is renamed, its first name is free
        var created = NewFleet("Fresh");
        kit.Api.ConfirmCandidate(created, kit.Map(ResourceKind.Fleets, Fleet(6, "Fresh")), session);
        Assert.Single(await Rows(kit, created, session));
        kit.Api.ConfirmCandidate(new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Update, RemoteId = "6" }, kit.Map(ResourceKind.Fleets, Fleet(6, "Newer")), session);
        Assert.Empty(await Rows(kit, created, session));
    }

    [Fact]
    public async Task Airport_aliases_and_route_endpoints_are_part_of_the_signature_of_an_uncertain_write()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        var session = new CandidateQuerySession();
        kit.Server.On(HttpMethod.Get, "airports", FakeResponse.Ok(List(ApiKit.Egll)));
        // an airport is found by either of its codes, in any case
        foreach (var code in new[] { "lhr", "egll" })
            Assert.Single(await Rows(kit, ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", code)), session));

        // an uncertain route or routing update blocks the same airport pair under every spelling of the airports, old value and new
        foreach (var kind in new[] { ResourceKind.Routes, ResourceKind.Routings })
        {
            var field = kind == ResourceKind.Routes ? "Flight Number" : "Route String";
            var raw = kind == ResourceKind.Routes
                ? """{"id":9,"departure_id":101,"arrival_id":101,"flight_number":"OLD"}"""
                : """{"id":9,"airline_id":7,"departure_airport_id":101,"arrival_airport_id":101,"route":"OLD"}""";
            var before = kit.Map(kind, raw);
            var unknown = new ChangeItem { Resource = kind, Kind = ChangeKind.Update, Before = before, After = before.With(field, "NEW") };
            kit.Api.ReserveCandidate(unknown, session);
            foreach (var code in new[] { "LHR", "egll", "101" })
                foreach (var value in new[] { "old", "new" })
                    Assert.Equal("ApiIdentityPending", kit.Api.CandidateBlock(ApiKit.Create(kind, (Dep, code), (Arr, "101"), (field, value)), session));
            kit.Api.ConfirmCandidate(unknown, kit.Map(kind, raw.Replace("OLD", "NEW", StringComparison.Ordinal)), session);
        }
        Assert.Single(kit.Server.Requests);
    }

    [Fact]
    public async Task A_session_of_one_airline_refuses_a_workspace_of_another()
    {
        var first = new ApiKit();
        first.Server.On(HttpMethod.Get, "airports", FakeResponse.Ok(List(ApiKit.Egll)));
        var session = new CandidateQuerySession();
        await Rows(first, ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "LHR")), session);

        var other = new ApiKit("8");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Rows(other, ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "LHR")), session));
        Assert.Equal("ApiWrongVa", MessageErrors.Describe(error).Code);
        Assert.Empty(other.Server.Requests);
    }

    // ---- building the index ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_index_build_publishes_nothing_and_an_aircraft_registration_is_compared_across_fleets()
    {
        var kit = new ApiKit();
        var session = new CandidateQuerySession();
        string Aircraft(int id, int fleet) => JsonSerializer.Serialize(new { id, airline_id = 7, fleet_id = fleet, registration = "G-TEST", name = "Plane" });
        var fail = true;
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(List(Fleet(4, "First"), Fleet(5, "Second"))));
        kit.Server.On(HttpMethod.Get, "fleet/4/aircraft", FakeResponse.Ok(List(Aircraft(10, 4))));
        kit.Server.On(HttpMethod.Get, "fleet/5/aircraft", _ => fail ? throw new TimeoutException("simulated") : FakeResponse.Ok(List(Aircraft(11, 5))));
        var query = ApiKit.Create(ResourceKind.Aircraft, ("Fleet ID", "4"), ("Registration", "g-test"));

        using var metrics = PerformanceRun.Begin(Guid.NewGuid(), Guid.NewGuid());
        await Assert.ThrowsAnyAsync<Exception>(() => Rows(kit, query, session));
        Assert.Equal(0, metrics.IndexBuilds);

        fail = false;
        Assert.Equal(["10", "11"], (await Rows(kit, query, session)).Select(r => r.Identity!.RemoteId).Order());
        Assert.Equal(1, metrics.IndexBuilds);
        var requests = kit.Server.Requests.Count;
        Assert.Equal(2, (await Rows(kit, query, session)).Count);
        Assert.Equal(requests, kit.Server.Requests.Count);
    }

    // ---- reservations from a job that was saved before ---------------------------------------------------------------

    [Fact]
    public void An_unknown_delete_whose_row_has_no_usable_identity_keeps_every_airport_create_waiting()
    {
        var kit = new ApiKit();
        var legacy = new DataRow { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = "LHR" } }.WithIdentity(new("101", "7"));
        var unknown = new ChangeItem { Resource = ResourceKind.Airports, Kind = ChangeKind.Delete, Before = legacy, After = legacy.Dup(), State = ItemState.Unknown };
        var create = ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "EGLL"));

        // no raw record: the aliases of the airport are unknown, so nothing can be said to be free
        var session = new CandidateQuerySession();
        kit.Api.SeedCandidates(new BatchJob { Items = [unknown] }, session);
        Assert.Equal("ApiIdentityPending", kit.Api.CandidateBlock(create, session));

        // a raw record without any code is opaque too, not an empty reservation
        unknown.Before = legacy.WithRawApiJson("{}");
        session = new CandidateQuerySession();
        kit.Api.SeedCandidates(new BatchJob { Items = [unknown] }, session);
        Assert.Equal("ApiIdentityPending", kit.Api.CandidateBlock(create, session));
    }

    [Fact]
    public void The_pending_reasons_are_translated_and_task_files_without_them_still_load()
    {
        var zh = new LocalizationService();
        var en = new LocalizationService();
        en.SetLanguage("en-US");
        foreach (var key in new[] { "ApiAirportCreatePending", "ApiIdentityPending" })
        {
            var descriptor = Messages.Define(key);
            Assert.NotEqual(zh.Format(descriptor), en.Format(descriptor));
            var item = JsonSerializer.Deserialize<ChangeItem>(JsonSerializer.Serialize(new ChangeItem { State = ItemState.Pending, Description = descriptor }))!;
            Assert.Equal(ItemState.Pending, item.State);
            Assert.Equal(en.Format(descriptor), en.Format(item.Description!));
        }
        var old = JsonSerializer.Deserialize<BatchJob>("""{"Status":"Old external status","Items":[{"State":4,"Message":"Old diagnostic"}]}""")!;
        Assert.Equal(ItemState.Unknown, old.Items.Single().State);
        Assert.Equal("Old diagnostic", old.Items.Single().Message);
    }
}
