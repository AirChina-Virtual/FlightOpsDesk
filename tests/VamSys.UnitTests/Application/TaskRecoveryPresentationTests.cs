using VamSys.Presentation;
using VamSys.Core;

namespace VamSys.UnitTests.Application;

/// <summary>How the task page shows the ways out of a blocked task: discarded items are not failures, and manual confirmations are counted apart.</summary>
public class TaskRecoveryPresentationTests
{
    static readonly Func<Task> Nothing = () => Task.CompletedTask;

    [Fact]
    public void Discarded_items_are_shown_apart_from_real_failures_and_discarding_is_offered_only_for_never_sent_items()
    {
        var language = new LocalizationService();
        var pending = new ChangeItem { Resource = ResourceKind.Fleets, State = ItemState.Pending };
        var failed = new ChangeItem { Resource = ResourceKind.Fleets, State = ItemState.Failed };
        failed.SetMessage(Messages.Define("ApiHttp", ("status", 422)));
        var job = new BatchJob { Mode = RunMode.Online, Items = [pending, failed] };
        var model = new TaskJobViewModel(job, language, () => true, () => RunMode.Offline, Nothing, Nothing, Nothing, Nothing, discard: Nothing);

        Assert.True(model.Discard.CanExecute(null));
        Assert.DoesNotContain("已放弃", model.Summary, StringComparison.Ordinal);

        pending.State = ItemState.Failed;
        pending.SetMessage(Messages.Define("ApiDiscardedUnsent"));
        model.Update(pending.Id);
        var texts = model.Items.Select(i => i.Text).ToArray();
        Assert.False(model.Discard.CanExecute(null));
        Assert.Contains("已放弃 1", model.Summary, StringComparison.Ordinal);
        Assert.Contains("· 已放弃 ·", texts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("· 失败 ·", texts[0], StringComparison.Ordinal);
        Assert.Contains("· 失败 ·", texts[1], StringComparison.Ordinal);

        language.SetLanguage("en-US");
        model.RefreshLanguage();
        Assert.Contains("Discarded", model.Items[0].Text, StringComparison.Ordinal);
        Assert.Contains("Discarded 1", model.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_demo_task_is_never_offered_discarding()
    {
        var demo = new TaskJobViewModel(new() { Mode = RunMode.Demo, Items = [new() { State = ItemState.Pending }] }, new LocalizationService(),
            () => true, () => RunMode.Demo, Nothing, Nothing, Nothing, Nothing, discard: Nothing);
        Assert.False(demo.Discard.CanExecute(null));
    }

    [Fact]
    public void Manual_confirmations_are_counted_apart_and_the_deletion_check_is_enabled_only_after_a_committed_update()
    {
        var item = new ChangeItem
        {
            Resource = ResourceKind.Routes,
            Kind = ChangeKind.Delete,
            State = ItemState.ManuallyConfirmed,
            Rebased = true,
            WriteAccepted = true,
            ApiSteps = [new() { Method = "DELETE", Path = "routes/10", State = BatchStepState.ResponseReceived }]
        };
        var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
        var mode = RunMode.Online;
        var model = new TaskJobViewModel(job, new LocalizationService(), () => true, () => mode, Nothing, Nothing, Nothing, Nothing, verifyDeletes: Nothing);

        Assert.Contains("人工确认 1", model.Summary, StringComparison.Ordinal);
        Assert.True(model.VerifyDeletes.CanExecute(null));
        var previous = model.Summary;

        // the page follows only committed updates: changing the item alone changes nothing on screen
        item.State = ItemState.Succeeded;
        Assert.Equal(previous, model.Summary);
        Assert.True(model.VerifyDeletes.CanExecute(null));

        model.Update(item.Id);
        Assert.Contains("1/1", model.Summary, StringComparison.Ordinal);
        Assert.False(model.VerifyDeletes.CanExecute(null));

        item.State = ItemState.ManuallyConfirmed;
        model.Update(item.Id);
        mode = RunMode.Offline;
        Assert.False(model.VerifyDeletes.CanExecute(null));
    }
}
