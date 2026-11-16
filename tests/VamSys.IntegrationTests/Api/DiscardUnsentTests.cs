using VamSys.Core;
using VamSys.Infrastructure;
using static VamSys.IntegrationTests.Api.PipelineHarness;
using static VamSys.IntegrationTests.Api.StoredWorld;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// Items of a stopped online task that never reached the server can be given up without contacting it: they become Failed (every
/// executor skips Failed items), the drafts stay, and the workspace is unblocked. Anything that may have reached the server is refused.
/// </summary>
[Collection(StoredWorldCollection.Name)]
public class DiscardUnsentTests
{
    [Fact]
    public async Task Discarding_never_sent_items_unblocks_the_workspace_keeps_the_drafts_and_nothing_is_sent_on_resume()
    {
        using var s = new StoredWorld();
        s.World.MarkDelete(ResourceKind.Routes, "10");
        s.World.MarkDelete(ResourceKind.Airports, "103");
        var job = s.Submit();
        Assert.All(job.Items, i => Assert.Equal(ItemState.Pending, i.State));
        Assert.True(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        // a pending task blocks changing the connection
        var blocked = Assert.Throws<InvalidOperationException>(() => s.Store.SaveConnectionSettings(s.W, "123", "secret", "", false));
        Assert.Equal("ApiPendingJob", MessageErrors.Describe(blocked).Code);
        var drafts = s.W.Resources[ResourceKind.Routes].Draft.Count;

        Assert.Equal(2, await OperationsPendingDiscard.DiscardAsync(s.W, job, s.Writer));

        Assert.All(job.Items, i => { Assert.Equal(ItemState.Failed, i.State); Assert.True(ApiOperationPolicy.IsDiscardedUnsent(i)); });
        Assert.False(ApiOperationPolicy.HasOpenOnlineItems(s.W));
        Assert.Equal(drafts, s.W.Resources[ResourceKind.Routes].Draft.Count);
        Assert.True(Schemas.Deleted(s.W.Resources[ResourceKind.Routes].Draft.Single(r => r.Identity?.RemoteId == "10")));
        var disk = s.Disk;
        Assert.All(disk.Jobs.Single().Items, i => Assert.True(ApiOperationPolicy.IsDiscardedUnsent(i)));
        Assert.False(ApiOperationPolicy.HasOpenOnlineItems(disk));

        await s.Run(job);
        Assert.Empty(s.Writes());
        Assert.All(job.Items, i => Assert.True(ApiOperationPolicy.IsDiscardedUnsent(i)));
        s.Store.SaveConnectionSettings(s.W, "123", "secret", "", false);
    }

    [Fact]
    public void A_skipped_item_continue_can_still_send_holds_the_connection_settings_until_what_it_waits_on_fails()
    {
        using var s = new StoredWorld();
        s.World.MarkDelete(ResourceKind.Routes, "10");
        s.World.MarkDelete(ResourceKind.Airports, "103");
        var job = s.Submit();
        var (waited, skipped) = (job.Items[0], job.Items[1]);
        waited.State = ItemState.Succeeded;
        skipped.State = ItemState.Skipped;
        skipped.Dependencies = [waited.Id];
        var blocked = Assert.Throws<InvalidOperationException>(() => s.Store.SaveConnectionSettings(s.W, "123", "secret", "", false));
        Assert.Equal("ApiPendingJob", MessageErrors.Describe(blocked).Code);

        waited.State = ItemState.Failed;
        s.Store.SaveConnectionSettings(s.W, "123", "secret", "", false);
    }

    [Fact]
    public async Task Only_items_that_never_reached_the_server_can_be_discarded_and_a_failed_save_changes_nothing()
    {
        using var s = new StoredWorld();
        ChangeItem Rename(BatchStepState? step, bool accepted = false, bool created = false, ItemState state = ItemState.Pending)
        {
            var before = s.W.Resources[ResourceKind.Fleets].Snapshot[0].Dup();
            var after = before.With("Name", "Renamed");
            return new ChangeItem
            {
                Resource = ResourceKind.Fleets,
                Kind = ChangeKind.Update,
                State = state,
                Before = before,
                After = after,
                Fields = new() { ["Name"] = new(FieldIntent.Set, "Renamed") },
                WriteAccepted = accepted,
                CreateCompleted = created,
                ApiSteps = step is { } value ? [new() { Method = "PUT", Path = "fleet/4", State = value }] : []
            };
        }
        // each of these may have reached the server: a step in flight or answered, an accepted write, a completed create, an Unknown or Running item
        var refused = new[]
        {
            Rename(BatchStepState.InFlight), Rename(BatchStepState.ResponseReceived), Rename(BatchStepState.Prepared, accepted: true),
            Rename(BatchStepState.Prepared, created: true), Rename(null, state: ItemState.Unknown), Rename(BatchStepState.Prepared, state: ItemState.Running)
        };
        var eligible = Rename(BatchStepState.Prepared);
        Assert.All(refused, i => Assert.False(ApiOperationPolicy.CanDiscardUnsent(i)));
        Assert.True(ApiOperationPolicy.CanDiscardUnsent(eligible));
        var job = new BatchJob { Mode = RunMode.Online, Items = [.. refused, eligible] };
        s.W.Jobs.Add(job);
        s.Store.Save(s.W);
        var before = Json(job.Items);

        var failing = new FailingWriter(s.Writer, _ => true);
        await Assert.ThrowsAsync<PersistenceException>(() => OperationsPendingDiscard.DiscardAsync(s.W, job, failing));
        Assert.Equal(1, failing.Hits);
        Assert.Equal(before, Json(job.Items));
        Assert.Equal(before, Json(s.Disk.Jobs.Single().Items));

        Assert.Equal(1, await OperationsPendingDiscard.DiscardAsync(s.W, job, s.Writer));
        Assert.Equal(Json(refused), Json(job.Items.Take(refused.Length)));
        Assert.True(ApiOperationPolicy.IsDiscardedUnsent(job.Items[^1]));
        Assert.Equal(Json(job.Items), Json(s.Disk.Jobs.Single().Items));
        // nothing left to discard, and the server was never contacted
        Assert.Equal(0, await OperationsPendingDiscard.DiscardAsync(s.W, job, s.Writer));
        Assert.Empty(s.Remote.Server.Requests);

        var demo = new BatchJob { Mode = RunMode.Demo, Items = [Rename(null)] };
        s.W.Jobs.Add(demo);
        await Expect.BlockedAsync(() => OperationsPendingDiscard.DiscardAsync(s.W, demo, s.Writer), "ApiUnknown");
    }
}
