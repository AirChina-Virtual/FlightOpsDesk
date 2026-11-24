using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;
using static VamSys.IntegrationTests.Api.StoredWorld;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// A new aircraft with internal remarks is a POST without them, then a PUT of the remarks to the id the POST's answer gave
/// (<see cref="AircraftInternalRemarksTests"/>). When the POST's answer is lost the PUT is never sent. The record the POST
/// made is still found, by the values the POST sent, and bound to the item, which stays Unknown and says so; the way out is
/// accepting the record as the server holds it. Nothing but the one POST is ever written: not on "Continue", not in the
/// end-of-round check, not after a restart.
/// </summary>
[Collection(StoredWorldCollection.Name)]
public class AircraftLostPostTests
{
    const string Created = "fleet/4/aircraft/90";
    const string Post = "POST fleet/4/aircraft";

    static StoredWorld World(string remarks = "hangar 3")
    {
        var s = new StoredWorld();
        s.Remote.AircraftCreateDropsInternalRemarks = true;
        s.World.New(ResourceKind.Aircraft, ("Name", "Plane Two"), ("Registration", "QA-2"), ("Fleet ID", "4"), ("Internal Remarks", remarks));
        return s;
    }

    // The server creates the aircraft (record 90), created_at on its clock, then the answer is lost. `also` runs right after.
    static void LoseTheAnswer(GoldenWorld w, TimeSpan createdAfter = default, Action? also = null)
    {
        var posted = false;
        w.Remote.Intercept = r =>
        {
            if (r.Method != "POST" || posted)
                return null;
            posted = true;
            w.Remote.Respond(r);
            w.Remote.Rows[Created]["created_at"] = (DateTimeOffset.UtcNow + createdAfter).ToString("O");
            also?.Invoke();
            throw new TimeoutException("simulated: created, answer lost");
        };
    }

    // Bound to record 90 found by the POST's values: still Unknown, explained, never resumable, open to "accept".
    static void AssertBound(ChangeItem item)
    {
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("ApiCreateFoundFollowUpsNotSent", Code(item));
        Assert.Equal("90", item.RemoteId);
        Assert.Equal("90", item.After.Identity?.RemoteId);
        Assert.Null(item.RecoveryCandidateId);
        Assert.True(item.CreateFoundWithoutAnswer);
        Assert.False(item.CreateCompleted);
        Assert.False(item.WriteAccepted);
        Assert.False(item.CanProposeRecoveryId);
        Assert.False(OperationsAdapter.CanResumeCreate(item));
        Assert.False(OperationsUnknownResolver.CanCheck(item));
        Assert.True(OperationsServerValueResolver.CanAccept(item));
        Assert.Equal(BatchStepState.InFlight, item.ApiSteps[0].State);
        Assert.Equal(BatchStepState.Prepared, item.ApiSteps[1].State);
        Assert.Contains(item.VerificationDifferences, d => d.Field == "internal_remarks");
    }

    static void AssertNotBound(ChangeItem item)
    {
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Null(item.RemoteId);
        Assert.Null(item.After.Identity);
        Assert.False(item.CreateFoundWithoutAnswer);
        Assert.False(OperationsServerValueResolver.CanAccept(item));
        Assert.True(item.CanProposeRecoveryId);
    }

    static void AssertOnlyReadsSince(StoredWorld s, int requests)
        => Assert.All(s.Remote.Server.Requests.Skip(requests), r => Assert.Equal("GET", r.Method));

    // After "accept": the server's record is the snapshot, the remarks the PUT never wrote stay in the draft as a conflict.
    static void AssertAccepted(StoredWorld s, ChangeItem item)
    {
        Assert.Equal(ItemState.ManuallyConfirmed, item.State);
        Assert.True(item.Rebased);
        Assert.Equal("AcceptServerValue", item.ManualResolution!.Source);
        Assert.Equal("ApiAcceptedServerValue", Code(item));
        Assert.Empty(s.W.VerifiedOperations);
        Assert.False(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        Assert.False(WorkspaceStore.HasUnresolvedWrites(s.W));
        var data = s.W.Resources[ResourceKind.Aircraft];
        var local = item.After.LocalId;
        var snapshot = data.Snapshot.Single(r => r.LocalId == local);
        var draft = data.FindDraftRow(local)!;
        Assert.Equal("90", snapshot.Identity!.RemoteId);
        Assert.Equal(snapshot.Identity, draft.Identity);
        Assert.Equal("", snapshot.Get("Internal Remarks"));
        Assert.Equal("hangar 3", draft.Get("Internal Remarks"));
        Assert.Equal("Plane Two", snapshot.Get("Name"));
        var conflict = Assert.Single(data.Conflicts);
        Assert.Equal(("Internal Remarks", "hangar 3"), (conflict.Field, conflict.Local));
        Assert.True(string.IsNullOrEmpty(conflict.Remote));
    }

    [Fact]
    public async Task Without_the_posts_answer_no_put_is_ever_sent()
    {
        using var s = World();
        LoseTheAnswer(s.World);
        var job = s.Submit();
        await s.Run(job);

        // the end-of-round check found the record by the POST's values and bound it; nothing else was written
        var item = job.Items.Single();
        AssertBound(item);
        Assert.Equal(("90", "Automatic"), (item.RecoveryCandidates.Single().RemoteId, item.RecoveryCandidates.Single().Source));
        Assert.Equal(["90"], item.FoundCreateCandidates);
        Assert.Equal([Post], s.Writes());
        Assert.True(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        Assert.Equal(Json(job.Items), Json(s.Disk.Jobs.Single().Items));

        // "Continue" only reads and keeps it as it is
        s.Remote.Intercept = null;
        var requests = s.Remote.Server.Requests.Count;
        await s.Run(job);
        item = job.Items.Single();
        AssertBound(item);
        AssertOnlyReadsSince(s, requests);
        Assert.Contains("GET " + Created, Calls(s.World).Skip(requests));

        // accept the server's value
        var resolver = new OperationsServerValueResolver(s.World.Api, s.W);
        var review = await resolver.InspectAsync(job, item, Ct);
        Assert.True(review.CanAccept);
        Assert.Equal("internal_remarks", Assert.Single(review.Differences).Field);
        await resolver.AcceptAsync(job, review, s.Writer);
        item = job.Items.Single();
        AssertAccepted(s, item);
        Assert.Equal(Json(job.Items), Json(s.Disk.Jobs.Single().Items));
        Assert.Equal(Json(s.W.Resources[ResourceKind.Aircraft].Conflicts), Json(s.Disk.Resources[ResourceKind.Aircraft].Conflicts));

        // neither "Continue" nor a restart writes anything or reopens it
        await s.Run(job);
        Assert.Equal(ItemState.ManuallyConfirmed, job.Items.Single().State);
        var restored = s.Disk;
        await new OperationsBatchExecutor(s.Adapter(restored), restored).ExecuteAsync(restored.Jobs.Single(), s.Store.CheckpointWriter(restored), Ct);
        Assert.Equal(ItemState.ManuallyConfirmed, restored.Jobs.Single().Items.Single().State);
        Assert.Equal([Post], s.Writes());
        Assert.Equal(0, Count(s.World, "PUT"));
        Assert.Null(s.Remote.Rows[Created]["internal_remarks"]);
    }

    [Fact]
    public async Task A_bound_record_survives_a_restart_and_the_restarted_program_only_reads_and_can_accept_it()
    {
        using var s = World();
        LoseTheAnswer(s.World);
        var job = s.Submit();
        await s.Run(job);
        AssertBound(job.Items.Single());
        s.Remote.Intercept = null;

        // the program starts again from what is on disk
        var restored = s.Disk;
        var restoredJob = restored.Jobs.Single();
        Assert.Equal(Json(job.Items), Json(restoredJob.Items));
        AssertBound(restoredJob.Items.Single());
        var api = s.Adapter(restored);
        var requests = s.Remote.Server.Requests.Count;
        await new OperationsBatchExecutor(api, restored).ExecuteAsync(restoredJob, s.Store.CheckpointWriter(restored), Ct);
        AssertBound(restoredJob.Items.Single());
        AssertOnlyReadsSince(s, requests);
        Assert.Equal(Json(restoredJob.Items), Json(s.Disk.Jobs.Single().Items));

        var resolver = new OperationsServerValueResolver(api, restored);
        var review = await resolver.InspectAsync(restoredJob, restoredJob.Items.Single(), Ct);
        await resolver.AcceptAsync(restoredJob, review, s.Store.CheckpointWriter(restored));
        Assert.Equal(ItemState.ManuallyConfirmed, restoredJob.Items.Single().State);
        Assert.Equal(ItemState.ManuallyConfirmed, s.Disk.Jobs.Single().Items.Single().State);
        Assert.Equal([Post], s.Writes());
    }

    [Fact]
    public async Task The_id_the_user_enters_is_bound_when_the_record_holds_what_the_post_sent()
    {
        using var s = World();
        // created long after the request, so the program does not bind it by itself
        LoseTheAnswer(s.World, TimeSpan.FromMinutes(10));
        var job = s.Submit();
        await s.Run(job);
        var item = job.Items.Single();
        AssertNotBound(item);
        Assert.Empty(item.FoundCreateCandidates!);

        item.ProposeRecoveryId("90");
        s.Remote.Intercept = null;
        var requests = s.Remote.Server.Requests.Count;
        await s.Run(job);
        item = job.Items.Single();
        AssertBound(item);
        AssertOnlyReadsSince(s, requests);

        var resolver = new OperationsServerValueResolver(s.World.Api, s.W);
        await resolver.AcceptAsync(job, await resolver.InspectAsync(job, item, Ct), s.Writer);
        AssertAccepted(s, job.Items.Single());
        await s.Run(job);
        Assert.Equal([Post], s.Writes());
    }

    [Fact]
    public async Task An_id_whose_record_differs_from_what_the_post_sent_is_not_bound()
    {
        using var s = World();
        LoseTheAnswer(s.World, TimeSpan.FromMinutes(10));
        var job = s.Submit();
        await s.Run(job);
        s.Remote.Intercept = null;
        s.Remote.AddRow("fleet/4/aircraft/91", """{"id":91,"airline_id":7,"fleet_id":4,"name":"Another plane","registration":"QA-9","internal_remarks":null}""");

        var item = job.Items.Single();
        item.ProposeRecoveryId("91");
        var requests = s.Remote.Server.Requests.Count;
        await s.Run(job);
        item = job.Items.Single();
        AssertNotBound(item);
        Assert.Equal("91", item.RecoveryCandidateId);
        Assert.Equal("ApiUnknown", Code(item));
        AssertOnlyReadsSince(s, requests);
        Assert.Equal([Post], s.Writes());
    }

    [Theory]
    [InlineData("existed-before")]
    [InlineData("outside-window")]
    [InlineData("two-candidates")]
    public async Task A_record_the_search_cannot_tell_is_this_create_is_not_bound(string variant)
    {
        using var s = World();
        BatchJob? job = null;
        if (variant == "existed-before")
            LoseTheAnswer(s.World, also: () => job!.Items.Single().PreCreateCandidateIds!.Add("90"));
        else if (variant == "outside-window")
            LoseTheAnswer(s.World, TimeSpan.FromMinutes(-10));
        else
            LoseTheAnswer(s.World, also: () => s.Remote.AddRow("fleet/4/aircraft/91",
                """{"id":91,"airline_id":7,"fleet_id":4,"name":"Plane Two","registration":"QA-2","internal_remarks":null}""")["created_at"] = DateTimeOffset.UtcNow.ToString("O"));
        job = s.Submit();
        await s.Run(job);
        s.Remote.Intercept = null;
        await s.Run(job);

        var item = job.Items.Single();
        AssertNotBound(item);
        if (variant == "two-candidates")
        {
            Assert.Equal("ApiCreateCandidates", Code(item));
            Assert.Equal(["90", "91"], item.FoundCreateCandidates!.Order(StringComparer.Ordinal));
        }
        else
        {
            Assert.Equal("ApiUnknown", Code(item));
            Assert.Empty(item.FoundCreateCandidates!);
        }
        Assert.Equal([Post], s.Writes());
    }
}
