using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Presentation;

namespace VamSys.App;

// What the review page asks of the window: the guard every page button uses, and the actions behind its buttons.
public sealed record ReviewPageActions(Func<Func<Task>, Task> Invoke, Func<Task> ShowIssues, Func<Task> Submit, Func<Task> ResolveConflicts);

public sealed partial class ReviewPage : UserControl
{
    readonly ReviewPageActions actions;

    public ReviewPage(ReviewViewModel model, ReviewPageActions actions)
    {
        Model = model;
        this.actions = actions;
        InitializeComponent();
    }

    public ReviewViewModel Model { get; }

    async void ShowIssues(object sender, RoutedEventArgs e) => await actions.Invoke(actions.ShowIssues);
    async void Submit(object sender, RoutedEventArgs e) => await actions.Invoke(actions.Submit);
    async void ResolveConflicts(object sender, RoutedEventArgs e) => await actions.Invoke(actions.ResolveConflicts);
}

// A resource heading and a reviewed change look different.
public sealed partial class ReviewRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Heading { get; set; }
    public DataTemplate? Change { get; set; }
    protected override DataTemplate SelectTemplateCore(object item) => (item is ReviewRowViewModel { IsHeading: true } ? Heading : Change)!;
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
