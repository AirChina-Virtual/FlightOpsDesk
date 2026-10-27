using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;
using static VamSys.IntegrationTests.Api.StoredWorld;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// An accepted write whose read-back keeps differing leaves the item Unknown and blocks the workspace. The way out never writes:
/// the person accepts the record as the server holds it now. It becomes ManuallyConfirmed (never a verified operation) and what
/// did not take effect stays in the draft as a conflict.
/// </summary>
[Collection(StoredWorldCollection.Name)]
public class AcceptServerValueTests
{
    /// <summary>
    /// Runs one job whose single write the server accepts but does not keep. The scripted answer is hit exactly once (asserted),
    /// so the item is really an accepted write with a differing read-back.
    /// </summary>
    static async Task<(StoredWorld S, BatchJob Job, string Write)> AcceptedWrite(string variant)
    {
        var s = new StoredWorld();
        var hits = 0;
        string write;
        if (variant == "update")
        {
            write = "PUT fleet/4";
            s.World.Edit(ResourceKind.Fleets, "4", ("Name", "Local name"));
            s.Remote.Intercept = r =>
            {
                if (r.Method != "PUT") return null;
                hits++;
                s.Remote.Rows["fleet/4"]["name"] = "SERVER NAME";
                return FakeResponse.Ok("""{"data":{}}""");
            };
        }
        else if (variant == "create")
        {
            write = "POST fleet";
            s.World.New(ResourceKind.Fleets, ("Name", "Local name"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "cargo"), ("Parameter IDs", "1"));
            s.Remote.Intercept = r =>
            {
                if (r.Method != "POST") return null;
                hits++;
                var created = s.Remote.Respond(r);
                s.Remote.Rows["fleet/90"]["name"] = "SERVER NAME";
                return created;
            };
        }
        else
        {
            // a hard delete the server answers 204 and does not carry out
            write = "DELETE fleet/6";
            s.World.MarkDelete(ResourceKind.Fleets, "6");
            s.Remote.Intercept = r =>
            {
                if (r.Method != "DELETE") return null;
                hits++;
                return new FakeResponse(System.Net.HttpStatusCode.NoContent, null);
            };
        }
        var job = s.Submit();
        await s.Run(job);
        s.Remote.Intercept = null;
        Assert.Equal(1, hits);
        var item = job.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.True(item.WriteAccepted || item.CreateCompleted);
        Assert.Equal([write], s.Writes());
        return (s, job, write);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("create")]
    [InlineData("delete")]
    public async Task An_accepted_write_whose_read_back_differs_can_accept_the_servers_current_value_without_writing(string variant)
    {
        var (s, job, write) = await AcceptedWrite(variant);
        using var owner = s;
        var item = job.Items.Single();
        var local = item.After.LocalId;

        // before: every resume reads again, still differs and stays Unknown, blocking the workspace
        await s.Run(job);
        item = job.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.True(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        Assert.True(OperationsServerValueResolver.CanAccept(item));

        var resolver = new OperationsServerValueResolver(s.World.Api, s.W);
        var review = await resolver.InspectAsync(job, item, Ct);
        Assert.True(review.CanAccept);
        Assert.Contains(review.Differences, d => d.Field == (variant == "delete" ? "record" : "name"));

        // a checkpoint that cannot be written changes nothing: not the task, not the rows, not the disk
        var before = Json(job.Items);
        var data = s.W.Resources[item.Resource];
        var rows = Json(new { data.Snapshot, data.Draft, data.Conflicts });
        var failing = new FailingWriter(s.Writer, _ => true);
        await Assert.ThrowsAsync<PersistenceException>(() => resolver.AcceptAsync(job, review, failing));
        Assert.Equal(1, failing.Hits);
        Assert.Equal(before, Json(job.Items));
        Assert.Equal(rows, Json(new { data.Snapshot, data.Draft, data.Conflicts }));
        Assert.Equal(before, Json(s.Disk.Jobs.Single().Items));

        await resolver.AcceptAsync(job, review, s.Writer);
        var done = job.Items.Single();
        Assert.Equal(ItemState.ManuallyConfirmed, done.State);
        Assert.True(done.Rebased);
        Assert.Equal("AcceptServerValue", done.ManualResolution!.Source);
        Assert.True(done.ManualResolution.WriteReceipt);
        Assert.Equal(200, done.ManualResolution.ReadStatus);
        Assert.NotEmpty(done.VerificationDifferences);
        Assert.Equal(variant == "delete" ? "ApiAcceptedServerRecord" : "ApiAcceptedServerValue", done.Description?.Code);
        Assert.False(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        Assert.False(WorkspaceStore.HasUnresolvedWrites(s.W));
        Assert.Empty(s.W.VerifiedOperations);

        var snapshot = data.Snapshot.Single(r => r.LocalId == local);
        var draft = data.FindDraftRow(local)!;
        if (variant == "delete")
        {
            Assert.False(Schemas.Deleted(draft));
            Assert.Equal("6", snapshot.Identity!.RemoteId);
            Assert.Empty(data.Conflicts);
        }
        else
        {
            // the server's value is the snapshot, the person's value stays in the draft and is shown as a conflict
            Assert.Equal("SERVER NAME", snapshot.Get("Name"));
            Assert.Equal("Local name", draft.Get("Name"));
            Assert.Equal(snapshot.Identity, draft.Identity);
            var conflict = Assert.Single(data.Conflicts);
            Assert.Equal("Name", conflict.Field);
            Assert.Equal("Local name", conflict.Local);
            Assert.Equal("SERVER NAME", conflict.Remote);
        }
        var disk = s.Disk;
        var saved = disk.Resources[item.Resource];
        Assert.Equal(Json(job.Items), Json(disk.Jobs.Single().Items));
        Assert.Equal(Json(data.Conflicts), Json(saved.Conflicts));
        Assert.Equal(Json(snapshot), Json(saved.Snapshot.Single(r => r.LocalId == local)));
        Assert.Equal(Json(draft), Json(saved.Draft.Single(r => r.LocalId == local)));

        // resuming neither writes nor reopens it: the only write ever sent is the original one
        await s.Run(job);
        Assert.Equal(ItemState.ManuallyConfirmed, job.Items.Single().State);
        Assert.Equal([write], s.Writes());
    }

    // ---- when it must not be offered ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("read-failure")]
    [InlineData("missing")]
    [InlineData("matches")]
    public async Task The_server_value_is_accepted_only_after_a_successful_read_that_still_differs(string variant)
    {
        var (s, job, write) = await AcceptedWrite("update");
        using var owner = s;
        var item = job.Items.Single();
        var before = Json(job.Items);
        var failedReads = 0;
        if (variant == "read-failure")
            s.Remote.Intercept = r => { failedReads++; return Status(500); };
        if (variant == "missing")
            s.Remote.Rows.Remove("fleet/4");
        if (variant == "matches")
            s.Remote.Rows["fleet/4"]["name"] = "Local name";
        var resolver = new OperationsServerValueResolver(s.World.Api, s.W);
        var requests = Calls(s.World).Length;

        if (variant == "read-failure")
        {
            await Assert.ThrowsAnyAsync<ApiResponseException>(() => resolver.InspectAsync(job, item, Ct));
            Assert.True(failedReads > 0, "the failing read was never reached");
        }
        else
        {
            var review = await resolver.InspectAsync(job, item, Ct);
            Assert.False(review.CanAccept);
            Assert.Equal(variant == "missing", review.Current == null);
            await Expect.BlockedAsync(() => resolver.AcceptAsync(job, review, s.Writer), "ApiUnknown");
            Assert.Contains("GET fleet/4", Calls(s.World).Skip(requests));
        }

        Assert.Equal(before, Json(job.Items));
        Assert.Equal(before, Json(s.Disk.Jobs.Single().Items));
        Assert.Equal([write], s.Writes());
    }

    [Theory]
    [InlineData("soft-delete")]
    [InlineData("move")]
    [InlineData("unaccepted")]
    [InlineData("resolved")]
    public async Task Soft_deletes_moves_writes_without_a_receipt_and_resolved_items_are_never_offered_the_servers_value(string kind)
    {
        StoredWorld s;
        if (kind == "soft-delete")
            s = await AcceptedSoftDelete();
        else
        {
            s = new StoredWorld();
            var hits = 0;
            if (kind == "move")
            {
                s.World.Edit(ResourceKind.Aircraft, "8", ("Fleet ID", "5"));
                // the target path still names the old fleet after the PATCH: an accepted move whose read-back differs
                s.Remote.Intercept = r => r.Method == "GET" && r.Path.EndsWith("/fleet/5/aircraft/8", StringComparison.Ordinal) && Count(s.World, "PATCH") > 0
                    ? (++hits > 0 ? FakeResponse.Ok("""{"data":{"id":8,"airline_id":7,"fleet_id":4,"registration":"B-TEST","name":"Plane","cargo":1000}}""") : null) : null;
            }
            else
            {
                s.World.Edit(ResourceKind.Fleets, "4", ("Name", "Local name"));
                if (kind == "unaccepted")
                    s.Remote.Intercept = r => r.Method == "PUT" ? (++hits > 0 ? Status(500) : null) : null;
            }
            var created = s.Submit();
            await s.Run(created);
            s.Remote.Intercept = null;
            if (kind != "resolved")
                Assert.True(hits > 0, "the scripted answer was never reached");
        }
        using var owner = s;
        var job = s.W.Jobs.Single();
        var item = job.Items.Single();
        Assert.Equal(kind == "resolved" ? ItemState.Succeeded : ItemState.Unknown, item.State);
        if (kind == "move")
            Assert.True(item.WriteAccepted);
        if (kind == "unaccepted")
            Assert.False(item.WriteAccepted);
        var before = Json(job.Items);
        var requests = Calls(s.World).Length;

        Assert.False(OperationsServerValueResolver.CanAccept(item));
        await Expect.BlockedAsync(() => new OperationsServerValueResolver(s.World.Api, s.W).InspectAsync(job, item, Ct), "ApiUnknown");

        Assert.Equal(before, Json(job.Items));
        Assert.Equal(requests, Calls(s.World).Length);
    }
}
