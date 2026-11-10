using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using VamSys.Core;
namespace VamSys.App;

public sealed partial class MainWindow
{
    TasksPage? tasksPage;
    // The presentation timer of the task list: progress is shown at most every 50 ms; saving never waits for it.
    readonly DispatcherAutosaveTimer taskUpdates = new(TimeSpan.FromMilliseconds(50));
    TaskListViewModel? taskListModel;
    readonly Dictionary<Guid, double> taskScroll = [];
    int taskPageBuilds;
    TaskListViewModel Tasks
    {
        get
        {
            if (taskListModel is null)
            {
                taskListModel = new(localization, taskUpdates, CreateTaskModel);
                taskUpdates.Tick += (_, _) => taskListModel.Flush();
            }
            return taskListModel;
        }
    }
    IReadOnlyList<TaskJobViewModel> taskModels => Tasks.Jobs;
    ListView? taskList => tasksPage?.Jobs;
    TaskJobViewModel CreateTaskModel(BatchJob job)
    {
        TaskJobViewModel? vm = null;
        vm = new(job, localization, () => gate.CanAct, () => workspace.Mode,
            () => UserAction(() => TaskDetails(vm!)),
            () => UserAction(() => ReviewUnknown(job)),
            () => UserAction(() => job.Mode == RunMode.Online ? ExecuteApi(job) : ExecuteDemo(job)),
            () => UserAction(async () =>
            {
                if (!gate.CanAct)
                    return;
                foreach (var i in await demo.ResetFailedAsync(job))
                    vm!.Update(i.Id);
                await ExecuteDemo(job);
            }), () => UserAction(() => ExportTaskDiagnostics(job.Id)), () => UserAction(() => VerifyTaskDeletions(job)), () => UserAction(() => DiscardUnsent(job)),
            batch.RejectedFleetsToRemove, () => UserAction(() => RemoveRejectedFleets(job, vm!)),
            () => UserAction(() => { reviews.Invalidate(); SelectPane(ShellPage.Review); return Task.CompletedTask; }));
        return vm;
    }
    // 2.4: removes from each failed route the fleets the server refused it for, then shows the review again.
    async Task RemoveRejectedFleets(BatchJob job, TaskJobViewModel vm)
    {
        if (!gate.CanInvokeCommand || !CanEditData())
            return;
        var removed = 0;
        foreach (var item in job.Items.ToArray())
            if (batch.RejectedFleetsToRemove(item) is { Count: > 0 } fleets)
                removed += await batch.RemoveRouteFleetsAsync(item.After.LocalId, fleets);
        vm.RefreshHeader();
        reviews.Invalidate();
        SelectPane(ShellPage.Review);
        Status(() => L("ReviewFleetsRemoved", ("count", removed)));
    }
    void EnsureTasks()
    {
        // A switched or reloaded workspace starts with new cards and a new page.
        if (Tasks.Sync(workspace))
            tasksPage = null;
    }
    void NotifyTask(BatchProgress change)
    {
        if (change.WorkspaceId != workspace.Id)
            return;
        InvalidateDataViews();
        if (!ReferenceEquals(Tasks.Owner, workspace))
            tasksPage = null;
        Tasks.Notify(workspace, change.JobId, change.ItemId, change.Immediate);
    }
    void FlushTasks() => Tasks.Flush();
    void RefreshTaskAvailability()
    {
        if (!ReferenceEquals(Tasks.Owner, workspace))
            return;
        Tasks.CanCancel = running != null && gate.ControlsEnabled;
        Tasks.RefreshAvailability();
    }
    async Task TaskDetails(TaskJobViewModel vm)
    {
        FlushTasks();
        await Confirm(new TaskDetailsDialogViewModel(vm) { Title = L("Text_4DCE8EF2E4"), PrimaryLabel = L("Text_3FD47EDCE4") });
    }
    UIElement BuildTasksPage()
    {
        EnsureTasks();
        FlushTasks();
        RefreshTaskAvailability();
        if (tasksPage != null)
            return tasksPage;
        taskPageBuilds++;
        var owner = workspace.Id;
        return tasksPage = new TasksPage(Tasks, new TasksPageActions(
            Invoke: async action => { if (gate.CanInvokeCommand) await UserAction(action); },
            Cancel: () => running?.Cancel(),
            SavedOffset: () => taskScroll.GetValueOrDefault(owner)));
    }
    // Local only: never-sent items are saved as Failed before the task in memory changes.
    async Task DiscardUnsent(BatchJob job)
    {
        if (!gate.CanAct || job.Mode != RunMode.Online)
            return;
        var ids = recovery.DiscardableItems(job);
        if (ids.Count == 0 || !await ConfirmMessage(L("ApiDiscardPending"), L("ApiDiscardConfirm", ("count", ids.Count)), L("ApiDiscardPending")))
            return;
        var count = 0;
        await Work(async () =>
        {
            count = await recovery.DiscardUnsentAsync(job);
        });
        foreach (var id in ids)
            NotifyTask(new(workspace.Id, job.Id, id, id == ids[^1]));
        Status(() => L("ApiDiscarded", ("count", count)));
    }
    void CaptureTaskState()
    {
        if (Tasks.Owner != null && taskList != null && ReferenceEquals(PageHost.Content, tasksPage))
            taskScroll[Tasks.Owner.Id] = FindVisual<ScrollViewer>(taskList)?.VerticalOffset ?? 0;
    }
}
