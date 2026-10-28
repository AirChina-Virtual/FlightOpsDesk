using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Presentation;

namespace VamSys.App;

// What the task page asks of the window: the guard every page button uses, cancelling the running task, and the
// scroll offset to show when the list is put back on screen.
public sealed record TasksPageActions(Func<Func<Task>, Task> Invoke, Action Cancel, Func<double> SavedOffset);

public sealed partial class TasksPage : UserControl
{
    readonly TasksPageActions actions;

    public TasksPage(TaskListViewModel model, TasksPageActions actions)
    {
        Model = model;
        this.actions = actions;
        InitializeComponent();
    }

    public TaskListViewModel Model { get; }
    // The job list once it is on screen; the window reads its scroll offset and selection.
    public ListView? Jobs { get; private set; }

    // The page is kept per workspace, so this runs each time it is shown again.
    void JobsLoaded(object sender, RoutedEventArgs e)
    {
        var list = (ListView)sender;
        Jobs = list;
        DispatcherQueue.TryEnqueue(() => MainWindow.FindVisual<ScrollViewer>(list)?.ChangeView(null, actions.SavedOffset(), null, true));
    }

    async void Cancel(object sender, RoutedEventArgs e) => await actions.Invoke(() => { actions.Cancel(); return Task.CompletedTask; });
}
