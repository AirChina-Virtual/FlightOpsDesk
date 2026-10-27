using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Api;

/// <summary>How many requests the duplicate check of a job costs: a full list or an airport pair is read once and to its last page.</summary>
public class DuplicateReadCountTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    static int Gets(GoldenWorld w, string collection)
        => w.Remote.Server.Requests.Count(r => r.Method == "GET" && r.Path.EndsWith("/" + collection, StringComparison.Ordinal));

    static (string, string)[] Scheduled(string number) => [("Type", "scheduled"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO" + number[2..]), ("Flight Number", number), ("Fleet IDs", "4")];

    static (string, string)[] NewFleet(string name) => [("Name", name), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"), ("Parameter IDs", "1")];

    static void AddRouting(GoldenWorld w, int id, string route)
        => w.Remote.AddRow("routings/" + id, $$"""{"id":{{id}},"airline_id":7,"departure_airport_id":101,"arrival_airport_id":102,"route":"{{route}}"}""");

    static DataRow InsertFirst(GoldenWorld w, ResourceKind kind, params (string, string)[] fields)
    {
        var row = new DataRow { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };
        w.Workspace.Resources[kind].Draft.Insert(0, row);
        return row;
    }

    // ---- paging ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_route_that_duplicates_a_record_on_the_last_page_of_its_airport_pair_is_refused()
    {
        var w = new GoldenWorld();
        w.Remote.PageSizes["routes"] = 1; // routes 10, 11 and 12 are on the pair: three pages
        w.New(ResourceKind.Routes, Scheduled("CA120"));
        await w.RunAsync();
        Assert.Equal("ApiDuplicate", Code(w.Job!.Items.Single()));
        Assert.Equal(ItemState.Failed, w.Job.Items.Single().State);
        Assert.Equal(3, Gets(w, "routes"));
        Assert.Equal(0, Count(w, "POST"));
        var first = w.Remote.Server.Requests.First(r => r.Path.EndsWith("/routes", StringComparison.Ordinal));
        var query = Uri.UnescapeDataString(first.PathAndQuery);
        Assert.Contains("filter[departure_id]=101", query, StringComparison.Ordinal);
        Assert.Contains("filter[arrival_id]=102", query, StringComparison.Ordinal);
        Assert.DoesNotContain("flight_number", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_routing_that_duplicates_a_record_on_the_second_page_of_its_airport_pair_is_refused()
    {
        var w = new GoldenWorld();
        AddRouting(w, 8, "UL9");
        w.Remote.PageSizes["routings"] = 1;
        w.New(ResourceKind.Routings, (Dep, "EGLL"), (Arr, "EGKK"), ("Route String", "DCT"));
        await w.RunAsync();
        Assert.Equal("ApiDuplicate", Code(w.Job!.Items.Single()));
        Assert.Equal(2, Gets(w, "routings"));
        Assert.Equal(0, Count(w, "POST"));
    }

    [Fact]
    public async Task A_pair_with_several_pages_is_read_to_the_end_before_a_create_that_is_not_a_duplicate()
    {
        var w = new GoldenWorld();
        w.Remote.PageSizes["routes"] = 1;
        w.New(ResourceKind.Routes, Scheduled("CA555"));
        await w.RunAsync();
        Assert.Equal(ItemState.Succeeded, w.Job!.Items.Single().State);
        Assert.Equal(3, Gets(w, "routes"));
        Assert.Equal(1, Count(w, "POST"));
    }

    // ---- one read per pair or list -----------------------------------------------------------------------------------

    [Fact]
    public async Task Twenty_route_creates_read_their_pair_once_and_a_repeat_of_a_new_route_is_found_without_a_request()
    {
        var w = new GoldenWorld();
        w.Remote.PageSizes["routes"] = 2; // two pages for the three routes on the pair
        for (var i = 0; i < 20; i++)
            w.New(ResourceKind.Routes, Scheduled("CA" + (900 + i)));
        w.New(ResourceKind.Routes, Scheduled("CA120")); // on the second page
        w.New(ResourceKind.Routes, Scheduled("ca900")); // created earlier in the job
        await w.RunAsync();
        Assert.All(w.Job!.Items.Take(20), i => Assert.Equal(ItemState.Succeeded, i.State));
        Assert.Equal(["ApiDuplicate", "ApiDuplicate"], w.Job.Items.Skip(20).Select(Code));
        Assert.Equal(2, Gets(w, "routes"));
        Assert.Equal(20, Count(w, "POST"));
    }

    [Fact]
    public async Task Twenty_routing_creates_read_their_pair_once_and_a_repeat_of_a_new_routing_is_found_without_a_request()
    {
        var w = new GoldenWorld();
        AddRouting(w, 20, "UL1");
        AddRouting(w, 21, "UL2");
        w.Remote.PageSizes["routings"] = 2;
        for (var i = 0; i < 20; i++)
            w.New(ResourceKind.Routings, (Dep, "EGLL"), (Arr, "EGKK"), ("Route String", "R" + i.ToString("000")));
        w.New(ResourceKind.Routings, (Dep, "EGLL"), (Arr, "EGKK"), ("Route String", "UL2"));
        w.New(ResourceKind.Routings, (Dep, "EGLL"), (Arr, "EGKK"), ("Route String", "r000"));
        await w.RunAsync();
        Assert.All(w.Job!.Items.Take(20), i => Assert.Equal(ItemState.Succeeded, i.State));
        Assert.Equal(["ApiDuplicate", "ApiDuplicate"], w.Job.Items.Skip(20).Select(Code));
        Assert.Equal(2, Gets(w, "routings"));
        Assert.Equal(20, Count(w, "POST"));
    }

    [Fact]
    public async Task One_hundred_route_creates_on_a_pair_with_no_routes_cost_one_filtered_list_among_20000_routes_of_other_pairs()
    {
        var w = new GoldenWorld();
        for (var i = 0; i < 20000; i++)
            w.Remote.AddRow("routes/" + (1000 + i), $$"""{"id":{{1000 + i}},"departure_id":{{2000 + i}},"arrival_id":{{40000 + i}},"type":"scheduled","callsign":"CAO1","flight_number":"CA1","fleet_ids":[4]}""");
        for (var i = 0; i < 100; i++)
            w.New(ResourceKind.Routes, ("Type", "scheduled"), (Dep, "EGLL"), (Arr, "EGCC"), ("Callsign", "CAO" + (100 + i)), ("Flight Number", "CA" + (100 + i)), ("Fleet IDs", "4"));
        await w.RunAsync();
        Assert.All(w.Job!.Items, i => Assert.Equal(ItemState.Succeeded, i.State));
        Assert.Equal(1, Gets(w, "routes"));
        Assert.Equal(100, Count(w, "POST"));
        Assert.Equal(100, w.Remote.Server.Requests.Count(r => r.Method == "GET" && r.Path.Contains("/routes/", StringComparison.Ordinal)));
    }

    // ---- the pair cache follows confirmed writes ------------------------------------------------------------------

    [Fact]
    public async Task A_route_renamed_earlier_in_the_job_frees_its_old_flight_number_and_blocks_the_new_one_in_the_pair_cache()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Routes, "10", ("Flight Number", "CA777"));
        InsertFirst(w, ResourceKind.Routes, Scheduled("CA801")); // reads the pair before the update is confirmed
        w.New(ResourceKind.Routes, Scheduled("CA100"));
        w.New(ResourceKind.Routes, Scheduled("CA777"));
        await w.RunAsync();
        var items = w.Job!.Items;
        Assert.Equal(ItemState.Succeeded, items[0].State);
        Assert.Equal(ItemState.Succeeded, items[1].State);
        Assert.Equal(ItemState.Succeeded, items[2].State);
        Assert.Equal("ApiDuplicate", Code(items[3]));
        Assert.Equal(1, Gets(w, "routes"));
        Assert.Equal(1, w.Remote.Rows.Values.Count(r => r["flight_number"]?.ToString() == "CA100"));
    }

    [Fact]
    public async Task A_routing_changed_earlier_in_the_job_frees_its_old_route_string_and_blocks_the_new_one_in_the_pair_cache()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Routings, "9", ("Route String", "MID"));
        InsertFirst(w, ResourceKind.Routings, (Dep, "EGLL"), (Arr, "EGKK"), ("Route String", "FIRST"));
        w.New(ResourceKind.Routings, (Dep, "EGLL"), (Arr, "EGKK"), ("Route String", "DCT"));
        w.New(ResourceKind.Routings, (Dep, "EGLL"), (Arr, "EGKK"), ("Route String", "MID"));
        await w.RunAsync();
        var items = w.Job!.Items;
        Assert.All(items.Take(3), i => Assert.Equal(ItemState.Succeeded, i.State));
        Assert.Equal("ApiDuplicate", Code(items[3]));
        Assert.Equal(1, Gets(w, "routings"));
    }

    // ---- whole-list resources -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_airport_create_is_refused_after_one_unfiltered_list_when_a_code_matches_in_any_case()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Airports, ("ICAO/IATA", "lhr"), ("Name", "London"));
        await w.RunAsync();
        Assert.Equal("ApiDuplicate", Code(w.Job!.Items.Single()));
        var request = Assert.Single(w.Remote.Server.Requests);
        Assert.DoesNotContain("filter", Uri.UnescapeDataString(request.PathAndQuery), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_aircraft_registration_is_compared_across_every_fleet_so_one_list_per_fleet_is_read()
    {
        var w = new GoldenWorld();
        w.Remote.AddRow("fleet/5/aircraft/9", """{"id":9,"airline_id":7,"fleet_id":5,"registration":"G-TEST","name":"Other plane"}""");
        w.New(ResourceKind.Aircraft, ("Fleet ID", "4"), ("Name", "Plane"), ("Registration", "g-test"));
        await w.RunAsync();
        Assert.Equal("ApiDuplicate", Code(w.Job!.Items.Single()));
        Assert.Equal(["GET fleet", "GET fleet/4/aircraft", "GET fleet/5/aircraft", "GET fleet/6/aircraft"], Calls(w).Order());
        Assert.Equal(0, Count(w, "POST"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_create_that_succeeded_or_may_have_succeeded_blocks_a_same_named_create_without_a_second_list(bool outcomeUnknown)
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Fleets, NewFleet("Twin fleet"));
        w.New(ResourceKind.Fleets, NewFleet("TWIN FLEET"));
        if (outcomeUnknown)
            w.Remote.Intercept = r => r.Method == "POST" ? throw new TimeoutException("simulated") : null;
        await w.RunAsync();
        var second = w.Job!.Items[1];
        Assert.Equal(outcomeUnknown ? ItemState.Unknown : ItemState.Succeeded, w.Job.Items[0].State);
        Assert.Equal(outcomeUnknown ? ItemState.Pending : ItemState.Failed, second.State);
        Assert.Equal(outcomeUnknown ? "ApiIdentityPending" : "ApiDuplicate", Code(second));
        Assert.Equal(1, Count(w, "POST"));
        Assert.Equal(1, Gets(w, "fleet"));
    }
}
