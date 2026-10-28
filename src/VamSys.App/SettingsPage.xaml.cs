using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Presentation;

namespace VamSys.App;

// What the settings page asks of the window: the same guards every page button uses, the status line, and the
// online operations the window runs.
public sealed record SettingsPageActions(
    Func<Func<Task>, Task> Invoke,
    Func<Func<Task>, Task> Guard,
    Action<string> Status,
    Func<Task> Connect,
    Func<Task> Refresh,
    Func<Task> LoadParameters,
    Func<Task> BackupClear);

public sealed partial class SettingsPage : UserControl
{
    readonly SettingsPageActions actions;
    PasswordBox? secret;

    public SettingsPage(SettingsViewModel model, SettingsPageActions actions)
    {
        Model = model;
        this.actions = actions;
        InitializeComponent();
    }

    public SettingsViewModel Model { get; }

    void SecretLoaded(object sender, RoutedEventArgs e) => secret = (PasswordBox)sender;
    string Secret => secret?.Password ?? "";

    void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = ((ComboBox)sender).SelectedIndex;
        if (index < 0 || index == Model.LanguageIndex)
            return;
        _ = actions.Guard(() => { Model.SelectLanguage(index); return Task.CompletedTask; });
    }

    async void SaveConnection(object sender, RoutedEventArgs e) => await actions.Invoke(async () => Saved(await Model.SaveConnectionAsync(Secret)));
    async void UpdateCredentials(object sender, RoutedEventArgs e) => await actions.Invoke(async () => Saved(await Model.UpdateCredentialsAsync(Secret)));
    async void Connect(object sender, RoutedEventArgs e) => await actions.Invoke(actions.Connect);
    async void Refresh(object sender, RoutedEventArgs e) => await actions.Invoke(actions.Refresh);
    async void LoadParameters(object sender, RoutedEventArgs e) => await actions.Invoke(actions.LoadParameters);
    async void BackupClear(object sender, RoutedEventArgs e) => await actions.Invoke(actions.BackupClear);

    // A completed save clears the secret field and reports; a refused one leaves both as they are.
    void Saved(string? status)
    {
        if (status is null)
            return;
        if (secret is not null)
            secret.Password = "";
        actions.Status(status);
    }
}
