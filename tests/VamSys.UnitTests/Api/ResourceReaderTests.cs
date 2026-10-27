using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Api;

/// <summary>Everything that reads: collections page by page, single records, the airline lookup and the atomic refresh.</summary>
public class ResourceReaderTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string Page(string next, params string[] records)
        => "{\"data\":[" + string.Join(',', records) + "],\"meta\":{\"next_cursor_url\":" + (next == "" ? "null" : "\"" + next + "\"") + "}}";

    // The next page is named by a path on the same origin, as the real API does.
    static string Url(string path) => "/api/v3/operations/" + path;

    static string[] Paths(ApiKit kit) => kit.Server.Requests.Select(r => r.Path[OperationsAdapter.BaseUri.AbsolutePath.Length..]).ToArray();

    static async Task<List<T>> All<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(Ct))
            list.Add(item);
        return list;
    }

    // ---- collections -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_collection_is_read_page_by_page_and_every_record_is_mapped()
    {
        var kit = new ApiKit();
        kit.Server.Enqueue(HttpMethod.Get, "airports",
            FakeResponse.Ok(Page(Url("airports?cursor=2"), ApiKit.Egll)),
            FakeResponse.Ok(Page("", ApiKit.Egkk)));
        var rows = await All(kit.Api.ReadAllAsync(ResourceKind.Airports, Ct));
        Assert.Equal(["EGLL", "EGKK"], rows.Select(r => r.Get("ICAO/IATA")));
        Assert.Equal(2, kit.Server.Requests.Count);
    }

    [Fact]
    public async Task Aircraft_are_read_fleet_by_fleet()
    {
        var kit = new ApiKit();
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(Page("", ApiKit.Fleet4, ApiKit.Fleet5)));
        kit.Server.On(HttpMethod.Get, "fleet/4/aircraft", FakeResponse.Ok(Page("", ApiKit.Aircraft8)));
        kit.Server.On(HttpMethod.Get, "fleet/5/aircraft", FakeResponse.Ok(Page("", """{"id":9,"airline_id":7,"fleet_id":5,"registration":"B-TWO","name":"Two"}""")));
        var rows = await All(kit.Api.ReadAllAsync(ResourceKind.Aircraft, Ct));
        Assert.Equal(["B-TEST", "B-TWO"], rows.Select(r => r.Get("Registration")));
        Assert.Equal(["fleet?page%5Bsize%5D=100", "fleet/4/aircraft?page%5Bsize%5D=100", "fleet/5/aircraft?page%5Bsize%5D=100"], kit.Server.Requests.Select(r => r.PathAndQuery[OperationsAdapter.BaseUri.AbsolutePath.Length..]));
    }

    [Fact]
    public void Filters_are_escaped_into_the_query()
    {
        var uri = OperationsAdapter.FilterUri("routes", new Dictionary<string, string> { ["departure_id"] = "101", ["fleet id"] = "a&b" });
        Assert.Equal("https://vamsys.io/api/v3/operations/routes?filter%5Bdeparture_id%5D=101&filter%5Bfleet%20id%5D=a%26b", uri.AbsoluteUri);
    }

    [Fact]
    public void Collection_paths_are_fixed_per_resource()
    {
        Assert.Equal(["airports", "fleet", "routings", "routes"], new[] { ResourceKind.Airports, ResourceKind.Fleets, ResourceKind.Routings, ResourceKind.Routes }.Select(OperationsAdapter.Collection));
        Assert.ThrowsAny<ArgumentException>(() => OperationsAdapter.Collection(ResourceKind.Aircraft));
    }

    // ---- single records ----------------------------------------------------------------------------------------

    [Fact]
    public async Task One_record_is_read_from_its_own_path_and_a_404_means_it_is_gone()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        kit.Server.On(HttpMethod.Get, "airports/101", ApiKit.Data(ApiKit.Egll));
        Assert.Equal("EGLL", (await kit.Api.FindAsync(ResourceKind.Airports, "101", Ct))!.Get("ICAO/IATA"));
        Assert.Null(await kit.Api.FindAsync(ResourceKind.Airports, "102", Ct));
    }

    [Fact]
    public async Task Routings_answer_a_bare_record_and_everything_else_wraps_it_in_data()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        kit.Server.On(HttpMethod.Get, "routings/9", FakeResponse.Ok(ApiKit.Routing9));
        var row = await kit.Api.FindAsync(ResourceKind.Routings, "9", Ct);
        Assert.Equal("DCT", row!.Get("Route String"));
        Assert.Equal("EGLL", row.Get("Departure Airport (ICAO/IATA)"));
    }

    [Fact]
    public async Task A_record_with_another_id_than_the_one_asked_for_is_refused()
    {
        var kit = new ApiKit();
        kit.Server.On(HttpMethod.Get, "fleet/4", ApiKit.Data(ApiKit.Fleet5));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.FindAsync(ResourceKind.Fleets, "4", Ct));
        Assert.Equal("ApiWrongVa", MessageErrors.Describe(error).Code);
    }

    [Fact]
    public async Task Only_a_404_is_taken_for_gone_every_other_failure_is_raised()
    {
        var kit = new ApiKit();
        kit.Server.On(HttpMethod.Get, "fleet/4", FakeResponse.WithStatus(422));
        await Assert.ThrowsAsync<ApiResponseException>(() => kit.Api.FindAsync(ResourceKind.Fleets, "4", Ct));
        kit.Server.On(HttpMethod.Get, "fleet/4", FakeResponse.WithStatus(403));
        await Assert.ThrowsAsync<ApiAccessException>(() => kit.Api.FindAsync(ResourceKind.Fleets, "4", Ct));
    }

    [Fact]
    public async Task A_workspace_without_an_airline_cannot_look_anything_up()
    {
        var kit = new ApiKit(airline: null);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.FindAsync(ResourceKind.Fleets, "4", Ct));
        Assert.Equal("ApiConnectFirst", MessageErrors.Describe(error).Code);
    }

    [Fact]
    public async Task An_aircraft_can_only_be_looked_up_once_it_has_been_read()
    {
        var kit = new ApiKit();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.FindAsync(ResourceKind.Aircraft, "8", Ct));
        Assert.Equal("ApiRefreshFirst", MessageErrors.Describe(error).Code);
        Assert.Empty(kit.Server.Requests);
    }

    // ---- which airline are the credentials for? -----------------------------------------------------------------

    [Fact]
    public async Task The_airline_is_found_in_the_first_collection_that_has_a_record()
    {
        var kit = new ApiKit(airline: null);
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(Page("", ApiKit.Fleet4)));
        Assert.True(await kit.Api.IdentifyAirlineAsync(Ct));
        Assert.Equal("7", kit.Workspace.AirlineId);
        Assert.Equal(["fleet"], Paths(kit));
    }

    [Fact]
    public async Task An_empty_airline_is_found_through_airports_then_ranks_then_containers()
    {
        var kit = new ApiKit(airline: null);
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(Page("")));
        kit.Server.On(HttpMethod.Get, "airports", FakeResponse.Ok(Page("")));
        kit.Server.On(HttpMethod.Get, "ranks", FakeResponse.WithStatus(403));
        kit.Server.On(HttpMethod.Get, "containers", FakeResponse.Ok(Page("", """{"id":1,"airline_id":7}""")));
        Assert.True(await kit.Api.IdentifyAirlineAsync(Ct));
        Assert.Equal("7", kit.Api.SessionAirlineId);
        Assert.Equal(4, kit.Server.Requests.Count);
    }

    [Fact]
    public async Task When_nothing_names_the_airline_none_is_guessed()
    {
        var kit = new ApiKit(airline: null);
        foreach (var path in new[] { "fleet", "airports", "ranks", "containers" })
            kit.Server.On(HttpMethod.Get, path, FakeResponse.Ok(Page("")));
        Assert.False(await kit.Api.IdentifyAirlineAsync(Ct));
        Assert.Null(kit.Workspace.AirlineId);
    }

    [Fact]
    public async Task Reads_that_need_the_session_airline_identify_it_once_and_refuse_a_mismatch()
    {
        var kit = new ApiKit();
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(Page("", ApiKit.Fleet4)));
        await kit.Api.EnsureReadConnectionAsync(Ct);
        await kit.Api.EnsureReadConnectionAsync(Ct);
        Assert.Single(kit.Server.Requests);

        var empty = new ApiKit();
        foreach (var path in new[] { "fleet", "airports", "ranks", "containers" })
            empty.Server.On(HttpMethod.Get, path, FakeResponse.Ok(Page("")));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => empty.Api.EnsureReadConnectionAsync(Ct));
        Assert.Equal("ApiConnectFirst", MessageErrors.Describe(error).Code);
    }

    // ---- refresh ----------------------------------------------------------------------------------------------

    static ApiKit Remote(bool withAircraft = true)
    {
        var kit = new ApiKit();
        kit.Server.On(HttpMethod.Get, "airports", FakeResponse.Ok(Page("", ApiKit.Egll, ApiKit.Egkk)));
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(Page("", ApiKit.Fleet4)));
        kit.Server.On(HttpMethod.Get, "fleet/4/aircraft", FakeResponse.Ok(Page("", withAircraft ? ApiKit.Aircraft8 : "")));
        kit.Server.On(HttpMethod.Get, "routings", FakeResponse.Ok(Page("", ApiKit.Routing9)));
        kit.Server.On(HttpMethod.Get, "routes", FakeResponse.Ok(Page("", ApiKit.Scheduled10)));
        return kit;
    }

    [Fact]
    public async Task A_refresh_reads_everything_saves_it_and_only_then_replaces_the_workspace()
    {
        var kit = Remote();
        Workspace? saved = null;
        var counts = new List<(ResourceKind, int)>();
        await kit.Api.RefreshSnapshotsAsync(staged =>
        {
            saved = staged;
            // while the staged copy is saved the live workspace is still empty
            Assert.Empty(kit.Workspace.Resources[ResourceKind.Airports].Snapshot);
            return Task.CompletedTask;
        }, Ct, (kind, count) => counts.Add((kind, count)));
        Assert.NotNull(saved);
        Assert.Equal(2, kit.Workspace.Resources[ResourceKind.Airports].Snapshot.Count);
        Assert.Single(kit.Workspace.Resources[ResourceKind.Aircraft].Snapshot);
        Assert.Single(kit.Workspace.Resources[ResourceKind.Routes].Snapshot);
        Assert.Contains("Airports:Read", kit.Workspace.VerifiedOperations);
        Assert.Contains("Aircraft:Read", kit.Workspace.VerifiedOperations);
        // the aircraft were read through the fleets that were just read: the fleet collection is requested once
        Assert.Single(kit.Server.Requests, r => r.Path.EndsWith("/fleet", StringComparison.Ordinal));
        // the refreshed records are known to the session
        kit.Server.On(HttpMethod.Get, "fleet/4/aircraft/8", ApiKit.Data(ApiKit.Aircraft8));
        Assert.NotNull(await kit.Api.FindAsync(ResourceKind.Aircraft, "8", Ct));
    }

    [Fact]
    public async Task A_refresh_that_fails_to_read_leaves_the_workspace_and_the_session_as_they_were()
    {
        var kit = Remote();
        kit.Server.On(HttpMethod.Get, "routes", FakeResponse.WithStatus(422));
        await Assert.ThrowsAsync<ApiResponseException>(() => kit.Api.RefreshSnapshotsAsync(_ => Task.CompletedTask, Ct));
        Assert.All(Enum.GetValues<ResourceKind>(), kind => Assert.Empty(kit.Workspace.Resources[kind].Snapshot));
        Assert.Empty(kit.Workspace.VerifiedOperations);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.FindAsync(ResourceKind.Aircraft, "8", Ct));
        Assert.Equal("ApiRefreshFirst", MessageErrors.Describe(error).Code);
    }

    [Fact]
    public async Task A_refresh_that_fails_to_save_leaves_the_workspace_as_it_was()
    {
        var kit = Remote();
        await Assert.ThrowsAsync<IOException>(() => kit.Api.RefreshSnapshotsAsync(_ => throw new IOException("disk full"), Ct));
        Assert.All(Enum.GetValues<ResourceKind>(), kind => Assert.Empty(kit.Workspace.Resources[kind].Snapshot));
        Assert.Empty(kit.Workspace.VerifiedOperations);
    }

    [Fact]
    public async Task A_cancelled_refresh_changes_nothing()
    {
        var kit = Remote();
        using var cts = new CancellationTokenSource();
        // the last collection is read, then the caller gives up: nothing may be saved or replaced
        kit.Server.On(HttpMethod.Get, "routes", _ => { cts.Cancel(); return FakeResponse.Ok(Page("", ApiKit.Scheduled10)); });
        var saved = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kit.Api.RefreshSnapshotsAsync(_ => { saved = true; return Task.CompletedTask; }, cts.Token));
        Assert.False(saved);
        Assert.All(Enum.GetValues<ResourceKind>(), kind => Assert.Empty(kit.Workspace.Resources[kind].Snapshot));
    }
    [Fact]
    public async Task A_refresh_binds_a_workspace_without_an_airline_to_the_one_it_read()
    {
        var kit = Remote();
        kit.Workspace.AirlineId = null;
        await kit.Api.RefreshSnapshotsAsync(_ => Task.CompletedTask, Ct);
        Assert.Equal("7", kit.Workspace.AirlineId);
        Assert.Equal("7", kit.Api.SessionAirlineId);
    }

    // ---- C11: assertions of the C3 scenarios that were only matched by test name ----------------------------------

    [Fact]
    public async Task Aircraft_rows_remember_their_fleet_and_a_single_aircraft_answers_in_data()
    {
        var kit = new ApiKit();
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(Page("", ApiKit.Fleet4)));
        kit.Server.On(HttpMethod.Get, "fleet/4/aircraft", FakeResponse.Ok(Page("", ApiKit.Aircraft8)));
        var rows = await All(kit.Api.ReadAllAsync(ResourceKind.Aircraft, Ct));
        Assert.Equal("4", Assert.Single(rows).Identity!.ParentFleetId);

        kit.Server.On(HttpMethod.Get, "fleet/4/aircraft/8", ApiKit.Data(ApiKit.Aircraft8));
        Assert.Equal("Plane", (await kit.Api.FindAsync(ResourceKind.Aircraft, "8", Ct))!.Get("Name"));
    }

    // ---- C11: what the old Contract and Lifecycle scenarios checked and the tests above did not state --------------------

    [Fact]
    public async Task A_refresh_asks_for_pages_of_100_with_reads_only_and_files_the_routes_without_an_airline_under_the_one_it_read()
    {
        var kit = Remote();
        kit.Workspace.AirlineId = null;
        await kit.Api.RefreshSnapshotsAsync(_ => Task.CompletedTask, Ct);
        Assert.All(kit.Server.Requests, r =>
        {
            Assert.Equal("GET", r.Method);
            Assert.Contains("page[size]=100", Uri.UnescapeDataString(r.PathAndQuery), StringComparison.Ordinal);
        });
        var route = kit.Workspace.Resources[ResourceKind.Routes].Snapshot.Single();
        Assert.Equal("7", route.Identity!.ConnectionId);
        Assert.Equal("EGLL", route.Get("Departure Airport (ICAO/IATA)"));
        Assert.Equal("EGKK", route.Get("Arrival Airport (ICAO/IATA)"));
        Assert.Equal("7", kit.Api.SessionAirlineId);
    }

    [Fact]
    public async Task Records_of_another_airline_stop_a_read_and_leave_the_bound_airline_alone()
    {
        var kit = new ApiKit();
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(Page("", """{"id":4,"airline_id":8,"name":"Other"}""")));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => All(kit.Api.ReadAllAsync(ResourceKind.Fleets, Ct)));
        Assert.Equal("ApiWrongVa", MessageErrors.Describe(error).Code);
        Assert.Equal("7", kit.Workspace.AirlineId);
        Assert.Null(kit.Api.SessionAirlineId);
        Assert.All(kit.Server.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("read-failure")]
    [InlineData("cancel")]
    [InlineData("save-failure")]
    public async Task A_refresh_swaps_the_airport_index_together_with_the_workspace_or_not_at_all(string mode)
    {
        var kit = new ApiKit();
        var old = kit.Map(ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR"}""");
        kit.Workspace.Resources[ResourceKind.Airports].Snapshot.Add(old.Dup());
        kit.Workspace.Resources[ResourceKind.Airports].Draft.Add(old.Dup());
        var before = kit.Workspace.Serialize();
        using var cts = new CancellationTokenSource();
        kit.Server.On(HttpMethod.Get, "airports", FakeResponse.Ok(Page("", """{"id":103,"airline_id":7,"icao":"EGLL","iata":"LHR"}""", """{"id":102,"airline_id":7,"icao":"EGKK","iata":"LGW"}""")));
        foreach (var path in new[] { "fleet", "routings", "routes" })
            kit.Server.On(HttpMethod.Get, path, _ =>
            {
                if (mode == "read-failure")
                    throw new TimeoutException("simulated");
                if (mode != "cancel")
                    return FakeResponse.Ok(Page(""));
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });
        string? saved = null;
        var refresh = kit.Api.RefreshSnapshotsAsync(staged =>
        {
            if (mode == "save-failure")
                throw new IOException("save failed");
            saved = staged.Serialize();
            return Task.CompletedTask;
        }, cts.Token);
        if (mode == "success")
            await refresh;
        else
            await Assert.ThrowsAnyAsync<Exception>(() => refresh);

        static ChangeItem Routing(string code) => ApiKit.Create(ResourceKind.Routings, ("Departure Airport (ICAO/IATA)", code), ("Arrival Airport (ICAO/IATA)", code), ("Route String", "DCT"));
        Assert.Equal(mode == "success" ? 103L : 101L, kit.Api.Plan(Routing("LHR"))[0].Body["departure_airport_id"]);
        if (mode == "success")
        {
            Assert.Equal(saved, kit.Workspace.Serialize());
            // the id of the airport before the refresh is not an airport any more
            Assert.ThrowsAny<InvalidOperationException>(() => kit.Api.Plan(Routing("101")));
        }
        else
        {
            Assert.Null(saved);
            Assert.Equal(before, kit.Workspace.Serialize());
        }
    }
}
