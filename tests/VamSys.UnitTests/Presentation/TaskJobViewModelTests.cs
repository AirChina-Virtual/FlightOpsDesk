using VamSys.App;
using VamSys.Core;

namespace VamSys.UnitTests.Presentation;

public class TaskJobViewModelTests
{
    static TaskJobViewModel Model(BatchJob job, LocalizationService language)
        => new(job, language, () => true, () => RunMode.Online, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);

    [Fact]
    public void Task_presentation_never_observes_mutable_uncommitted_status_or_item_changes()
    {
        var item = new ChangeItem { State = ItemState.Pending };
        var job = new BatchJob { Mode = RunMode.Online, Items = [item], Status = "committed" };
        var vm = Model(job, new LocalizationService());
        var list = vm.Items;
        var text = list[0].Text;
        var title = vm.Title;
        var notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;

        item.State = ItemState.Succeeded;
        item.Message = "not committed";
        job.Status = "not committed";
        vm.RefreshHeader();
        Assert.Equal(text, list[0].Text);
        Assert.Equal(title, vm.Title);

        notifications = 0;
        vm.Update(item.Id, false);
        Assert.Equal(0, notifications);
        Assert.Contains("not committed", vm.Title, StringComparison.Ordinal);

        vm.Present(item.Id);
        vm.RefreshHeader();
        Assert.Contains("not committed", list[0].Text, StringComparison.Ordinal);
        Assert.Same(list, vm.Items);

        item.DiagnosticDetails = new() { ErrorId = "server-error-id", FieldErrors = new() { ["first"] = [new string('x', 9000)], ["fleet_ids.0"] = ["late complete validation message"] } };
        Assert.Contains("server-error-id", item.DiagnosticDetails.Summary, StringComparison.Ordinal);
        vm.Update(item.Id);
        Assert.Contains("fleet_ids.0: late complete validation message", list[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Task_counters_remain_correct_for_repeated_updates_replacement_candidates_and_rebasing()
    {
        var language = new LocalizationService();
        var item = new ChangeItem { State = ItemState.Unknown, Resource = ResourceKind.Airports };
        var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
        var vm = Model(job, language);
        Assert.True(vm.Review.CanExecute(null));
        Assert.True(vm.Resume.CanExecute(null));
        var list = vm.Items;

        job.Items[0] = new() { Id = item.Id, State = ItemState.Succeeded, Rebased = false };
        vm.Update(item.Id);
        vm.Update(item.Id);
        Assert.False(vm.Review.CanExecute(null));
        Assert.True(vm.Resume.CanExecute(null));

        job.Items[0].Rebased = true;
        vm.Update(item.Id);
        Assert.False(vm.Resume.CanExecute(null));

        language.SetLanguage("en-US");
        vm.RefreshLanguage();
        Assert.Same(list, vm.Items);
        Assert.Contains("Succeeded", list[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Large_task_updates_notify_only_the_changed_job_and_detail_row()
    {
        var language = new LocalizationService();
        var models = Enumerable.Range(0, 1000).Select(_ => Model(new() { Items = Enumerable.Range(0, 20).Select(_ => new ChangeItem()).ToList() }, language)).ToList();
        var job = new BatchJob { Items = Enumerable.Range(0, 20000).Select(_ => new ChangeItem()).ToList() };
        var active = Model(job, language);
        models.Add(active);
        var details = active.Items;
        var unrelated = 0;
        foreach (var vm in models.Take(1000))
            vm.PropertyChanged += (_, _) => unrelated++;
        var changed = 0;
        foreach (var vm in details)
            vm.PropertyChanged += (_, _) => changed++;

        for (var n = 0; n < 1000; n++)
        {
            var item = job.Items[n];
            item.State = ItemState.Succeeded;
            active.Update(item.Id, false);
            active.Present(item.Id);
            active.RefreshHeader();
        }

        Assert.Equal(0, unrelated);
        Assert.Equal(1000, changed);
        Assert.Same(details, active.Items);
    }
}
