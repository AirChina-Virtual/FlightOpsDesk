using VamSys.Core;
using VamSys.Infrastructure;
using static VamSys.IntegrationTests.Api.PipelineHarness;
using static VamSys.IntegrationTests.Api.StoredWorld;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// The proof that a soft delete took effect (the 204 answer, then a 404 read) over the real executor, store and fake server,
/// and the read-only checks that complete or upgrade a delete later. None of them ever writes.
/// </summary>
[Collection(StoredWorldCollection.Name)]
public class SoftDeleteVerificationTests
{
    /// <summary>A route delete the server answered 204 without removing the record, confirmed by hand: the "manual history".</summary>
    static async Task<(StoredWorld S, BatchJob Job)> ManuallyConfirmedDelete()
    {
        var s = await AcceptedSoftDelete();
        var job = s.W.Jobs.Single();
        await new OperationsDeletionVerifier(s.World.Api, s.W).ConfirmManuallyAsync(job, job.Items.Single(), s.Writer, Ct);
        Assert.Equal(ItemState.ManuallyConfirmed, job.Items.Single().State);
        return (s, job);
    }

    static Task Verify(StoredWorld s, BatchJob job, IBatchCheckpointWriter? writer = null)
        => new OperationsDeletionVerifier(s.World.Api, s.W).VerifyAsync(job, writer ?? s.Writer, Ct);

    // ---- the automatic proof -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(ResourceKind.Airports, "103")]
    [InlineData(ResourceKind.Routes, "10")]
    [InlineData(ResourceKind.Routes, "11")]
    public async Task Airport_scheduled_and_jumpseat_deletes_are_proven_by_204_then_404_and_the_proof_is_saved(ResourceKind kind, string id)
    {
        using var s = new StoredWorld();
        s.World.MarkDelete(kind, id);
        var job = s.Submit();
        await s.Run(job);
        var item = job.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal("ApiInvisible", item.DeletionVerification!.Outcome);
        var disk = s.Disk;
        Assert.Equal(404, disk.Jobs.Single().Items.Single().DeletionVerification!.ReadStatus);
        Assert.Contains(ApiOperationPolicy.Key(item), disk.VerifiedOperations);
        Assert.Equal(1, Count(s.World, "DELETE"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("status")]
    [InlineData("method")]
    [InlineData("path")]
    [InlineData("steps")]
    [InlineData("identity")]
    [InlineData("localIdentity")]
    [InlineData("accepted")]
    [InlineData("time")]
    [InlineData("oldjson")]
    public async Task A_read_only_check_completes_a_delete_only_when_the_saved_write_evidence_is_whole(string fault)
    {
        using var s = await AcceptedSoftDelete();
        var job = s.W.Jobs.Single();
        var item = job.Items.Single();
        // the record is gone now, so the read is a 404 for every case; only the saved evidence differs
        s.Remote.Rows.Remove("routes/10");
        if (fault == "status") item.DiagnosticDetails = item.DiagnosticDetails! with { Status = 500 };
        if (fault == "method") item.DiagnosticDetails = item.DiagnosticDetails! with { Method = "PUT" };
        if (fault == "path") item.DiagnosticDetails = item.DiagnosticDetails! with { Path = "/api/v3/operations/routes/999" };
        if (fault == "steps") item.ApiSteps[0].State = BatchStepState.InFlight;
        if (fault == "identity") item.After = item.After.WithIdentity(new("999", "7"));
        if (fault == "localIdentity") item.After = item.After.WithLocalId(Guid.NewGuid());
        if (fault == "accepted") item.WriteAccepted = false;
        if (fault == "time") item.DiagnosticDetails = item.DiagnosticDetails! with { FinishedAt = item.DiagnosticDetails!.FinishedAt.AddYears(1) };
        if (fault == "oldjson") { item.ApiSteps = []; item.DiagnosticDetails = null; }
        var before = Calls(s.World).Length;

        await Verify(s, job);

        // the deletion was read in every case, and nothing was written beyond the one DELETE of the first run
        // (a record whose after-identity names another record is not even read)
        if (fault == "identity")
            Assert.Empty(Calls(s.World).Skip(before));
        else
            Assert.Contains("GET routes/10", Calls(s.World).Skip(before));
        Assert.Equal(["DELETE routes/10"], s.Writes());
        Assert.Equal(fault == "none" ? ItemState.Succeeded : ItemState.Unknown, job.Items.Single().State);
    }

    // ---- manual confirmation of an accepted delete ---------------------------------------------------------------

    [Fact]
    public async Task A_manual_deletion_confirmation_commits_before_the_rows_and_status_change_and_never_fabricates_a_verification()
    {
        using var s = await AcceptedSoftDelete();
        var job = s.W.Jobs.Single();
        var item = job.Items.Single();
        var local = item.After.LocalId;
        var before = Json(item);
        var drafts = s.W.Resources[ResourceKind.Routes].Draft.Count;
        var requests = s.Remote.Server.Requests.Count;
        var verifier = new OperationsDeletionVerifier(s.World.Api, s.W);

        var failing = new FailingWriter(s.Writer, _ => true);
        await Assert.ThrowsAsync<PersistenceException>(() => verifier.ConfirmManuallyAsync(job, item, failing, Ct));
        Assert.Equal(1, failing.Hits);
        Assert.Equal(before, Json(item));
        Assert.Equal(drafts, s.W.Resources[ResourceKind.Routes].Draft.Count);
        Assert.Equal(before, Json(s.Disk.Jobs.Single().Items.Single()));

        await verifier.ConfirmManuallyAsync(job, item, s.Writer, Ct);
        Assert.Equal(ItemState.ManuallyConfirmed, job.Items.Single().State);
        Assert.Null(s.W.Resources[ResourceKind.Routes].FindDraftRow(local));
        Assert.Equal(drafts - 1, s.W.Resources[ResourceKind.Routes].Draft.Count);
        Assert.Equal(requests, s.Remote.Server.Requests.Count);
        Assert.Equal(ItemState.ManuallyConfirmed, s.Disk.Jobs.Single().Items.Single().State);
        Assert.DoesNotContain("Routes:Delete", s.W.VerifiedOperations);
    }

    // ---- the explicit read-only check of an old manual confirmation --------------------------------------------------

    [Fact]
    public async Task The_explicit_read_only_check_upgrades_a_manual_confirmation_and_leaves_pending_items_and_unrelated_drafts_alone()
    {
        var (s, job) = await ManuallyConfirmedDelete();
        using var owner = s;
        var pending = new ChangeItem { Resource = ResourceKind.Routes, Kind = ChangeKind.Delete };
        job.Items.Add(pending);
        var fleets = s.W.Resources[ResourceKind.Fleets];
        var row = fleets.Draft[0];
        fleets.Edit([row.LocalId], () => fleets.ReplaceDraftRows([row.With("Name", "Unrelated local edit")]));
        s.Store.Save(s.W);
        var history = fleets.Undo.Count;
        var pendingBefore = Json(pending);
        // the record is invisible now
        s.Remote.Rows.Remove("routes/10");
        var before = Calls(s.World).Length;

        await Verify(s, job);

        var upgraded = job.Items[0];
        Assert.Equal(ItemState.Succeeded, upgraded.State);
        Assert.True(upgraded.DeletionVerification!.WasManuallyConfirmed);
        Assert.Equal("ApiInvisible", upgraded.DeletionVerification.Outcome);
        Assert.Equal(pendingBefore, Json(job.Items[1]));
        Assert.Equal(["GET routes/10"], Calls(s.World).Skip(before));
        var disk = s.Disk;
        Assert.Equal(history, disk.Resources[ResourceKind.Fleets].Undo.Count);
        Assert.Equal("Unrelated local edit", disk.Resources[ResourceKind.Fleets].Draft[0].Get("Name"));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(403)]
    public async Task A_read_that_still_shows_the_record_or_fails_keeps_the_manual_confirmation_and_never_writes(int status)
    {
        var (s, job) = await ManuallyConfirmedDelete();
        using var owner = s;
        var write = Json(job.Items.Single().DiagnosticDetails);
        var failedReads = 0;
        if (status != 200)
            s.Remote.Intercept = r => { failedReads++; return Status(status); };
        var before = Calls(s.World).Length;

        await Verify(s, job);

        var item = job.Items.Single();
        Assert.Equal(ItemState.ManuallyConfirmed, item.State);
        Assert.Equal(write, Json(item.DiagnosticDetails));
        Assert.True(item.DeletionVerification!.WasManuallyConfirmed);
        if (status == 200)
            Assert.Contains("GET routes/10", Calls(s.World).Skip(before));
        else
            Assert.True(failedReads > 0, "the failing read was never reached");
        Assert.Equal(["DELETE routes/10"], s.Writes());
        if (status == 403)
            Assert.Equal(JobStatus.Paused, job.StatusCode);
    }

    [Fact]
    public async Task A_checkpoint_that_cannot_be_written_during_the_read_only_check_leaves_memory_and_disk_as_they_were()
    {
        var (s, job) = await ManuallyConfirmedDelete();
        using var owner = s;
        s.Remote.Rows.Remove("routes/10");
        var before = Json(job.Items.Single());
        var requests = Calls(s.World).Length;
        var failing = new FailingWriter(s.Writer, _ => true);

        await Assert.ThrowsAsync<PersistenceException>(() => Verify(s, job, failing));

        Assert.Equal(1, failing.Hits);
        Assert.Equal(before, Json(job.Items.Single()));
        Assert.Equal(before, Json(s.Disk.Jobs.Single().Items.Single()));
        Assert.DoesNotContain("Routes:Delete", s.W.VerifiedOperations);
        Assert.Equal(["GET routes/10"], Calls(s.World).Skip(requests));
    }
}
