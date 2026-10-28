using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Presentation;

namespace VamSys.App;

// What the import page asks of the window: the guard every page button uses, and the two operations it starts.
public sealed record ImportPageActions(Func<Func<Task>, Task> Invoke, Func<Task> ImportCsv, Func<Task> Refresh);

public sealed partial class ImportPage : UserControl
{
    readonly ImportPageActions actions;

    public ImportPage(ImportViewModel model, ImportPageActions actions)
    {
        Model = model;
        this.actions = actions;
        InitializeComponent();
    }

    public ImportViewModel Model { get; }

    async void Import(object sender, RoutedEventArgs e) => await actions.Invoke(actions.ImportCsv);
    async void Refresh(object sender, RoutedEventArgs e) => await actions.Invoke(actions.Refresh);
}
