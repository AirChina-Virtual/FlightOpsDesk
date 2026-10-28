using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.App;

// What the workspace page asks of the window: the guard every page button uses, and the actions behind its buttons.
public sealed record WorkspacePageActions(
    Func<Func<Task>, Task> Invoke,
    Func<Task> NewWorkspace,
    Func<Task> CreateDemo,
    Action<ResourceKind> OpenResource);

public sealed partial class WorkspacePage : UserControl
{
    readonly WorkspacePageActions actions;

    public WorkspacePage(WorkspaceViewModel model, WorkspacePageActions actions)
    {
        Model = model;
        this.actions = actions;
        InitializeComponent();
    }

    public WorkspaceViewModel Model { get; }

    async void NewWorkspace(object sender, RoutedEventArgs e) => await actions.Invoke(actions.NewWorkspace);
    async void CreateDemo(object sender, RoutedEventArgs e) => await actions.Invoke(actions.CreateDemo);
    async void OpenResource(object sender, RoutedEventArgs e)
    {
        var card = (ResourceCardViewModel)((FrameworkElement)sender).DataContext;
        await actions.Invoke(() => { actions.OpenResource(card.Kind); return Task.CompletedTask; });
    }
}
