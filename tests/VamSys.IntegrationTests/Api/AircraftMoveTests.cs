using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;
using static VamSys.IntegrationTests.Api.StoredWorld;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// Moving an aircraft to another fleet is its own task item: one PATCH carrying only the fleet, read back under the target fleet,
/// never sent twice. The tests run the real executor over a real database and a stateful fake server; a failure is injected only
/// where the test says so, and each test asserts that the injected failure was reached.
/// </summary>
[Collection(StoredWorldCollection.Name)]
public class AircraftMoveTests
{
    static (StoredWorld S, BatchJob Job) Move()
    {
        var s = new StoredWorld();
        s.World.Edit(ResourceKind.Aircraft, "8", ("Fleet ID", "5"));
        var job = s.Submit();
        Assert.Equal(ResourceKind.Aircraft, job.Items.Single().Resource);
        return (s, job);
    }

    /// <summary>The program starts again over what the database holds: a freshly loaded workspace and a new executor.</summary>
    static async Task<BatchJob> Recover(StoredWorld s)
    {
        var reloaded = s.Disk;
        var recovered = reloaded.Jobs.Single();
        await new OperationsBatchExecutor(s.Adapter(reloaded), reloaded).ExecuteAsync(recovered, s.Store.CheckpointWriter(reloaded), Ct);
        return recovered;
    }

    [Fact]
    public async Task A_move_sends_one_fleet_only_patch_reads_it_back_under_the_target_fleet_and_rebases_the_parent_atomically()
    {
        var (s, job) = Move();
        using var owner = s;
        var item = job.Items.Single();
        var local = item.After.LocalId;

        await s.Run(job);

        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(["PATCH aircraft/8"], s.Writes());
        Assert.Equal("""{"fleet_id":5}""", s.Remote.Server.Requests.Single(r => r.Method == "PATCH").Body);
        var calls = Calls(s.World);
        Assert.Contains("GET fleet/5", calls);
        Assert.Contains("GET fleet/5/aircraft/8", calls);
        var disk = s.Disk;
        var row = disk.Resources[ResourceKind.Aircraft].Draft.Single();
        Assert.Equal(local, row.LocalId);
        Assert.Equal("5", row.Identity!.ParentFleetId);
        Assert.Equal("5", row.Get("Fleet ID"));
        Assert.Contains("Aircraft:Update:FleetId:Move", disk.VerifiedOperations);
        Assert.DoesNotContain("Aircraft:Update", disk.VerifiedOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_patch_answered_500_is_only_recovered_by_reading_the_target_whether_or_not_the_server_committed_it(bool committed)
    {
        var (s, job) = Move();
        using var owner = s;
        var failures = 0;
        s.Remote.Intercept = r =>
        {
            if (r.Method != "PATCH") return null;
            failures++;
            if (committed) s.Remote.Respond(r);
            return Status(500);
        };

        await s.Run(job);
        Assert.Equal(1, failures);
        // the read at the end of the round (E2) already settles a committed move; one that was not committed stays unknown
        Assert.Equal(committed ? ItemState.Succeeded : ItemState.Unknown, job.Items.Single().State);
        Assert.Equal(1, Count(s.World, "PATCH"));

        var recovered = await Recover(s);

        Assert.Equal(committed ? ItemState.Succeeded : ItemState.Unknown, recovered.Items.Single().State);
        Assert.Equal(1, Count(s.World, "PATCH"));
        Assert.Equal(1, failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_checkpoint_that_fails_around_the_patch_never_resends_it_and_a_received_answer_is_recovered_by_reading(bool afterResponse)
    {
        var (s, job) = Move();
        using var owner = s;
        var failing = new FailingWriter(s.Writer, c => c.Kind == CheckpointKind.Step && c.Item!.ApiSteps.Count > 0
            && c.Item.ApiSteps[0].State == (afterResponse ? BatchStepState.ResponseReceived : BatchStepState.InFlight));

        await Assert.ThrowsAsync<PersistenceException>(() => s.Run(job, failing));

        // a step is saved as in flight before it is sent: when that save fails nothing has left the machine
        Assert.Equal(1, failing.Hits);
        Assert.Equal(afterResponse ? 1 : 0, Count(s.World, "PATCH"));

        var recovered = await Recover(s);

        Assert.Equal(1, Count(s.World, "PATCH"));
        Assert.Equal(ItemState.Succeeded, recovered.Items.Single().State);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("mixed")]
    [InlineData("steps")]
    [InlineData("locally-deleted")]
    [InlineData("changed")]
    public async Task A_move_to_a_foreign_or_missing_fleet_with_other_fields_or_tampered_steps_or_a_changed_aircraft_is_refused_before_any_write(string bad)
    {
        using var s = new StoredWorld();
        if (bad == "foreign") s.Remote.Rows["fleet/5"]["airline_id"] = 8;
        if (bad == "missing") s.Remote.Rows.Remove("fleet/5");
        if (bad == "locally-deleted") s.World.MarkDelete(ResourceKind.Fleets, "5");
        if (bad == "changed") s.Remote.Rows["fleet/4/aircraft/8"]["fleet_id"] = 6;
        (string, string)[] edits = bad == "mixed" ? [("Fleet ID", "5"), ("Name", "Other")] : [("Fleet ID", "5")];
        s.World.Edit(ResourceKind.Aircraft, "8", edits);
        var job = s.Submit();
        // only the move is run; the deletion of the fleet in the "locally-deleted" case is another item of the task
        job.Items.RemoveAll(i => i.Resource != ResourceKind.Aircraft);
        var item = job.Items.Single();
        if (bad == "steps")
            item.ApiSteps = [new BatchStep { Method = "PUT", Path = "aircraft/8", Body = new() { ["fleet_id"] = System.Text.Json.JsonSerializer.SerializeToElement(5) } }];

        await s.Run(job);

        Assert.Equal(bad == "changed" ? ItemState.Conflict : ItemState.Failed, item.State);
        Assert.Equal(bad switch { "steps" => "ApiRefreshFirst", "changed" => "ApiConflict", "mixed" => "ApiMoveSeparate", "foreign" => "ApiWrongVa", _ => "ApiMoveTarget" }, Code(item));
        Assert.Empty(s.Writes());
        // the target fleet was looked at where the refusal depends on it
        if (bad is "foreign" or "missing")
            Assert.Contains("GET fleet/5", Calls(s.World));
        if (bad == "changed")
            Assert.Contains("GET fleet/4/aircraft/8", Calls(s.World));
    }

    [Fact]
    public async Task A_target_path_answer_whose_fleet_id_still_names_the_old_fleet_leaves_the_move_unknown_and_the_parent_unchanged()
    {
        var (s, job) = Move();
        using var owner = s;
        var wrong = 0;
        s.Remote.Intercept = r =>
        {
            if (r.Method != "GET" || !r.Path.EndsWith("/fleet/5/aircraft/8", StringComparison.Ordinal) || Count(s.World, "PATCH") == 0) return null;
            wrong++;
            return FakeResponse.Ok("""{"data":{"id":8,"airline_id":7,"fleet_id":4,"registration":"B-TEST","name":"Plane","cargo":1000}}""");
        };

        await s.Run(job);

        Assert.True(wrong > 0, "the wrong answer was never reached");
        Assert.Equal(ItemState.Unknown, job.Items.Single().State);
        Assert.Equal(1, Count(s.World, "PATCH"));
        Assert.Equal("4", s.Disk.Resources[ResourceKind.Aircraft].Snapshot.Single().Identity!.ParentFleetId);
    }
}
