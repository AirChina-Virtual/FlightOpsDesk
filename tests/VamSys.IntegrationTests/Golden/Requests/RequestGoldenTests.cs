using System.Net;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Golden.Requests;

/// <summary>
/// Characterization tests (invariant I07): each scenario runs a complete in-process online job (change planner,
/// batch executor, adapter, transport) against a fake Operations API and compares the full request sequence with
/// Expected/&lt;scenario&gt;.json. Regenerate with VAMSYS_UPDATE_GOLDEN=1 only after reviewing why requests changed.
/// </summary>
public class RequestGoldenTests
{
    static readonly (string, string) NewFleetName = ("Name", "机型 New");

    // ---- create / update / delete for each resource ------------------------------------------------------------

    [Fact]
    public async Task Airport_create()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "Stansted"));
        await w.RunAsync();
        w.Verify("airport_create");
    }

    [Fact]
    public async Task Airport_update()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Airports, "101", ("Name", "London Heathrow"));
        await w.RunAsync();
        w.Verify("airport_update");
    }

    [Fact]
    public async Task Airport_delete()
    {
        var w = new GoldenWorld();
        w.MarkDelete(ResourceKind.Airports, "103");
        await w.RunAsync();
        w.Verify("airport_delete");
    }

    [Fact]
    public async Task Fleet_create_with_parameter_ids()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Fleets, NewFleetName, ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"), ("Parameter IDs", "1"));
        await w.RunAsync();
        w.Verify("fleet_create_with_parameter_ids");
    }

    [Fact]
    public async Task Fleet_update()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Fleets, "4", ("Name", "Fleet 4 renamed"));
        await w.RunAsync();
        w.Verify("fleet_update");
    }

    [Fact]
    public async Task Fleet_delete()
    {
        var w = new GoldenWorld();
        w.MarkDelete(ResourceKind.Fleets, "6");
        await w.RunAsync();
        w.Verify("fleet_delete");
    }

    [Fact]
    public async Task Aircraft_create()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Aircraft, ("Name", "Plane Two"), ("Registration", "QA-TEST"), ("Fleet ID", "4"), ("Freight", "2205"));
        await w.RunAsync();
        w.Verify("aircraft_create");
    }

    [Fact]
    public async Task Aircraft_update()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Aircraft, "8", ("Name", "Plane renamed"));
        await w.RunAsync();
        w.Verify("aircraft_update");
    }

    [Fact]
    public async Task Aircraft_delete()
    {
        var w = new GoldenWorld();
        w.MarkDelete(ResourceKind.Aircraft, "8");
        await w.RunAsync();
        w.Verify("aircraft_delete");
    }

    [Fact]
    public async Task Routing_create()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Routings, ("Departure Airport (ICAO/IATA)", "EGLL"), ("Arrival Airport (ICAO/IATA)", "EGCC"), ("Route String", "DCT"), ("Tags", "a,b"));
        await w.RunAsync();
        w.Verify("routing_create");
    }

    [Fact]
    public async Task Routing_update()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Routings, "9", ("Remarks", "updated remarks"));
        await w.RunAsync();
        w.Verify("routing_update");
    }

    [Fact]
    public async Task Routing_delete()
    {
        var w = new GoldenWorld();
        w.MarkDelete(ResourceKind.Routings, "9");
        await w.RunAsync();
        w.Verify("routing_delete");
    }

    // ---- route variants: scheduled and jumpseat ----------------------------------------------------------------

    [Fact]
    public async Task Route_scheduled_create()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Routes, ("Type", "scheduled"), ("Departure Airport (ICAO/IATA)", "EGLL"), ("Arrival Airport (ICAO/IATA)", "EGCC"),
            ("Callsign", "CAO200"), ("Flight Number", "CA200"), ("Fleet IDs", "4,5"), ("Departure Time (HH:MM)", "08:30"), ("Arrival Time (HH:MM)", "10:15"),
            ("Altitude", "35000"), ("Tags", "x,y"), ("Remarks", "new scheduled"), ("Is Hidden", "FALSE"));
        await w.RunAsync();
        w.Verify("route_scheduled_create");
    }

    [Fact]
    public async Task Route_scheduled_update()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Routes, "10", ("Remarks", "scheduled remarks changed"));
        await w.RunAsync();
        w.Verify("route_scheduled_update");
    }

    [Fact]
    public async Task Route_scheduled_delete()
    {
        var w = new GoldenWorld();
        w.MarkDelete(ResourceKind.Routes, "10");
        await w.RunAsync();
        w.Verify("route_scheduled_delete");
    }

    [Fact]
    public async Task Route_jumpseat_create()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Routes, ("Type", "jumpseat"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"),
            ("Departure Airport (ICAO/IATA)", "EGKK"), ("Arrival Airport (ICAO/IATA)", "EGCC"), ("Remarks", "new jumpseat"), ("Is Hidden", "TRUE"));
        await w.RunAsync();
        w.Verify("route_jumpseat_create");
    }

    [Fact]
    public async Task Route_jumpseat_update()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Routes, "11", ("Remarks", "jumpseat remarks changed"));
        await w.RunAsync();
        w.Verify("route_jumpseat_update");
    }

    [Fact]
    public async Task Route_jumpseat_delete()
    {
        var w = new GoldenWorld();
        w.MarkDelete(ResourceKind.Routes, "11");
        await w.RunAsync();
        w.Verify("route_jumpseat_delete");
    }

    [Fact]
    public async Task Route_scheduled_fleet_ids_replace()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Routes, "10", ("Fleet IDs", "5"));
        await w.RunAsync();
        w.Verify("route_scheduled_fleet_ids_replace");
    }

    [Fact]
    public async Task Route_scheduled_fleet_ids_append()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Routes, "10", ("Fleet IDs", "4,5"));
        await w.RunAsync();
        w.Verify("route_scheduled_fleet_ids_append");
    }

    [Fact]
    public async Task Route_scheduled_fleet_ids_remove()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Routes, "12", ("Fleet IDs", "4"));
        await w.RunAsync();
        w.Verify("route_scheduled_fleet_ids_remove");
    }

    [Fact]
    public async Task Route_update_time_keeps_original_seconds()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Routes, "12", ("Departure Time (HH:MM)", "00:10"));
        await w.RunAsync();
        w.Verify("route_update_time_keeps_original_seconds");
    }

    // ---- other specific variants ------------------------------------------------------------------------------

    [Fact]
    public async Task Aircraft_move_between_fleets_uses_patch()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Aircraft, "8", ("Fleet ID", "5"));
        await w.RunAsync();
        w.Verify("aircraft_move_between_fleets_uses_patch");
    }

    [Fact]
    public async Task Airport_container_ids_cleared_sends_empty_array()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Airports, "101", ("Container IDs", ""));
        await w.RunAsync();
        w.Verify("airport_container_ids_cleared_sends_empty_array");
    }

    [Fact]
    public async Task Airport_container_ids_replaced()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Airports, "101", ("Container IDs", "2,3"));
        await w.RunAsync();
        w.Verify("airport_container_ids_replaced");
    }

    [Fact]
    public async Task Update_with_cleared_draft_field_is_blocked_before_any_request()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Routings, "9", ("Remarks", ""));
        await w.RunAsync();
        Assert.Empty(w.Remote.Server.Requests);
        w.Verify("update_cleared_field_blocked_before_any_request");
    }

    [Fact]
    public async Task Update_with_unspecified_field_omits_it_from_body()
    {
        var w = new GoldenWorld();
        var before = w.Workspace.Resources[ResourceKind.Routings].Snapshot.Single().Copy();
        var after = before.Copy();
        after.Fields["Remarks"] = "changed";
        w.Workspace.Resources[ResourceKind.Routings].ReplaceDraftRows([after]);
        // Built by hand: the planner never produces Unspecified, but saved tasks and the review screen can.
        var item = new ChangeItem
        {
            Resource = ResourceKind.Routings,
            Kind = ChangeKind.Update,
            Before = before,
            After = after,
            Fields = new() { ["Remarks"] = new(FieldIntent.Set, "changed"), ["Internal Remarks"] = new(FieldIntent.Unspecified, null) }
        };
        w.Begin("run");
        var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
        await new OperationsBatchExecutor(w.Api, w.Workspace).ExecuteAsync(job, () => Task.CompletedTask, default);
        w.Note($"Routings Update {item.State}");
        var put = Assert.Single(w.Remote.Server.Requests, r => r.Method == "PUT");
        Assert.Equal("{\"remarks\":\"changed\"}", put.Body);
        w.Verify("update_unspecified_field_omitted_from_body");
    }

    // ---- duplicate checks --------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_blocked_when_duplicate_check_finds_same_fleet_name()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Fleets, ("Name", "fleet 4"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Parameter IDs", "1"));
        await w.RunAsync();
        Assert.DoesNotContain(w.Remote.Server.Requests, r => r.Method == "POST");
        w.Verify("create_duplicate_fleet_found_nothing_sent");
    }

    [Fact]
    public async Task Create_blocked_when_duplicate_check_finds_same_airport_alias()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Airports, ("ICAO/IATA", "LHR"), ("Name", "Another London"));
        await w.RunAsync();
        Assert.DoesNotContain(w.Remote.Server.Requests, r => r.Method == "POST");
        w.Verify("create_duplicate_airport_found_nothing_sent");
    }

    [Fact]
    public async Task Create_blocked_when_duplicate_check_finds_same_route()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Routes, ("Type", "scheduled"), ("Departure Airport (ICAO/IATA)", "EGLL"), ("Arrival Airport (ICAO/IATA)", "EGKK"),
            ("Callsign", "CAO100"), ("Flight Number", "ca100"), ("Fleet IDs", "4"));
        await w.RunAsync();
        Assert.DoesNotContain(w.Remote.Server.Requests, r => r.Method == "POST");
        w.Verify("create_duplicate_route_found_nothing_sent");
    }

    [Fact]
    public async Task Create_proceeds_when_candidates_found_are_not_duplicates()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Routes, ("Type", "scheduled"), ("Departure Airport (ICAO/IATA)", "EGLL"), ("Arrival Airport (ICAO/IATA)", "EGKK"),
            ("Callsign", "CAO101"), ("Flight Number", "CA101"), ("Fleet IDs", "4"));
        await w.RunAsync();
        Assert.Contains(w.Remote.Server.Requests, r => r.Method == "POST");
        w.Verify("create_route_candidates_found_but_not_duplicate");
    }

    // ---- rate limit, timeout and recovery ---------------------------------------------------------------------

    [Fact]
    public async Task Write_429_returns_to_pending_and_is_sent_once_on_resume()
    {
        var w = new GoldenWorld();
        var limited = false;
        w.Remote.Intercept = r =>
        {
            if (r.Method != "PUT" || limited)
                return null;
            limited = true;
            return new FakeResponse((HttpStatusCode)429, "{}", new Dictionary<string, string> { ["Retry-After"] = "30" });
        };
        w.Edit(ResourceKind.Fleets, "4", ("Name", "Fleet 4 renamed"));
        await w.RunAsync("first run");
        Assert.Equal(ItemState.Pending, w.Job!.Items.Single().State);
        await w.ResumeAsync("resume after 429");
        Assert.Equal(ItemState.Succeeded, w.Job.Items.Single().State);
        Assert.Equal(2, w.Remote.Server.Requests.Count(r => r.Method == "PUT"));
        w.Verify("write_429_then_resume");
    }

    [Fact]
    public async Task Write_timeout_not_applied_is_recovered_read_only_and_never_resent()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "PUT" ? throw new TimeoutException("simulated write timeout") : null;
        w.Edit(ResourceKind.Fleets, "4", ("Name", "Fleet 4 renamed"));
        await w.RunAsync("first run");
        Assert.Equal(ItemState.Unknown, w.Job!.Items.Single().State);
        await w.ResumeAsync("recovery");
        await w.ResumeAsync("recovery again");
        Assert.Equal(ItemState.Unknown, w.Job.Items.Single().State);
        Assert.Equal(1, w.Remote.Server.Requests.Count(r => r.Method == "PUT"));
        w.Verify("write_timeout_not_applied_recovery_is_read_only");
    }

    [Fact]
    public async Task Write_timeout_applied_by_server_is_confirmed_by_read_without_resend()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r =>
        {
            if (r.Method != "PUT")
                return null;
            w.Remote.Respond(r);
            throw new TimeoutException("simulated lost response");
        };
        w.Edit(ResourceKind.Fleets, "4", ("Name", "Fleet 4 renamed"));
        await w.RunAsync("first run");
        Assert.Equal(ItemState.Unknown, w.Job!.Items.Single().State);
        await w.ResumeAsync("recovery");
        Assert.Equal(ItemState.Succeeded, w.Job.Items.Single().State);
        Assert.Equal(1, w.Remote.Server.Requests.Count(r => r.Method == "PUT"));
        w.Verify("write_timeout_applied_recovery_confirms_without_resend");
    }

    [Fact]
    public async Task Create_post_timeout_is_never_resent_during_recovery()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "POST" ? throw new TimeoutException("simulated create timeout") : null;
        w.New(ResourceKind.Fleets, NewFleetName, ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Parameter IDs", "1"));
        await w.RunAsync("first run");
        Assert.Equal(ItemState.Unknown, w.Job!.Items.Single().State);
        await w.ResumeAsync("recovery");
        Assert.Equal(1, w.Remote.Server.Requests.Count(r => r.Method == "POST"));
        w.Verify("create_post_timeout_unknown_never_resent");
    }

    // ---- several changes in one job: dependency and execution order ------------------------------------------

    [Fact]
    public async Task Mixed_job_runs_creates_in_resource_order_and_deletes_in_reverse()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "Stansted"));
        var fleet = w.New(ResourceKind.Fleets, NewFleetName, ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Parameter IDs", "1"));
        w.New(ResourceKind.Aircraft, ("Name", "Plane Three"), ("Registration", "QA-NEW"), ("Fleet ID", "local:" + fleet.LocalId));
        w.Edit(ResourceKind.Routings, "9", ("Remarks", "mixed job"));
        w.MarkDelete(ResourceKind.Routes, "10");
        w.MarkDelete(ResourceKind.Airports, "103");
        await w.RunAsync();
        w.Verify("job_mixed_ordering_and_local_fleet_reference");
    }

    // ---- refresh ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_two_airports_with_the_same_iata()
    {
        var w = new GoldenWorld(standard: false);
        w.Remote.AddRow("airports/101", """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"London"}""");
        w.Remote.AddRow("fleet/4", """{"id":4,"airline_id":7,"name":"Fleet 4","code":"B738","type":"cargo"}""");
        w.Remote.AddRow("fleet/4/aircraft/8", """{"id":8,"airline_id":7,"fleet_id":4,"registration":"B-TEST","name":"Plane"}""");
        w.LoadRemote();
        w.Remote.AddRow("airports/104", """{"id":104,"airline_id":7,"icao":"EGLC","iata":"LHR","name":"London City (same IATA)"}""");
        w.Begin("refresh");
        try
        { await w.Api.RefreshSnapshotsAsync(_ => Task.CompletedTask, default); w.Note("refresh completed"); }
        catch (Exception e) { w.Note("refresh threw " + e.GetType().Name); }
        w.Note("airport snapshot rows " + w.Workspace.Resources[ResourceKind.Airports].Snapshot.Count);
        w.Verify("refresh_two_airports_same_iata");
    }

    [Fact]
    public async Task Refresh_all_resources_with_pagination()
    {
        var w = new GoldenWorld();
        w.Remote.AddRow("fleet/4/aircraft/15", """{"id":15,"airline_id":7,"fleet_id":4,"registration":"B-TWO","name":"Plane 15"}""");
        w.Remote.PageSizes["airports"] = 2;           // 3 airports: 2 pages
        w.Remote.PageSizes["fleet"] = 2;              // 3 fleets: 2 pages
        w.Remote.PageSizes["fleet/4/aircraft"] = 1;   // 2 aircraft: 2 pages
        w.Remote.PageSizes["routes"] = 2;             // 3 routes: 2 pages
        w.Begin("refresh");
        await w.Api.RefreshSnapshotsAsync(_ => Task.CompletedTask, default);
        foreach (var kind in Enum.GetValues<ResourceKind>())
            w.Note($"{kind} snapshot rows {w.Workspace.Resources[kind].Snapshot.Count}");
        w.Verify("refresh_all_resources_with_pagination");
    }
}
