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
            }), () => UserAction(() => ExportTaskDiagnostics(job.Id)), () => UserAction(() => VerifyTaskDeletions(job)), () => UserAction(() => DiscardUnsent(job)));
        return vm;
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
        var list = new ListView
        {
            MaxHeight = UiTokens.TaskDetailMaxHeight,
            ItemsSource = vm.Items,
            SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = (DataTemplate)XamlReader.Load("""<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Text="{Binding Text}" TextWrapping="Wrap" Padding="8"/></DataTemplate>""")
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(list, "TaskDetailsList");
        await Confirm(L("Text_4DCE8EF2E4"), list, L("Text_3FD47EDCE4"));
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
        if (ids.Count == 0 || !await Confirm(L("ApiDiscardPending"), Text(() => L("ApiDiscardConfirm", ("count", ids.Count))), L("ApiDiscardPending")))
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
