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
                ((NavigationViewItem)Navigation.MenuItems[item.PaneIndex()]).Content = L(item.TitleKey());
            ResourcePicker.ItemsSource = Enum.GetValues<ResourceKind>().Select(k => L(Schemas.All[k].Name)).ToList();
            ResourcePicker.SelectedIndex = (int)resource;
            AutomationProperties.SetName(ResourcePicker, L("ResourceType"));
            AutomationProperties.SetName(WorkspacePicker, L("CurrentWorkspace"));
            RefreshShell();
            FlushTasks();
            foreach (var task in taskModels)
                task.RefreshLanguage();
            StatusLabel.Text = statusText();
            foreach (var row in visible)
            foreach (var cell in row.Cells)
                cell.RefreshLanguage();
            if (page == "review")
            foreach (var row in reviewRows)
                row.RefreshLanguage();
            RefreshDataAvailability();
        }
        finally { ready = previous; }
    }
    void RefreshShell()
    {
        ModeBadge.Text = L(ShellViewModel.ModeBadgeKey(workspace.Mode));
        SnapshotLabel.Text = L("Text_B5B62EA34A", ("arg0", Data.Draft.Count), ("arg1", Data.SnapshotAt?.ToLocalTime().ToString("g", localization.DisplayCulture) ?? L("Text_C4CF69F358")));
        PageTitle.Text = L(shell.PageTitleKey);
    }
    ComboBox LanguagePicker()
    {
        var picker = new ComboBox { ItemsSource = new[] { "简体中文", "English" }, SelectedIndex = localization.Language == "en-US" ? 1 : 0, MinWidth = UiTokens.LanguagePickerMinWidth };
        AutomationProperties.SetAutomationId(picker, "LanguagePicker");
        picker.Localize(localization, "Header", () => L("Language"));
        picker.Localize(localization, "SelectedIndex", () => localization.Language == "en-US" ? 1 : 0);
        picker.SelectionChanged += async (_, _) => await Guard(() =>
        {
            var language = picker.SelectedIndex == 1 ? "en-US" : "zh-CN";
            if (language == localization.Language)
                return Task.CompletedTask;
            try
            { settings.SaveLanguage(language); }
            catch { picker.SelectedIndex = localization.Language == "en-US" ? 1 : 0; throw; }
            localization.SetLanguage(language);
            return Task.CompletedTask;
        });
        return picker;
    }
}
