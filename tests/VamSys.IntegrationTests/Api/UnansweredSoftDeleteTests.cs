using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;
using static VamSys.IntegrationTests.Api.StoredWorld;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// A soft delete (airport, route) whose DELETE was sent and never answered has no write receipt, so the 204/404 proof does not
/// apply. A read-only GET decides what the person may do: confirm it by hand when the record is gone, or mark it not applied
/// when the record still reads exactly as before. Every path here is read-only: the DELETE is sent once, ever.
/// </summary>
[Collection(StoredWorldCollection.Name)]
public class UnansweredSoftDeleteTests
{
    static string PathOf(ResourceKind kind) => kind == ResourceKind.Airports ? "airports/103" : "routes/10";

    /// <summary>
    /// The first run sends the DELETE, the answer is lost (timeout) and the server did or did not apply it. The scripted
    /// timeout is hit exactly once, which the helper asserts, so the item really is an unanswered delete. When the server
    /// applied it, the reads of the rest of the first run fail too: a 404 at the end of the round would otherwise complete
    /// the delete by itself (D3, see the test below), and these tests are about what is left for the person.
    /// </summary>
    static async Task<(StoredWorld S, BatchJob Job, string Path)> Unanswered(ResourceKind kind, bool serverApplied)
    {
        var s = new StoredWorld();
        var path = PathOf(kind);
        var timeouts = 0;
        s.Remote.Intercept = r =>
        {
            if (serverApplied && timeouts > 0 && r.Method == "GET")
                throw new TimeoutException("simulated read");
            if (r.Method != "DELETE")
                return null;
            timeouts++;
            if (serverApplied)
                s.Remote.Respond(r);
            throw new TimeoutException("simulated");
        };
        s.World.MarkDelete(kind, kind == ResourceKind.Airports ? "103" : "10");
        var job = s.Submit();
        await s.Run(job);
        s.Remote.Intercept = null;

        Assert.Equal(1, timeouts);
        var item = job.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.False(item.WriteAccepted);
        Assert.Equal(BatchStepState.InFlight, item.ApiSteps.Single().State);
        Assert.Equal(serverApplied, !s.Remote.Rows.ContainsKey(path));
        Assert.True(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        return (s, job, path);
    }

    /// <summary>Resume and the read-only deletion check: neither can settle an unanswered delete.</summary>
    static async Task ResumeAndCheck(StoredWorld s, BatchJob job)
    {
        await s.Run(job);
        await new OperationsDeletionVerifier(s.World.Api, s.W).VerifyAsync(job, s.Writer, Ct);
    }

    // ---- the record is gone: confirm by hand ---------------------------------------------------------------------

    [Theory]
    [InlineData(ResourceKind.Routes)]
    [InlineData(ResourceKind.Airports)]
    public async Task An_unanswered_soft_delete_that_reads_404_can_be_confirmed_by_hand_and_is_marked_as_having_no_receipt(ResourceKind kind)
    {
        var (s, job, path) = await Unanswered(kind, serverApplied: true);
        using var owner = s;
        var local = job.Items.Single().After.LocalId;

        await new OperationsDeletionVerifier(s.World.Api, s.W).VerifyAsync(job, s.Writer, Ct);
        var item = job.Items.Single();
        // before: the read-only deletion check (which needs a receipt) does not settle it, nor does the manual confirmation
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.False(OperationsUnknownResolver.CanCheck(item));
        Assert.True(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        var verifier = new OperationsDeletionVerifier(s.World.Api, s.W);
        await Expect.BlockedAsync(() => verifier.ConfirmManuallyAsync(job, item, s.Writer, Ct), "ApiUnknown");
        Assert.True(OperationsDeletionVerifier.CanConfirmWithoutReceipt(item));

        // a checkpoint that cannot be written changes nothing in memory or on disk
        var before = Json(job.Items);
        var drafts = s.W.Resources[kind].Draft.Count;
        var failing = new FailingWriter(s.Writer, _ => true);
        await Assert.ThrowsAsync<PersistenceException>(() => verifier.ConfirmWithoutReceiptAsync(job, item, failing, Ct));
        Assert.Equal(1, failing.Hits);
        Assert.Equal(before, Json(job.Items));
        Assert.Equal(drafts, s.W.Resources[kind].Draft.Count);
        Assert.Equal(before, Json(s.Disk.Jobs.Single().Items));

        Assert.True(await verifier.ConfirmWithoutReceiptAsync(job, item, s.Writer, Ct));
        var done = job.Items.Single();
        Assert.Equal(ItemState.ManuallyConfirmed, done.State);
        Assert.Equal("ApiManualDeleteNoReceipt", done.Description?.Code);
        Assert.Equal("SoftDeleteWithoutReceipt", done.ManualResolution!.Source);
        Assert.False(done.ManualResolution.WriteReceipt);
        Assert.Equal(404, done.ManualResolution.ReadStatus);
        Assert.Equal("ManualWithoutWriteReceipt", done.DeletionVerification!.Outcome);
        Assert.True(done.DeletionVerification.WasManuallyConfirmed);
        Assert.Equal(404, done.DeletionVerification.ReadStatus);
        // the deletion draft leaves the workspace, the task no longer blocks it, and nothing was verified
        Assert.Null(s.W.Resources[kind].FindDraftRow(local));
        Assert.DoesNotContain(s.W.Resources[kind].Snapshot, r => r.LocalId == local);
        Assert.False(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        Assert.False(WorkspaceStore.HasUnresolvedWrites(s.W));
        Assert.Empty(s.W.VerifiedOperations);
        Assert.Equal(Json(job.Items), Json(s.Disk.Jobs.Single().Items));

        // a later read-only check or resume never upgrades it to the full proof
        await ResumeAndCheck(s, job);
        var later = job.Items.Single();
        Assert.Equal(ItemState.ManuallyConfirmed, later.State);
        Assert.False(later.ManualResolution!.WriteReceipt);
        Assert.Equal("ApiManualDeleteNoReceipt", later.Description?.Code);
        Assert.Empty(s.W.VerifiedOperations);
        Assert.Equal([$"DELETE {path}"], s.Writes());
    }

    // ---- D3: the record is gone and was read as 404 by the program itself ------------------------------------------

    [Theory]
    [InlineData(ResourceKind.Routes)]
    [InlineData(ResourceKind.Airports)]
    public async Task An_unanswered_soft_delete_that_reads_404_completes_in_the_same_round_marked_without_receipt_and_not_verified(ResourceKind kind)
    {
        using var s = new StoredWorld();
        var path = PathOf(kind);
        s.Remote.Intercept = r =>
        {
            if (r.Method != "DELETE")
                return null;
            s.Remote.Respond(r);
            throw new TimeoutException("simulated");
        };
        s.World.MarkDelete(kind, kind == ResourceKind.Airports ? "103" : "10");
        var job = s.Submit();
        var local = job.Items.Single().After.LocalId;

        await s.Run(job);

        var item = job.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal("ApiDeletedWithoutReceipt", item.Description?.Code);
        Assert.False(item.WriteAccepted);
        Assert.Equal("AutomaticWithoutWriteReceipt", item.DeletionVerification!.Outcome);
        Assert.False(item.DeletionVerification.WasManuallyConfirmed);
        Assert.Null(item.DeletionVerification.WriteStatus);
        Assert.Equal(404, item.DeletionVerification.ReadStatus);
        Assert.Null(item.ManualResolution);
        // the deletion draft leaves the workspace, nothing blocks it any more, and the operation is not verified by it
        Assert.Null(s.W.Resources[kind].FindDraftRow(local));
        Assert.False(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        Assert.Empty(s.W.VerifiedOperations);
        Assert.Empty(s.Disk.VerifiedOperations);
        Assert.Equal(Json(job.Items), Json(s.Disk.Jobs.Single().Items));
        Assert.Equal([$"DELETE {path}"], s.Writes());
    }

    [Theory]
    [InlineData("readable")]
    [InlineData("read-failure")]
    [InlineData("accepted")]
    public async Task A_record_that_is_still_readable_a_failed_read_or_a_receipt_leaves_the_delete_unknown(string variant)
    {
        // "accepted": the server answered 204 and kept the record, so there is a receipt; the others have none
        using var s = variant == "accepted" ? await StoredWorld.AcceptedSoftDelete() : (await Unanswered(ResourceKind.Routes, serverApplied: variant == "read-failure")).S;
        var job = s.W.Jobs.Single();
        var item = job.Items.Single();
        var reads = 0;
        if (variant == "read-failure")
            s.Remote.Intercept = r => { reads++; return Status(500); };
        var verifier = new OperationsDeletionVerifier(s.World.Api, s.W);
        var before = Json(job.Items);
        var requests = s.Remote.Server.Requests.Count;

        if (variant == "accepted")
        {
            Assert.True(item.WriteAccepted);
            Assert.False(OperationsDeletionVerifier.CanConfirmWithoutReceipt(item));
            await Expect.BlockedAsync(() => verifier.ConfirmWithoutReceiptAsync(job, item, s.Writer, Ct), "ApiUnknown");
        }
        else if (variant == "read-failure")
        {
            await Assert.ThrowsAnyAsync<ApiResponseException>(() => verifier.ConfirmWithoutReceiptAsync(job, item, s.Writer, Ct));
            Assert.True(reads > 0, "the failing read was never reached");
        }
        else
        {
            Assert.Contains("routes/10", s.Remote.Rows.Keys);
            Assert.False(await verifier.ConfirmWithoutReceiptAsync(job, item, s.Writer, Ct));
            Assert.Contains("GET routes/10", Calls(s.World).Skip(requests));
        }

        Assert.Equal(before, Json(job.Items));
        Assert.Equal(before, Json(s.Disk.Jobs.Single().Items));
        Assert.Equal(ItemState.Unknown, job.Items.Single().State);
        Assert.All(s.Remote.Server.Requests.Skip(requests), r => Assert.Equal("GET", r.Method));
        Assert.Single(s.Writes());
    }

    [Fact]
    public async Task A_hard_delete_is_never_offered_the_soft_delete_confirmation()
    {
        using var s = new StoredWorld();
        var attempts = 0;
        s.Remote.Intercept = r => r.Method == "DELETE" ? (++attempts > 0 ? throw new TimeoutException("simulated") : null) : null;
        s.World.MarkDelete(ResourceKind.Fleets, "6");
        var job = s.Submit();
        await s.Run(job);
        Assert.Equal(1, attempts);
        var item = job.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.False(OperationsDeletionVerifier.CanConfirmWithoutReceipt(item));
        await Expect.BlockedAsync(() => new OperationsDeletionVerifier(s.World.Api, s.W).ConfirmWithoutReceiptAsync(job, item, s.Writer, Ct), "ApiUnknown");
    }

    // ---- the record is unchanged: mark it not applied ------------------------------------------------------------

    [Theory]
    [InlineData(ResourceKind.Routes)]
    [InlineData(ResourceKind.Airports)]
    public async Task An_unanswered_soft_delete_that_still_reads_exactly_as_before_can_be_marked_not_applied(ResourceKind kind)
    {
        var (s, job, path) = await Unanswered(kind, serverApplied: false);
        using var owner = s;
        var local = job.Items.Single().After.LocalId;

        await ResumeAndCheck(s, job);
        var item = job.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.True(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        var verifier = new OperationsDeletionVerifier(s.World.Api, s.W);
        var review = await verifier.InspectWithoutReceiptAsync(job, item, Ct);
        Assert.True(review.CanMarkUnchanged);
        Assert.False(review.CanConfirmDeleted);
        Assert.Empty(review.Differences);
        Assert.False(await verifier.ConfirmWithoutReceiptAsync(job, item, s.Writer, Ct));

        var before = Json(job.Items);
        var data = s.W.Resources[kind];
        var rows = Json(new { data.Snapshot, data.Draft });
        var failing = new FailingWriter(s.Writer, _ => true);
        await Assert.ThrowsAsync<PersistenceException>(() => verifier.MarkUnchangedAsync(job, review, failing));
        Assert.Equal(1, failing.Hits);
        Assert.Equal(before, Json(job.Items));
        Assert.Equal(before, Json(s.Disk.Jobs.Single().Items));

        await verifier.MarkUnchangedAsync(job, review, s.Writer);
        var done = job.Items.Single();
        Assert.Equal(ItemState.Failed, done.State);
        Assert.Equal("ApiSoftDeleteUnchanged", done.Description?.Code);
        Assert.Equal("SoftDeleteUnchanged", done.ManualResolution!.Source);
        Assert.False(done.ManualResolution.WriteReceipt);
        Assert.Equal(200, done.ManualResolution.ReadStatus);
        Assert.Equal("UnchangedSinceBefore", done.DeletionVerification!.Outcome);
        Assert.Equal(200, done.DeletionVerification.ReadStatus);
        // the rows are untouched and the deletion stays in the draft, so the change can be reviewed and submitted again
        Assert.Equal(rows, Json(new { data.Snapshot, data.Draft }));
        Assert.True(Schemas.Deleted(data.FindDraftRow(local)!));
        Assert.False(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        Assert.False(WorkspaceStore.HasUnresolvedWrites(s.W));
        Assert.Empty(s.W.VerifiedOperations);
        Assert.Equal(Json(job.Items), Json(s.Disk.Jobs.Single().Items));

        await s.Run(job);
        Assert.Equal(ItemState.Failed, job.Items.Single().State);
        Assert.Equal([$"DELETE {path}"], s.Writes());
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("read-failure")]
    [InlineData("accepted")]
    public async Task A_record_that_differs_from_before_a_404_a_failed_read_or_a_receipt_is_never_marked_not_applied(string variant)
    {
        using var s = variant == "accepted" ? await StoredWorld.AcceptedSoftDelete() : (await Unanswered(ResourceKind.Routes, serverApplied: variant == "missing")).S;
        var job = s.W.Jobs.Single();
        var item = job.Items.Single();
        var failedReads = 0;
        if (variant == "changed")
            s.Remote.Rows["routes/10"]["remarks"] = "Edited elsewhere";
        if (variant == "read-failure")
            s.Remote.Intercept = r => { failedReads++; return Status(500); };
        var verifier = new OperationsDeletionVerifier(s.World.Api, s.W);
        var before = Json(job.Items);
        var requests = s.Remote.Server.Requests.Count;

        if (variant == "accepted")
            await Expect.BlockedAsync(() => verifier.InspectWithoutReceiptAsync(job, item, Ct), "ApiUnknown");
        else if (variant == "read-failure")
        {
            await Assert.ThrowsAnyAsync<ApiResponseException>(() => verifier.InspectWithoutReceiptAsync(job, item, Ct));
            Assert.True(failedReads > 0, "the failing read was never reached");
        }
        else
        {
            var review = await verifier.InspectWithoutReceiptAsync(job, item, Ct);
            Assert.False(review.CanMarkUnchanged);
            if (variant == "changed")
                Assert.Contains(review.Differences, d => d.Field == "Remarks" && d.Actual == "Edited elsewhere");
            else
                Assert.True(review.CanConfirmDeleted);
            await Expect.BlockedAsync(() => verifier.MarkUnchangedAsync(job, review, s.Writer), "ApiUnknown");
        }

        Assert.Equal(before, Json(job.Items));
        Assert.Equal(before, Json(s.Disk.Jobs.Single().Items));
        Assert.All(s.Remote.Server.Requests.Skip(requests), r => Assert.Equal("GET", r.Method));
        Assert.Single(s.Writes());
    }
}
