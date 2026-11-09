using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    readonly LocalizationService localization = new();
    Func<string> statusText = () => "";
    string L(string key, params (string Name, object? Value)[] args) => localization.Get(key, args);
    string L(MessageDescriptor message) => localization.Format(message);
    string JobText(BatchJob job) => L(job.Description ?? Messages.FromLegacy(job.Status));
    string ItemText(ChangeItem item) => L(item.Description ?? Messages.FromLegacy(item.Message));
    string ItemText(Issue issue) => L(issue.Description);
    string EnumText(Enum value) => L("Enum_" + value.GetType().Name + "_" + value);
    void InitializeLanguage()
    {
        localization.SetLanguage(settings.LoadLanguage());
        localization.MissingResource += key =>
        {
            try
            {
                var directory = AppPaths.DataDirectory;
                File.AppendAllText(Path.Combine(directory, "localization.log"), $"{DateTimeOffset.UtcNow:O} {localization.Language} {key}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        localization.LanguageChanged += (_, _) => RefreshLanguage();
        Status(() => L("OfflineReady"));
        RefreshLanguage();
    }
    void RefreshLanguage()
    {
        bool previous = ready;
        ready = false;
        try
        {
            RootGrid.Language = localization.Language;
            Title = AppTitleBar.Title = L("Text_E531BA46BD");
            AppTitleBar.Subtitle = RootGrid.ActualWidth is > 0 and < 1100 ? "" : L("Text_9750099F9F");
            foreach (var item in ShellPages.All)
                PaneItem(item).Content = L(item.TitleKey());
            RefreshResourceTabs();
            AutomationProperties.SetName(WorkspacePicker, L("CurrentWorkspace"));
            RefreshShell();
            Tasks.RefreshLanguage();
            StatusLabel.Text = statusText();
            foreach (var row in visible)
            foreach (var cell in row.Cells)
                cell.RefreshLanguage();
            foreach (var settingsPage in settingsPages.Values)
                settingsPage.Model.RefreshLanguage();
            if (ReferenceEquals(PageHost.Content, shownWorkspacePage))
                shownWorkspacePage?.Model.RefreshLanguage();
            if (ReferenceEquals(PageHost.Content, shownImportPage))
                shownImportPage?.Model.RefreshLanguage();
            if (ReferenceEquals(PageHost.Content, shownReviewPage))
                shownReviewPage?.Model.RefreshLanguage();
            RefreshDataAvailability();
        }
        finally { ready = previous; }
    }
    void RefreshShell()
    {
        ModeBadge.Text = L(ShellViewModel.ModeBadgeKey(workspace.Mode));
        // Writes reach a real server only in online mode, so the mode can be told apart at a glance.
        ModeBadgeHost.Style = workspace.Mode switch { RunMode.Online => UiTokens.ModeOnlineBadgeStyle, RunMode.Demo => UiTokens.ModeDemoBadgeStyle, _ => UiTokens.ModeOfflineBadgeStyle };
        RefreshReviewBadge();
        SnapshotLabel.Text = L("Text_B5B62EA34A", ("arg0", Data.Draft.Count), ("arg1", Data.SnapshotAt?.ToLocalTime().ToString("g", localization.DisplayCulture) ?? L("Text_C4CF69F358")));
        PageTitle.Text = L(shell.PageTitleKey);
    }
}
