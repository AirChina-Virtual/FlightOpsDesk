using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// The first stages of the execution pipeline: what is checked before a write may exist (conflicts, local references,
/// dependencies, parameters, move targets) and the duplicate check of a create. Whatever fails here has sent nothing.
/// </summary>
public class PreflightAndDuplicateStageTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    static (string, string)[] NewFleet(string name) => [("Name", name), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"), ("Parameter IDs", "1")];

    // ---- preflight: the record on the server ---------------------------------------------------------------------

    [Fact]
    public async Task A_record_that_changed_on_the_server_since_the_plan_is_a_conflict_and_nothing_is_written()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Fleets, "4", ("Name", "mine"));
        w.Remote.Rows["fleet/4"]["name"] = "theirs";
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Conflict, item.State);
        Assert.Equal("ApiConflict", Code(item));
        Assert.Equal(["GET fleet/4"], Calls(w));
        Assert.Contains(item.PreflightComparison!.BusinessFields, d => d.Field == "Name" && d.Expected == "Fleet 4" && d.Actual == "theirs");
        Assert.Equal(JobStatus.NeedsAttention, w.Job.StatusCode);
    }

    [Fact]
    public async Task A_record_that_vanished_from_the_server_is_a_conflict_too()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Fleets, "4", ("Name", "mine"));
        w.Remote.Rows.Remove("fleet/4");
        await w.RunAsync();
        Assert.Equal(ItemState.Conflict, w.Job!.Items.Single().State);
        Assert.Equal(0, Count(w, "PUT"));
    }

    [Theory]
    [InlineData(500, "ApiRejected")]
    [InlineData(429, "ApiRateLimited")]
    public async Task A_server_that_cannot_answer_the_preflight_read_leaves_the_item_pending(int status, string message)
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Fleets, "4", ("Name", "mine"));
        w.Remote.Intercept = r => r.Method == "GET" && r.Path.EndsWith("/fleet/4", StringComparison.Ordinal) ? (status == 429 ? RateLimited() : Status(status)) : null;
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Pending, item.State);
        Assert.Equal(message, Code(item));
        Assert.Equal("Preflight", item.DiagnosticDetails!.Phase);
        Assert.Equal(0, Count(w, "PUT"));
        Assert.Equal(JobStatus.NeedsAttention, w.Job.StatusCode);
    }

    [Fact]
    public async Task A_preflight_read_that_times_out_leaves_the_item_pending()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Fleets, "4", ("Name", "mine"));
        w.Remote.Intercept = r => r.Method == "GET" ? throw new TimeoutException("simulated") : null;
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Pending, item.State);
        Assert.Equal("ApiPreflight", Code(item));
        Assert.Equal(0, Count(w, "PUT"));
    }

    [Fact]
    public async Task An_authentication_rejection_pauses_the_job_before_the_rest_is_touched()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Fleets, "4", ("Name", "first"));
        w.Edit(ResourceKind.Fleets, "5", ("Name", "second"));
        w.Remote.Intercept = r => r.Method == "GET" && r.Path.EndsWith("/fleet/4", StringComparison.Ordinal) ? Status(401) : null;
        await w.RunAsync();
        Assert.Equal(JobStatus.Paused, w.Job!.StatusCode);
        Assert.Equal([ItemState.Pending, ItemState.Pending], w.Job.Items.Select(i => i.State));
        Assert.Equal("ApiAuth", Code(w.Job.Items[0]));
        Assert.DoesNotContain("GET fleet/5", Calls(w));
    }

    // ---- preflight: dependencies, parameters, move target ---------------------------------------------------------

    [Fact]
    public async Task A_delete_is_refused_when_the_server_still_has_things_that_depend_on_the_record()
    {
        var w = new GoldenWorld();
        // nothing local depends on fleet 4, but the server still has its aircraft and routes
        foreach (var kind in new[] { ResourceKind.Aircraft, ResourceKind.Routes })
        {
            w.Workspace.Resources[kind].Snapshot.Clear();
            w.Workspace.Resources[kind].Draft.Clear();
        }
        w.MarkDelete(ResourceKind.Fleets, "4");
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Failed, item.State);
        Assert.Equal("ApiDependencies", Code(item));
        Assert.Equal(0, Count(w, "DELETE"));
        Assert.Contains("GET fleet/4/aircraft", Calls(w));
    }

    [Fact]
    public async Task A_delete_with_local_dependants_is_refused_without_any_dependency_read()
    {
        var w = new GoldenWorld();
        w.MarkDelete(ResourceKind.Fleets, "4");
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Failed, item.State);
        Assert.Equal("ApiDependencies", Code(item));
        Assert.Equal(0, Count(w, "DELETE"));
    }

    [Fact]
    public async Task A_new_online_fleet_without_an_airline_parameter_is_refused_before_any_request()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Fleets, ("Name", "No parameter"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"));
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Failed, item.State);
        Assert.Equal("ApiParameterRequired", Code(item));
        Assert.Empty(w.Remote.Server.Requests);
    }

    [Fact]
    public async Task A_move_to_a_fleet_that_no_longer_exists_is_refused_before_the_patch()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Aircraft, "8", ("Fleet ID", "5"));
        w.Remote.Rows.Remove("fleet/5");
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Failed, item.State);
        Assert.Equal("ApiMoveTarget", Code(item));
        Assert.Equal(["GET fleet/4/aircraft/8", "GET fleet/5"], Calls(w));
    }

    [Fact]
    public async Task A_row_with_an_open_merge_conflict_is_never_submitted()
    {
        var w = new GoldenWorld();
        var draft = w.Edit(ResourceKind.Fleets, "4", ("Name", "mine"));
        w.Workspace.Resources[ResourceKind.Fleets].Conflicts.Add(new MergeConflict(draft.LocalId, "Name", "Fleet 4", "mine", "theirs"));
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Failed, item.State);
        Assert.Equal("ApiConflict", Code(item));
        Assert.Empty(w.Remote.Server.Requests);
    }

    // ---- local references ------------------------------------------------------------------------------------

    [Fact]
    public async Task An_item_waits_for_the_create_it_depends_on_and_is_skipped_when_that_create_fails()
    {
        var w = new GoldenWorld();
        // "Fleet 4" exists already: the create is refused as a duplicate, and the aircraft that belongs to it cannot go ahead
        var fleet = w.New(ResourceKind.Fleets, NewFleet("Fleet 4"));
        w.New(ResourceKind.Aircraft, ("Name", "Plane Three"), ("Registration", "QA-NEW"), ("Fleet ID", LocalReference.Format(fleet.LocalId)));
        await w.RunAsync();
        Assert.Equal([ItemState.Failed, ItemState.Skipped], w.Job!.Items.Select(i => i.State));
        Assert.Equal("ApiDuplicate", Code(w.Job.Items[0]));
        Assert.Equal("Text_3E85DDB213", Code(w.Job.Items[1]));
        Assert.Equal(0, Count(w, "POST"));
        Assert.DoesNotContain(Calls(w), c => c.Contains("aircraft", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_local_reference_is_replaced_by_the_remote_id_once_the_create_succeeded()
    {
        var w = new GoldenWorld();
        var fleet = w.New(ResourceKind.Fleets, NewFleet("Brand new fleet"));
        w.New(ResourceKind.Aircraft, ("Name", "Plane Three"), ("Registration", "QA-NEW"), ("Fleet ID", LocalReference.Format(fleet.LocalId)));
        await w.RunAsync();
        Assert.All(w.Job!.Items, i => Assert.Equal(ItemState.Succeeded, i.State));
        var aircraft = w.Job.Items[1];
        Assert.Equal(w.Job.Items[0].RemoteId, aircraft.After.Get("Fleet ID"));
        Assert.Contains("POST fleet/90/aircraft", Calls(w));
    }

    [Fact]
    public async Task A_reference_to_a_row_that_is_not_in_the_job_needs_a_refresh()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Aircraft, ("Name", "Plane Three"), ("Registration", "QA-NEW"), ("Fleet ID", LocalReference.Format(Guid.NewGuid())));
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Failed, item.State);
        Assert.Empty(w.Remote.Server.Requests);
    }

    // ---- duplicate check ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_create_that_would_duplicate_a_record_is_refused_after_the_candidates_were_read()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Fleets, NewFleet("FLEET 4"));
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Failed, item.State);
        Assert.Equal("ApiDuplicate", Code(item));
        Assert.Equal(["GET fleet"], Calls(w));
    }

    [Fact]
    public async Task A_candidate_read_that_fails_leaves_the_create_pending_and_nothing_is_created()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Fleets, NewFleet("Brand new fleet"));
        w.Remote.Intercept = r => r.Method == "GET" && r.Path.EndsWith("/fleet", StringComparison.Ordinal) ? Status(500) : null;
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Pending, item.State);
        Assert.Equal(0, Count(w, "POST"));
        Assert.Equal("Preflight", item.DiagnosticDetails!.Phase);
    }

    [Fact]
    public async Task While_one_create_is_unknown_a_create_of_the_same_record_waits_instead_of_creating_it_twice()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Fleets, NewFleet("Twin fleet"));
        w.New(ResourceKind.Fleets, NewFleet("TWIN FLEET"));
        w.Remote.Intercept = r => r.Method == "POST" ? throw new TimeoutException("simulated create timeout") : null;
        await w.RunAsync();
        Assert.Equal([ItemState.Unknown, ItemState.Pending], w.Job!.Items.Select(i => i.State));
        Assert.Equal("ApiIdentityPending", Code(w.Job.Items[1]));
        Assert.Equal(1, Count(w, "POST"));
    }

    [Fact]
    public async Task A_jumpseat_create_records_which_records_existed_before_the_request()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Routes, ("Type", "jumpseat"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"), (Dep, "EGKK"), (Arr, "EGCC"));
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.NotNull(item.PreCreateCandidateIds);
        Assert.Empty(item.PreCreateCandidateIds!);

        var other = new GoldenWorld();
        other.New(ResourceKind.Routes, ("Type", "jumpseat"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"), (Dep, "EGLL"), (Arr, "EGKK"));
        await other.RunAsync();
        var duplicate = other.Job!.Items.Single();
        Assert.Equal("ApiDuplicate", Code(duplicate));
        // candidates are listed in the order read, up to the one that was the same route
        Assert.Contains("10", duplicate.PreCreateCandidateIds!);
        Assert.Contains("11", duplicate.PreCreateCandidateIds!);
    }

    [Fact]
    public async Task A_create_for_an_airport_pair_reads_the_pair_once_for_the_whole_job()
    {
        var w = new GoldenWorld();
        foreach (var number in new[] { "CA201", "CA202", "CA203" })
            w.New(ResourceKind.Routes, ("Type", "scheduled"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO" + number[2..]), ("Flight Number", number), ("Fleet IDs", "4"));
        await w.RunAsync();
        Assert.All(w.Job!.Items, i => Assert.Equal(ItemState.Succeeded, i.State));
        Assert.Equal(1, Calls(w).Count(c => c == "GET routes"));
        Assert.Equal(3, Count(w, "POST"));
    }
}
