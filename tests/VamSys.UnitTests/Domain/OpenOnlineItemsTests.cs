using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Domain;

/// <summary>
/// Refresh, single-resource refresh and connection settings are held while an online task may still send something:
/// a pending, running or unknown item, or a skipped one "Continue" can still send. The task card offers "Continue" then.
/// </summary>
public class OpenOnlineItemsTests
{
    static (Workspace Workspace, BatchJob Job, ChangeItem Waited, ChangeItem Skipped) SkippedTask(ItemState waitedOn, RunMode mode = RunMode.Online)
    {
        var waited = new ChangeItem { Resource = ResourceKind.Fleets, State = waitedOn };
        var skipped = new ChangeItem { Resource = ResourceKind.Aircraft, State = ItemState.Skipped };
        skipped.Dependencies = [waited.Id];
        var job = new BatchJob { Mode = mode, Items = [waited, skipped] };
        return (new Workspace { Mode = RunMode.Online, Jobs = [job] }, job, waited, skipped);
    }

    [Fact]
    public void A_skipped_item_whose_dependency_succeeded_holds_refresh_and_settings_and_can_be_continued()
    {
        var (workspace, job, _, skipped) = SkippedTask(ItemState.Succeeded);
        Assert.True(ApiOperationPolicy.HasOpenOnlineItems(workspace));
        Assert.Equal([skipped], ApiOperationPolicy.OpenItems(job));
        var card = new TaskJobViewModel(job, new LocalizationService(), () => true, () => RunMode.Online, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        Assert.True(card.ShowsResume);
        Assert.True(card.Resume.CanExecute(null));
    }

    [Fact]
    public void A_skipped_item_that_waits_on_an_open_item_is_open_too()
    {
        var (workspace, job, _, skipped) = SkippedTask(ItemState.Unknown);
        Assert.True(ApiOperationPolicy.HasOpenOnlineItems(workspace));
        Assert.Contains(skipped, ApiOperationPolicy.OpenItems(job));
    }

    [Theory]
    [InlineData(ItemState.Failed)]
    [InlineData(ItemState.Conflict)]
    [InlineData(ItemState.ManuallyConfirmed)]
    public void A_skipped_item_that_waits_on_a_final_unsuccessful_item_holds_nothing(ItemState waitedOn)
    {
        var (workspace, job, waited, _) = SkippedTask(waitedOn);
        Assert.False(ApiOperationPolicy.HasOpenOnlineItems(workspace));
        Assert.Empty(ApiOperationPolicy.OpenItems(job));
        var card = new TaskJobViewModel(job, new LocalizationService(), () => true, () => RunMode.Online, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        Assert.False(card.ShowsResume);
        // Once the item it waits on succeeds (a recheck settled it), the skipped item can be continued again.
        waited.State = ItemState.Succeeded;
        waited.Rebased = true;
        card.Update(waited.Id);
        Assert.True(card.ShowsResume);
        Assert.True(ApiOperationPolicy.HasOpenOnlineItems(workspace));
    }

    [Fact]
    public void Demo_tasks_do_not_hold_the_online_workspace()
        => Assert.False(ApiOperationPolicy.HasOpenOnlineItems(SkippedTask(ItemState.Succeeded, RunMode.Demo).Workspace));
}
