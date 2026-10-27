using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// The stages that touch the server: the write (sent once, never repeated), the read-back that decides whether the write is
/// believed, and the read-only recovery of an item that was interrupted. Every failure path leaves the item in the state
/// that protects the server from a second write.
/// </summary>
public class WriteReadbackAndRecoveryStageTests
{
    static bool IsWrite(RecordedRequest r) => r.Method is "POST" or "PUT" or "PATCH" or "DELETE";

    // ---- write ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_server_error_on_a_write_leaves_the_item_unknown_and_the_write_is_never_sent_again()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "PUT" ? Status(500) : null;
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        await w.RunAsync("first run");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("ApiUnknownError", Code(item));
        Assert.Equal(BatchStepState.InFlight, item.ApiSteps[0].State);
        Assert.False(item.WriteAccepted);
        Assert.Equal("Writing", item.DiagnosticDetails!.Phase);
        await w.ResumeAsync("recovery");
        await w.ResumeAsync("recovery again");
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal(1, Count(w, "PUT"));
        Assert.Equal(JobStatus.NeedsAttention, w.Job.StatusCode);
    }

    [Fact]
    public async Task A_rate_limited_write_goes_back_to_pending_with_its_step_prepared_and_is_sent_once_on_the_next_run()
    {
        var w = new GoldenWorld();
        var limited = false;
        w.Remote.Intercept = r => { if (r.Method != "PUT" || limited) return null; limited = true; return RateLimited(); };
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        await w.RunAsync("first run");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Pending, item.State);
        Assert.Equal("ApiRateLimited", Code(item));
        Assert.Equal(BatchStepState.Prepared, item.ApiSteps[0].State);
        await w.ResumeAsync("second run");
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(2, Count(w, "PUT"));
    }

    [Fact]
    public async Task A_rejected_login_during_the_write_pauses_the_job_and_the_rest_waits()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "PUT" && r.Path.EndsWith("/fleet/4", StringComparison.Ordinal) ? Status(401) : null;
        w.Edit(ResourceKind.Fleets, "4", ("Name", "first"));
        w.Edit(ResourceKind.Fleets, "5", ("Name", "second"));
        await w.RunAsync();
        Assert.Equal(JobStatus.Paused, w.Job!.StatusCode);
        Assert.Equal([ItemState.Pending, ItemState.Pending], w.Job.Items.Select(i => i.State));
        Assert.Equal(BatchStepState.Prepared, w.Job.Items[0].ApiSteps[0].State);
        Assert.Equal(1, Count(w, "PUT"));
        // after signing in again the same job continues and sends each write once
        w.Remote.Intercept = null;
        await w.ResumeAsync("after sign-in");
        Assert.All(w.Job.Items, i => Assert.Equal(ItemState.Succeeded, i.State));
        Assert.Equal(3, Count(w, "PUT"));
    }

    [Fact]
    public async Task A_write_that_never_left_the_machine_is_prepared_again()
    {
        var w = new GoldenWorld();
        using var cancel = new CancellationTokenSource();
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        var job = NewJob(w);
        var saves = 0;
        // the caller gives up while the item is being saved as running, so the write is stopped before it is sent
        await Execute(w, job, () => { if (++saves == 1) cancel.Cancel(); return Task.CompletedTask; }, cancel.Token);
        var item = job.Items.Single();
        Assert.Equal(ItemState.Pending, item.State);
        Assert.Equal("ApiNotSent", Code(item));
        Assert.Equal(BatchStepState.Prepared, item.ApiSteps[0].State);
        Assert.Equal(0, Count(w, "PUT"));
        Assert.Equal(JobStatus.Canceled, job.StatusCode);
    }
    [Fact]
    public async Task A_failed_save_before_the_send_stops_the_run_with_nothing_sent()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        var job = NewJob(w);
        var saves = 0;
        // 1: the item is Running; 2: the step is in flight
        await Assert.ThrowsAsync<PersistenceException>(() => Execute(w, job, () => ++saves == 2 ? throw new IOException("disk full") : Task.CompletedTask));
        Assert.Equal(0, Count(w, "PUT"));
    }

    [Fact]
    public async Task A_failed_save_after_the_answer_is_recovered_by_reading_and_the_write_is_not_repeated()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        var job = NewJob(w);
        var saves = 0;
        // 3: the answer of the PUT is to be saved
        await Assert.ThrowsAsync<PersistenceException>(() => Execute(w, job, () => ++saves == 3 ? throw new IOException("disk full") : Task.CompletedTask));
        Assert.Equal(1, Count(w, "PUT"));
        Assert.Equal(ItemState.Running, job.Items.Single().State);
        // the program starts again over the same job: it only reads
        await Execute(w, job);
        Assert.Equal(ItemState.Succeeded, job.Items.Single().State);
        Assert.Equal(1, Count(w, "PUT"));
        Assert.Equal("renamed", Draft(w, ResourceKind.Fleets, "4").Get("Name"));
    }

    [Fact]
    public async Task A_create_whose_post_was_sent_but_not_answered_is_never_posted_again()
    {
        var w = new GoldenWorld();
        var posts = 0;
        w.Remote.Intercept = r => r.Method == "POST" && ++posts > 0 ? throw new TimeoutException("simulated") : null;
        w.New(ResourceKind.Fleets, ("Name", "New fleet"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Parameter IDs", "1"));
        await w.RunAsync("first run");
        Assert.Equal(ItemState.Unknown, w.Job!.Items.Single().State);
        await w.ResumeAsync("recovery");
        await w.ResumeAsync("recovery again");
        Assert.Equal(1, posts);
        Assert.Equal(1, Count(w, "POST"));
    }

    // ---- read-back ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_write_the_server_acknowledged_but_did_not_keep_is_not_believed()
    {
        var w = new GoldenWorld();
        // the PUT is answered with success but changes nothing
        w.Remote.Intercept = r => r.Method == "PUT" ? FakeResponse.Ok("""{"data":{}}""") : null;
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("ApiReadbackMismatch", Code(item));
        Assert.True(item.WriteAccepted);
        Assert.Equal(new ApiFieldDifference("name", "\"renamed\"", "\"Fleet 4\""), Assert.Single(item.VerificationDifferences));
        Assert.Equal("Verifying", item.ReadbackDetails!.Phase);
        Assert.Equal(["GET fleet/4", "PUT fleet/4", "GET fleet/4"], Calls(w));
        // the workspace did not learn anything from a write it could not confirm
        Assert.Equal("Fleet 4", w.Workspace.Resources[ResourceKind.Fleets].Snapshot.Single(r => r.Identity!.RemoteId == "4").Get("Name"));
        Assert.DoesNotContain("Fleets:Update", w.Workspace.VerifiedOperations);
    }

    [Fact]
    public async Task A_read_back_that_fails_leaves_the_item_unknown_and_a_later_read_confirms_it_without_a_second_write()
    {
        var w = new GoldenWorld();
        var failing = true;
        w.Remote.Intercept = r => failing && r.Method == "GET" && Count(w, "PUT") > 0 ? Status(500) : null;
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        await w.RunAsync("first run");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("ApiUnknownError", Code(item));
        Assert.Equal("Verifying", item.ReadbackDetails!.Phase);
        Assert.True(item.WriteAccepted);
        failing = false;
        await w.ResumeAsync("recovery");
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(1, Count(w, "PUT"));
        Assert.Contains("Fleets:Update", w.Workspace.VerifiedOperations);
    }

    [Fact]
    public async Task A_soft_delete_whose_record_can_still_be_read_is_not_confirmed()
    {
        var w = new GoldenWorld();
        // the DELETE is answered 204 but the record stays
        w.Remote.Intercept = r => r.Method == "DELETE" ? new FakeResponse(System.Net.HttpStatusCode.NoContent, null) : null;
        w.MarkDelete(ResourceKind.Airports, "103");
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("ApiSoftDeleteEvidence", Code(item));
        Assert.Equal("StillReadable", item.DeletionVerification!.Outcome);
        Assert.Contains(w.Workspace.Resources[ResourceKind.Airports].Snapshot, r => r.Identity!.RemoteId == "103");
    }

    [Fact]
    public async Task A_soft_delete_that_reads_404_after_the_204_is_proven_and_leaves_the_workspace()
    {
        var w = new GoldenWorld();
        w.MarkDelete(ResourceKind.Airports, "103");
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal("ApiDeletedInvisible", Code(item));
        Assert.Equal("ApiInvisible", item.DeletionVerification!.Outcome);
        Assert.DoesNotContain(w.Workspace.Resources[ResourceKind.Airports].Snapshot, r => r.Identity!.RemoteId == "103");
        Assert.DoesNotContain(w.Workspace.Resources[ResourceKind.Airports].Draft, r => r.Identity?.RemoteId == "103");
        Assert.Contains("Airports:Delete", w.Workspace.VerifiedOperations);
    }

    [Fact]
    public async Task A_verified_create_becomes_part_of_the_workspace()
    {
        var w = new GoldenWorld();
        var draft = w.New(ResourceKind.Fleets, ("Name", "Brand new"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Parameter IDs", "1"));
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.True(item.Rebased);
        Assert.True(item.CreateCompleted);
        Assert.Equal("90", item.RemoteId);
        var snapshot = w.Workspace.Resources[ResourceKind.Fleets].Snapshot.Single(r => r.LocalId == draft.LocalId);
        Assert.Equal("90", snapshot.Identity!.RemoteId);
        Assert.Equal("Brand new", snapshot.Get("Name"));
        var current = w.Workspace.Resources[ResourceKind.Fleets].FindDraftRow(draft.LocalId)!;
        Assert.Equal("90", current.Get("ID"));
        Assert.Contains("Fleets:Create:ParameterIds", w.Workspace.VerifiedOperations);
    }

    // ---- recovery ---------------------------------------------------------------------------------------------

    static ChangeItem LegacyTwoStepCreate(GoldenWorld w)
    {
        // A create whose POST was answered (the record 90 exists) and whose second step never left the machine.
        w.Remote.AddRow("fleet/90", """{"id":90,"airline_id":7,"name":"Legacy fleet","code":"B738","type":"pax"}""");
        var draft = w.New(ResourceKind.Fleets, ("Name", "Legacy fleet"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "180"));
        var item = new ChangeItem
        {
            Resource = ResourceKind.Fleets,
            Kind = ChangeKind.Create,
            After = draft,
            Fields = draft.Fields.ToDictionary(p => p.Key, p => new FieldChange(FieldIntent.Set, p.Value)),
            State = ItemState.Unknown,
            CreateCompleted = true,
            RemoteId = "90",
            ApiSteps =
            [
                new BatchStep { Method = "POST", Path = "fleet", State = BatchStepState.ResponseReceived, RemoteId = "90",
                    Body = new() { ["name"] = JsonSerializer.SerializeToElement("Legacy fleet"), ["code"] = JsonSerializer.SerializeToElement("B738"), ["type"] = JsonSerializer.SerializeToElement("pax") } },
                new BatchStep { Method = "PUT", Path = "{created}", State = BatchStepState.Prepared, Body = new() { ["max_pax"] = JsonSerializer.SerializeToElement(180) } }
            ]
        };
        return item;
    }

    [Fact]
    public async Task A_legacy_two_step_create_finishes_with_the_missing_step_only()
    {
        var w = new GoldenWorld();
        var item = LegacyTwoStepCreate(w);
        var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
        w.Workspace.Jobs.Add(job);
        await Execute(w, job);
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(0, Count(w, "POST"));
        Assert.Equal(["GET fleet/90", "PUT fleet/90", "GET fleet/90"], Calls(w));
        Assert.Equal(180, w.Remote.Rows["fleet/90"]["max_pax"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_legacy_two_step_create_whose_record_changed_since_stays_unknown()
    {
        var w = new GoldenWorld();
        var item = LegacyTwoStepCreate(w);
        w.Remote.Rows["fleet/90"]["name"] = "Renamed by somebody";
        var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
        await Execute(w, job);
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("ApiUnknown", Code(item));
        Assert.Equal(0, Calls(w).Count(c => c.StartsWith("PUT", StringComparison.Ordinal) || c.StartsWith("POST", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_accepted_write_that_the_record_does_not_show_stays_unknown_and_is_not_repeated()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "PUT" ? FakeResponse.Ok("""{"data":{}}""") : null;
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        await w.RunAsync("first run");
        w.Remote.Intercept = null;
        await w.ResumeAsync("recovery");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("ApiUnknown", Code(item));
        Assert.Equal(1, Count(w, "PUT"));
    }

    [Fact]
    public async Task An_unknown_hard_delete_whose_record_is_gone_is_completed_by_reading()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "DELETE" ? throw new TimeoutException("simulated") : null;
        w.MarkDelete(ResourceKind.Fleets, "6");
        await w.RunAsync("first run");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        // the server did apply the delete although the answer was lost
        w.Remote.Rows.Remove("fleet/6");
        w.Remote.Intercept = null;
        await w.ResumeAsync("recovery");
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(1, Count(w, "DELETE"));
        Assert.DoesNotContain(w.Workspace.Resources[ResourceKind.Fleets].Snapshot, r => r.Identity!.RemoteId == "6");
    }

    [Fact]
    public async Task A_proposed_recovery_id_is_read_and_when_it_matches_the_create_is_completed_without_a_post()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "POST" ? throw new TimeoutException("simulated") : null;
        w.New(ResourceKind.Fleets, ("Name", "Lost create"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Parameter IDs", "1"));
        await w.RunAsync("first run");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        // the server did create it, as record 77; the user finds the id and enters it
        w.Remote.AddRow("fleet/77", """{"id":77,"airline_id":7,"name":"Lost create","code":"B738","type":"pax","parameter_ids":["1"]}""");
        item.ProposeRecoveryId("77");
        w.Remote.Intercept = null;
        await w.ResumeAsync("recovery");
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal("77", item.RemoteId);
        Assert.Equal(1, Count(w, "POST"));
        Assert.Contains("GET fleet/77", Calls(w));
    }

    [Fact]
    public async Task An_item_that_was_marked_running_but_never_sent_anything_starts_over_as_a_fresh_item()
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        var job = NewJob(w);
        var item = job.Items.Single();
        // as left by a program that stopped after saving the plan and before sending: every step is still prepared
        OperationsAdapter.PrepareSteps(item, w.Api.Plan(item));
        item.State = ItemState.Running;
        await Execute(w, job);
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(["GET fleet/4", "PUT fleet/4", "GET fleet/4"], Calls(w));
    }

    [Fact]
    public async Task Recovery_is_read_only_even_when_the_read_itself_fails()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "PUT" ? throw new TimeoutException("simulated") : null;
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        await w.RunAsync("first run");
        var seen = w.Remote.Server.Requests.Count;
        w.Remote.Intercept = r => r.Method == "GET" ? Status(503) : r.Method == "PUT" ? throw new InvalidOperationException("must not write") : null;
        await w.ResumeAsync("recovery with a failing read");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("Recovering", item.ReadbackDetails!.Phase);
        Assert.Equal(1, Count(w, "PUT"));
        Assert.DoesNotContain(w.Remote.Server.Requests.Skip(seen), IsWrite);
    }

    // ---- C11: what the old Contract and Reaudit scenarios checked and the tests above did not state --------------------

    [Fact]
    public async Task A_rate_limited_create_goes_back_to_pending_with_nothing_created_and_is_posted_again_on_the_next_run()
    {
        var w = new GoldenWorld();
        var limited = false;
        w.Remote.Intercept = r => { if (r.Method != "POST" || limited) return null; limited = true; return RateLimited(); };
        w.New(ResourceKind.Fleets, ("Name", "New"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Parameter IDs", "1"));
        await w.RunAsync("first run");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Pending, item.State);
        Assert.Equal("ApiRateLimited", Code(item));
        Assert.False(item.CreateCompleted);
        Assert.Null(item.RemoteId);
        Assert.All(item.ApiSteps, s => Assert.Equal(BatchStepState.Prepared, s.State));
        await w.ResumeAsync("second run");
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal("90", item.RemoteId);
        Assert.Equal(2, Count(w, "POST"));
    }

    [Fact]
    public async Task A_created_route_whose_answer_cannot_be_read_in_full_keeps_its_id_across_a_restart_and_is_confirmed_without_a_second_post()
    {
        var w = new GoldenWorld();
        // the server created route 9 but the answer carries nothing except the id
        w.Remote.Intercept = r => r.Method == "POST" ? FakeResponse.WithStatus(201, """{"id":9}""") : null;
        w.New(ResourceKind.Routes, ("Type", "scheduled"), ("Departure Airport (ICAO/IATA)", "EGLL"), ("Arrival Airport (ICAO/IATA)", "EGKK"),
            ("Callsign", "CAO101"), ("Flight Number", "CA101"), ("Fleet IDs", "4"));
        var job = NewJob(w);
        var saved = "";
        await Execute(w, job, () => { saved = w.Workspace.Serialize(); return Task.CompletedTask; });
        var item = job.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("9", item.RemoteId);
        Assert.True(item.CreateCompleted);

        // the program starts again: the id is in the saved task and the record is only read
        w.Remote.Intercept = null;
        w.Remote.AddRow("routes/9", """{"id":9,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO101","flight_number":"CA101","fleet_ids":[4]}""");
        var (restored, api) = Restarted(w, saved);
        var restoredItem = restored.Jobs.Single().Items.Single();
        Assert.Equal("9", restoredItem.RemoteId);
        await new OperationsBatchExecutor(api, restored).ExecuteAsync(restored.Jobs.Single(), () => Task.CompletedTask, Ct);
        Assert.Equal(ItemState.Succeeded, restoredItem.State);
        Assert.Equal(1, Count(w, "POST"));
    }

    [Fact]
    public async Task A_create_whose_read_back_is_rejected_stays_unknown_with_its_expectations_saved_before_the_post_and_is_confirmed_after_a_restart()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "GET" && r.Path.EndsWith("/fleet/90", StringComparison.Ordinal) ? Status(422) : null;
        w.New(ResourceKind.Fleets, ("Name", "Brand new"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Parameter IDs", "1"));
        var job = NewJob(w);
        var saved = "";
        var expectedBeforeFirstPost = new List<string>();
        await Execute(w, job, () =>
        {
            saved = w.Workspace.Serialize();
            var item = job.Items.Single();
            if (Count(w, "POST") == 0 && item.State == ItemState.Running && item.ApiExpectedValues is { } expected)
                expectedBeforeFirstPost = [.. expected.Keys.Order()];
            return Task.CompletedTask;
        });
        var created = job.Items.Single();
        Assert.Equal(ItemState.Unknown, created.State);
        Assert.Equal("90", created.RemoteId);
        Assert.Equal(["code", "name", "parameter_ids", "type"], expectedBeforeFirstPost);
        Assert.Equal(1, Count(w, "POST"));

        w.Remote.Intercept = null;
        var (restored, api) = Restarted(w, saved);
        var restoredItem = restored.Jobs.Single().Items.Single();
        Assert.Equal(["code", "name", "parameter_ids", "type"], restoredItem.ApiExpectedValues!.Keys.Order());
        await new OperationsBatchExecutor(api, restored).ExecuteAsync(restored.Jobs.Single(), () => Task.CompletedTask, Ct);
        Assert.Equal(ItemState.Succeeded, restoredItem.State);
        Assert.Equal(1, Count(w, "POST"));
    }
}
