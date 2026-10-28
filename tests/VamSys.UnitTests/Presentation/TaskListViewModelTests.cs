using VamSys.Core;
using VamSys.Presentation;
using VamSys.UnitTests.Application;

namespace VamSys.UnitTests.Presentation;

public class TaskListViewModelTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    sealed class Fixture
    {
        public readonly LocalizationService Language = new();
        public readonly FakeTimer Timer = new();
        public readonly TaskListViewModel List;
        public readonly Workspace Workspace = new();
        public int Created;
        public Fixture()
        {
            Language.SetLanguage("en-US");
            List = new(Language, Timer, job =>
            {
                Created++;
                return new TaskJobViewModel(job, Language, () => true, () => RunMode.Demo,
                    () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
            });
        }
        public BatchJob Add(int minutes, int items = 2)
        {
            var job = new BatchJob { Created = T0.AddMinutes(minutes), Mode = RunMode.Demo, Items = Enumerable.Range(0, items).Select(_ => new ChangeItem()).ToList() };
            Workspace.Jobs.Add(job);
            return job;
        }
    }

    [Fact]
    public void Cards_are_newest_first_and_a_later_job_is_inserted_in_its_place()
    {
        var fixture = new Fixture();
        var older = fixture.Add(0);
        var newest = fixture.Add(20);
        Assert.True(fixture.List.Sync(fixture.Workspace));
        Assert.Equal([newest.Id, older.Id], fixture.List.Jobs.Select(j => j.Id));
        var middle = fixture.Add(10);
        Assert.False(fixture.List.Sync(fixture.Workspace));
        Assert.Equal([newest.Id, middle.Id, older.Id], fixture.List.Jobs.Select(j => j.Id));
        // Nothing new: no card is created again.
        fixture.List.Sync(fixture.Workspace);
        Assert.Equal(3, fixture.Created);
    }

    [Fact]
    public void Another_workspace_instance_starts_over()
    {
        var fixture = new Fixture();
        fixture.Add(0);
        fixture.List.Sync(fixture.Workspace);
        var reloaded = Workspace.Deserialize(fixture.Workspace.Serialize());
        Assert.True(fixture.List.Sync(reloaded));
        Assert.Same(reloaded, fixture.List.Owner);
        Assert.Equal(2, fixture.Created);
        Assert.Single(fixture.List.Jobs);
    }

    [Fact]
    public void A_change_is_counted_at_once_but_shown_only_when_the_presentation_timer_fires()
    {
        var fixture = new Fixture();
        var job = fixture.Add(0);
        fixture.List.Sync(fixture.Workspace);
        var card = fixture.List.Jobs.Single();
        var shown = new List<string?>();
        card.PropertyChanged += (_, e) => shown.Add(e.PropertyName);
        var before = card.Summary;

        job.Items[0].State = ItemState.Succeeded;
        fixture.List.Notify(fixture.Workspace, job.Id, job.Items[0].Id, immediate: false);
        Assert.Empty(shown);
        Assert.True(fixture.Timer.IsEnabled);
        // The committed count is already there for anyone who reads it.
        Assert.NotEqual(before, card.Summary);

        job.Items[1].State = ItemState.Succeeded;
        fixture.List.Notify(fixture.Workspace, job.Id, job.Items[1].Id, immediate: false);
        Assert.Equal(1, fixture.Timer.Starts);

        fixture.List.Flush();
        Assert.False(fixture.Timer.IsEnabled);
        Assert.Equal(1, shown.Count(n => n == nameof(TaskJobViewModel.Summary)));
        fixture.List.Flush();
        Assert.Equal(1, shown.Count(n => n == nameof(TaskJobViewModel.Summary)));
    }

    [Fact]
    public void An_immediate_change_is_shown_at_once_with_the_ones_waiting()
    {
        var fixture = new Fixture();
        var job = fixture.Add(0);
        fixture.List.Sync(fixture.Workspace);
        var card = fixture.List.Jobs.Single();
        _ = card.Items;
        var itemTexts = 0;
        foreach (var item in card.Items)
            item.PropertyChanged += (_, _) => itemTexts++;
        fixture.List.Notify(fixture.Workspace, job.Id, job.Items[0].Id, immediate: false);
        fixture.List.Notify(fixture.Workspace, job.Id, job.Items[1].Id, immediate: true);
        Assert.False(fixture.Timer.IsEnabled);
        Assert.Equal(2, itemTexts);
    }

    [Fact]
    public void A_change_of_a_job_the_list_has_not_seen_adds_its_card_and_an_unknown_job_is_ignored()
    {
        var fixture = new Fixture();
        fixture.List.Sync(fixture.Workspace);
        var job = fixture.Add(0);
        fixture.List.Notify(fixture.Workspace, job.Id, null, immediate: false);
        Assert.True(fixture.List.TryGet(job.Id, out _));
        fixture.Timer.Stop();
        fixture.List.Notify(fixture.Workspace, Guid.NewGuid(), null, immediate: false);
        Assert.False(fixture.Timer.IsEnabled);
    }

    [Fact]
    public void A_language_refresh_shows_waiting_changes_and_translates_the_page()
    {
        var fixture = new Fixture();
        var job = fixture.Add(0);
        fixture.List.Sync(fixture.Workspace);
        fixture.List.Notify(fixture.Workspace, job.Id, job.Items[0].Id, immediate: false);
        var english = fixture.List.CancelLabel;
        var page = new List<string?>();
        fixture.List.PropertyChanged += (_, e) => page.Add(e.PropertyName);
        fixture.Language.SetLanguage("zh-CN");
        fixture.List.RefreshLanguage();
        Assert.False(fixture.Timer.IsEnabled);
        Assert.NotEqual(english, fixture.List.CancelLabel);
        Assert.Equal([nameof(TaskListViewModel.Header), nameof(TaskListViewModel.CancelLabel)], page);
    }

    [Fact]
    public void Clearing_forgets_the_owner_cards_and_waiting_changes()
    {
        var fixture = new Fixture();
        var job = fixture.Add(0);
        fixture.List.Sync(fixture.Workspace);
        fixture.List.Notify(fixture.Workspace, job.Id, job.Items[0].Id, immediate: false);
        fixture.List.Clear();
        Assert.Null(fixture.List.Owner);
        Assert.Empty(fixture.List.Jobs);
        Assert.False(fixture.List.TryGet(job.Id, out _));
    }
}
