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
        localization.SetLanguage(store.LoadLanguage());
        localization.MissingResource += key =>
        {
            try
            {
                var directory = Environment.GetEnvironmentVariable("VAMSYS_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VamSysBatch");
                File.AppendAllText(Path.Combine(directory, "localization.log"), $"{DateTimeOffset.UtcNow:O} {localization.Language} {key}\n");
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
        localization.LanguageChanged += (_, _) => RefreshLanguage();
        Status(() => L("OfflineReady"));
        RefreshLanguage();
    }
    void RefreshLanguage()
    {
        bool previous = ready; ready = false;
        try
        {
            RootGrid.Language = localization.Language;
            Title = L("Text_E531BA46BD");
            AppTitleBar.Subtitle = RootGrid.ActualWidth is > 0 and < 1100 ? "" : L("Text_9750099F9F");
            string[] keys = ["Text_3797982942", "Text_9BB882C622", "Text_D52C17622D", "Text_10CCC8AAA2", "Text_00B514C36A", "Text_C2B958136D"];
            for (int i = 0; i < keys.Length; i++) ((NavigationViewItem)Navigation.MenuItems[i]).Content = L(keys[i]);
            ResourcePicker.ItemsSource = Enum.GetValues<ResourceKind>().Select(k => L(Schemas.All[k].Name)).ToList();
            ResourcePicker.SelectedIndex = (int)resource;
            AutomationProperties.SetName(ResourcePicker, L("ResourceType"));
            AutomationProperties.SetName(WorkspacePicker, L("CurrentWorkspace"));
            RefreshShell();FlushTasks();foreach(var task in taskModels)task.RefreshLanguage();
            StatusLabel.Text = statusText();
            foreach (var row in visible) foreach (var cell in row.Cells) cell.RefreshLanguage();
        }
        finally { ready = previous; }
    }
    void RefreshShell()
    {
        ModeBadge.Text = workspace.Mode switch { RunMode.Demo => L("Text_692F811146"), RunMode.Online => L("Text_C4C1F8C6F4"), _ => L("Text_6C352EE0B0") };
        SnapshotLabel.Text = L("Text_B5B62EA34A", ("arg0", Data.Draft.Count), ("arg1", Data.SnapshotAt?.ToLocalTime().ToString("g", localization.DisplayCulture) ?? L("Text_C4CF69F358")));
        PageTitle.Text = L(page switch { "data" => "Text_9BB882C622", "import" => "Text_D52C17622D", "review" => "Text_10CCC8AAA2", "tasks" => "Text_00B514C36A", "settings" => "Text_C2B958136D", _ => "Text_3797982942" });
    }
    ComboBox LanguagePicker()
    {
        var picker = new ComboBox { ItemsSource = new[] { "简体中文", "English" }, SelectedIndex = localization.Language == "en-US" ? 1 : 0, MinWidth = 220 };
        AutomationProperties.SetAutomationId(picker, "LanguagePicker");
        picker.Localize(localization, "Header", () => L("Language"));
        picker.Localize(localization, "SelectedIndex", () => localization.Language == "en-US" ? 1 : 0);
        picker.SelectionChanged += async (_, _) => await Guard(() =>
        {
            var language = picker.SelectedIndex == 1 ? "en-US" : "zh-CN";
            if (language == localization.Language) return Task.CompletedTask;
            try { store.SaveLanguage(language); }
            catch { picker.SelectedIndex = localization.Language == "en-US" ? 1 : 0; throw; }
            localization.SetLanguage(language); return Task.CompletedTask;
        });
        return picker;
    }
}
