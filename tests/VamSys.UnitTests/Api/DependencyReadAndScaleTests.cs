using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Api;

/// <summary>
/// Reads the adapter makes to protect a delete (what still depends on the record), how a refresh stages its result, and
/// the behaviours that must stay exact when there are thousands of rows. Counts are request counts, never timings.
/// </summary>
public class DependencyReadAndScaleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string List(params string[] records) => "{\"data\":[" + string.Join(',', records) + "],\"meta\":{\"next_cursor_url\":null}}";

    static string RelativePath(RecordedRequest r) => r.Path[OperationsAdapter.BaseUri.AbsolutePath.Length..];

    // ---- what depends on an airport -----------------------------------------------------------------------------

    const string SelfRoute = """{"id":9,"departure_id":101,"arrival_id":101,"type":"scheduled","callsign":"ACA123","flight_number":"AC123","fleet_ids":[4]}""";

    static (ApiKit Kit, List<string> WrongQueries, ChangeItem Delete) AirportDependants(string mode)
    {
        var kit = new ApiKit();
        var wrongQueries = new List<string>();
        FakeResponse Answer(RecordedRequest r)
        {
            var at = r.PathAndQuery.IndexOf('?', StringComparison.Ordinal);
            var q = Uri.UnescapeDataString(at < 0 ? "" : r.PathAndQuery[(at + 1)..]);
            // every dependency query is one side of the airport only (departure OR arrival), never both at once
            if (q.Contains("departure", StringComparison.Ordinal) == q.Contains("arrival", StringComparison.Ordinal))
                wrongQueries.Add(q);
            if (mode == "failure")
                throw new TimeoutException("simulated");
            if (mode == "second-page" && q.Contains("cursor", StringComparison.Ordinal))
                return FakeResponse.Ok(List(SelfRoute));
            if (mode == "second-page" && q.Contains("departure_id", StringComparison.Ordinal))
                return FakeResponse.Ok("{\"data\":[],\"meta\":{\"next_cursor_url\":\"" + OperationsAdapter.BaseUri + "routes?filter%5Bdeparture_id%5D=101&cursor=2\"}}");
            var found = (mode == "self" && r.Path.EndsWith("/routes", StringComparison.Ordinal))
                || (mode == "departure" && q.Contains("departure_id", StringComparison.Ordinal))
                || (mode == "arrival" && q.Contains("arrival_id", StringComparison.Ordinal));
            return FakeResponse.Ok(List(found ? SelfRoute : ""));
        }
        kit.Server.On(HttpMethod.Get, "routes", Answer).On(HttpMethod.Get, "routings", Answer);
        return (kit, wrongQueries, ApiKit.Delete(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll)));
    }

    [Theory]
    [InlineData("departure", true)]
    [InlineData("arrival", true)]
    [InlineData("second-page", true)]
    [InlineData("self", true)]
    [InlineData("empty", false)]
    public async Task An_airport_has_dependants_when_a_route_or_routing_names_it_on_either_side_on_any_page(string mode, bool expected)
    {
        var (kit, wrongQueries, item) = AirportDependants(mode);
        Assert.Equal(expected, await kit.Api.HasDeletionDependenciesAsync(item, Ct));
        Assert.Empty(wrongQueries);
        Assert.All(kit.Server.Requests, r => Assert.Equal("GET", r.Method));
        if (mode == "empty")
            Assert.Equal(4, kit.Server.Requests.Count); // routes and routings, each by departure and by arrival
    }

    [Fact]
    public async Task A_route_from_an_airport_to_itself_is_listed_once()
    {
        var (kit, wrongQueries, item) = AirportDependants("self");
        var rows = new List<DataRow>();
        await foreach (var pair in kit.Api.ReadDependencyCandidatesAsync(item, Ct))
            rows.Add(pair.Row);
        Assert.Single(rows);
        Assert.Equal(4, kit.Server.Requests.Count);
        Assert.Empty(wrongQueries);
    }

    [Fact]
    public async Task A_dependency_read_that_fails_is_raised_not_taken_for_no_dependants()
    {
        var (kit, _, item) = AirportDependants("failure");
        await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.HasDeletionDependenciesAsync(item, Ct));
        Assert.All(kit.Server.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task A_fleet_delete_only_reads_the_aircraft_of_the_fleet_and_the_routes_filtered_by_it()
    {
        var kit = new ApiKit();
        var fleet = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        kit.Server.On(HttpMethod.Get, "fleet/4/aircraft", FakeResponse.Ok(List()));
        kit.Server.On(HttpMethod.Get, "routes", FakeResponse.Ok(List(SelfRoute)));
        Assert.True(await kit.Api.HasDeletionDependenciesAsync(ApiKit.Delete(ResourceKind.Fleets, fleet), Ct));
        Assert.Equal(["fleet/4/aircraft", "routes"], kit.Server.Requests.Select(RelativePath).Order());
        var routes = kit.Server.Requests.Single(r => r.Path.EndsWith("/routes", StringComparison.Ordinal));
        Assert.Contains("filter[fleet_id]=4", Uri.UnescapeDataString(routes.PathAndQuery), StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_values_keep_ampersands_plus_signs_and_non_ascii_text_apart_from_the_query_syntax()
    {
        var uri = OperationsAdapter.FilterUri("routes", new Dictionary<string, string> { ["tag"] = "A&B + 中" });
        Assert.Contains("%26", uri.Query, StringComparison.Ordinal);
        Assert.Contains("%2B", uri.Query, StringComparison.Ordinal);
        Assert.Contains("A&B + 中", Uri.UnescapeDataString(uri.Query), StringComparison.Ordinal);
    }

    // ---- refresh ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refresh_reads_the_fleet_list_once_and_stages_without_touching_live_rows_or_undo(bool saveFails)
    {
        var kit = new ApiKit();
        var fleets = kit.Workspace.Resources[ResourceKind.Fleets];
        fleets.Draft.Add(new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "Local" } });
        fleets.Edit([], () => { });
        var draft = fleets.Draft;
        var undo = fleets.Undo.Count;
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(List(ApiKit.Fleet4, ApiKit.Fleet5)));
        foreach (var path in new[] { "airports", "routings", "routes", "fleet/4/aircraft", "fleet/5/aircraft" })
            kit.Server.On(HttpMethod.Get, path, FakeResponse.Ok(List()));

        var refresh = kit.Api.RefreshSnapshotsAsync(_ => saveFails ? throw new IOException("save failed") : Task.CompletedTask, Ct);
        if (saveFails)
            await Assert.ThrowsAsync<IOException>(() => refresh);
        else
            await refresh;

        var paths = kit.Server.Requests.Select(RelativePath).Select(p => p.Split('?')[0]).ToArray();
        Assert.Single(paths, "fleet");
        Assert.Equal(["fleet/4/aircraft", "fleet/5/aircraft"], paths.Where(p => p.EndsWith("/aircraft", StringComparison.Ordinal)));
        if (saveFails)
        {
            Assert.Same(fleets, kit.Workspace.Resources[ResourceKind.Fleets]);
            Assert.Same(draft, fleets.Draft);
            Assert.Equal(undo, fleets.Undo.Count);
            Assert.Empty(fleets.Snapshot);
        }
        else
        {
            Assert.Equal(2, kit.Workspace.Resources[ResourceKind.Fleets].Snapshot.Count);
            Assert.Empty(kit.Workspace.Resources[ResourceKind.Fleets].Undo);
            Assert.Equal(undo, fleets.Undo.Count);
        }
    }

    // ---- exactness with many rows -------------------------------------------------------------------------------

    [Fact]
    public async Task Airport_lookups_stay_exact_across_snapshot_and_cache_changes_with_3000_airports_and_1000_route_plans()
    {
        var kit = new ApiKit();
        kit.Server.On(HttpMethod.Get, "parameters", FakeResponse.Ok(List("""{"id":1,"airline_id":7,"icao_code":"ACA","iata_code":"AC","approved":true,"restricted":false,"fleet_ids":[4]}""")));
        kit.Server.On(HttpMethod.Get, "fleet/4", ApiKit.Data(ApiKit.Fleet4));
        await kit.Api.ReadParametersAsync(Ct);
        await kit.Api.FindAsync(ResourceKind.Fleets, "4", Ct);

        // rows are mapped by another adapter so that the one under test only knows what its snapshot holds
        var factory = new ApiKit();
        static string Code(int n, int length)
        {
            var c = new char[length];
            for (var i = length - 1; i >= 0; i--)
            { c[i] = (char)('A' + n % 26); n /= 26; }
            return new string(c);
        }
        DataRow Airport(int id, string icao, string iata) => factory.Map(ResourceKind.Airports, $$"""{"id":{{id}},"airline_id":7,"icao":"{{icao}}","iata":"{{iata}}"}""");
        var airports = kit.Workspace.Resources[ResourceKind.Airports].Snapshot;
        for (var i = 0; i < 3000; i++)
            airports.Add(Airport(1000 + i, "X" + Code(i, 3), Code(i, 3)));

        static ChangeItem Route(string dep, string arr)
            => ApiKit.Create(ResourceKind.Routes, ("Departure Airport (ICAO/IATA)", dep), ("Arrival Airport (ICAO/IATA)", arr), ("Flight Number", "AC123"), ("Callsign", "ACA123"), ("Type", "scheduled"), ("Fleet IDs", "4"));
        bool Plans(string dep, string arr)
        {
            try { kit.Api.PreviewPlan(Route(dep, arr)); return true; }
            catch (InvalidOperationException) { return false; }
        }

        for (var i = 0; i < 1000; i++)
            Assert.True(Plans(i % 2 == 0 ? Code(i, 3) : "X" + Code(i, 3), (1000 + (i * 7) % 3000).ToString()), "route " + i);

        // every mutation pattern used by refresh, rebase and deletion must be visible to the next lookup
        airports.Add(Airport(9001, "XDUP", "AAF"));
        Assert.False(Plans("AAF", "AAA"), "Appended alias not ambiguous");
        airports.RemoveAt(airports.Count - 1);
        Assert.True(Plans("AAF", "AAA"), "Removed alias still ambiguous");
        var moved = airports[5];
        airports.RemoveAt(5);
        airports.Add(Airport(1005, "XNEW", "NEW"));
        Assert.False(Plans(moved.Get("ICAO/IATA"), "AAA"));
        Assert.True(Plans("NEW", "AAA"), "Replaced row not reindexed");
        kit.Map(ResourceKind.Airports, """{"id":9002,"airline_id":7,"icao":"XZZZ","iata":"NEW"}""");
        Assert.False(Plans("NEW", "AAA"), "Known alias not ambiguous");
        kit.Api.Forget(ResourceKind.Airports, "9002");
        Assert.True(Plans("NEW", "AAA"), "Forgotten airport still indexed");
        kit.Workspace.Resources[ResourceKind.Airports].Snapshot = airports.Where(r => r.Identity!.RemoteId != "1005").ToList();
        Assert.False(Plans("NEW", "AAA"), "Replaced snapshot list not reindexed");
        kit.Workspace.AirlineId = "8";
        Assert.False(Plans("AAB", "AAA"), "Other VA airports resolved");
    }

    [Fact]
    public void Unresolved_conflicts_are_kept_once_each_when_20000_rows_are_merged()
    {
        var data = new ResourceData();
        var incoming = new List<DataRow>();
        for (var i = 0; i < 20000; i++)
        {
            var id = (1 + i).ToString();
            var remote = new DataRow { Identity = new(id, "7"), Fields = new Dictionary<string, string> { ["ID"] = id, ["Name"] = "R" + i } };
            var baseline = remote.Dup();
            data.Snapshot.Add(baseline);
            data.Draft.Add(baseline.Dup());
            incoming.Add(remote.Dup());
        }
        foreach (var row in data.Draft.Take(5000))
            data.Conflicts.Add(new(row.LocalId, "Name", "a", "b", "c"));
        data.Conflicts.Add(data.Conflicts[0]);
        data.Conflicts.Add(new(Guid.NewGuid(), "Name", "a", "b", "c"));

        SnapshotMerger.Merge(ResourceKind.Fleets, data, incoming);

        Assert.Equal(5000, data.Conflicts.Count);
        Assert.Equal(5000, data.Conflicts.Select(c => c.RowId).Distinct().Count());
    }

    [Fact]
    public async Task With_20000_records_one_build_serves_every_query_and_confirmed_renames_move_single_ids()
    {
        var kit = new ApiKit();
        var session = new CandidateQuerySession();
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(List(Enumerable.Range(1, 20000).Select(i => JsonSerializer.Serialize(new { id = i, airline_id = 7, name = "Fleet" + i, code = "B738", type = "pax" })).ToArray())));
        using var metrics = PerformanceRun.Begin(Guid.NewGuid(), Guid.NewGuid());
        int candidates = 0, queries = 0;
        async Task<List<DataRow>> Read(string name)
        {
            queries++;
            var query = ApiKit.Create(ResourceKind.Fleets, ("Name", name), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"));
            var rows = new List<DataRow>();
            await foreach (var row in kit.Api.ReadDuplicateCandidatesAsync(query, session, Ct))
            {
                Assert.True(kit.Api.IsDuplicate(query, row));
                rows.Add(row);
            }
            candidates += rows.Count;
            return rows;
        }

        for (var i = 1; i <= 100; i++)
        {
            Assert.Single(await Read("Fleet" + i));
            kit.Api.ConfirmCandidate(new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Update, RemoteId = i.ToString() },
                kit.Map(ResourceKind.Fleets, JsonSerializer.Serialize(new { id = i, airline_id = 7, name = "Renamed" + i, code = "B738", type = "pax" })), session);
            Assert.Empty(await Read("Fleet" + i));
            Assert.Single(await Read("renamed" + i));
            Assert.Empty(await Read("Absent" + i));
        }

        Assert.Equal(1, metrics.IndexBuilds);
        Assert.Single(kit.Server.Requests);
        Assert.Equal(200, candidates);
        Assert.Equal(400, queries);
    }
}

/// <summary>What the performance report of one run counts and what it must never contain.</summary>
public class PerformanceMetricsTests
{
    [Fact]
    public async Task The_report_counts_oauth_requests_and_never_contains_the_credentials_or_the_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new TestTime();
        using var server = new FakeOperationsServer { RecordTokenRequests = true };
        server.On(HttpMethod.Post, "/oauth/token", FakeResponse.Ok("""{"access_token":"PRIVATE_TOKEN","token_type":"Bearer","expires_in":120}"""));
        using var metrics = PerformanceRun.Begin(Guid.NewGuid(), Guid.NewGuid());
        var provider = TokenProvider.ClientCredentials(server.CreateClient(), new("PRIVATE_ID", "PRIVATE_SECRET"), time);
        await provider.GetAsync(ct);
        time.Advance(TimeSpan.FromSeconds(59));
        await provider.GetAsync(ct);
        time.Advance(TimeSpan.FromSeconds(2));
        await provider.GetAsync(ct);

        metrics.MarkCancellation();
        metrics.Finish("Canceled");
        Assert.True(metrics.CancellationMs.HasValue);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(metrics), StringComparison.Ordinal);
        Assert.Equal(2, metrics.Requests["OAuth/Other/POST"]);

        // a report that cannot be written (the target is a file, not a folder) is reported as not written, never thrown
        var file = Path.GetTempFileName();
        try
        { Assert.False(metrics.TryWrite(file)); }
        finally
        { File.Delete(file); }
    }
}
