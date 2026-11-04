using System.Windows.Automation;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// Switching language in place in the product package on a new data directory: unsaved settings, the draft, the filter and
/// the selection all survive, and the dialogs and review that follow are in the new language. Replaces
/// scripts/test-localization-ui.ps1.
/// </summary>
public class LocalizationTests
{
    [WindowFact]
    public void Language_switches_in_place_and_keeps_settings_draft_filter_selection_and_review()
    {
        using var app = Start("localization", package: Package.Product);
        AutomationElement[] Rows() => app.ListItemsNamed("RowViewModel");

        // Unsaved settings survive both ways, in the same editor.
        app.SelectItem("连接与设置");
        app.Edit("Client ID", "unsaved-language-test");
        var runtimeId = app.First("Client ID", ControlType.Edit).GetRuntimeId();
        app.Language("English");
        app.First("Save connection settings", ControlType.Button);
        var after = app.First("Client ID", ControlType.Edit);
        Assert.Equal("unsaved-language-test", after.Text());
        Assert.Equal(runtimeId, after.GetRuntimeId());
        app.Language("简体中文");
        app.First("保存连接设置", ControlType.Button);
        Assert.Equal("unsaved-language-test", app.Value("Client ID"));
        app.Language("English");
        app.First("Save connection settings", ControlType.Button);

        // The draft, the filter and the selection survive.
        app.SelectItem("Workspaces");
        app.Click("Create demo workspace");
        app.First("Demo · No live connection", ControlType.Text);
        app.SelectItem("Data management");
        app.Edit("Callsign", "DEM999");
        app.Edit("Search all fields", "DEM999");
        app.Click("Filter / sort");
        app.Until(() => Rows().Length == 1, "the filter leaves one row");
        Rows()[0].Select();
        app.SelectItem("Connection & settings");
        app.First("Interface language (applies immediately)", ControlType.ComboBox);
        app.Language("简体中文");
        app.First("保存连接设置", ControlType.Button);
        app.SelectItem("数据管理");
        Assert.Equal("DEM999", app.Value("搜索全部字段"));
        Assert.Equal("DEM999", app.Value("Callsign"));
        Assert.True(Rows()[0].IsSelected(), "Row selection lost");

        // Fleet dialog, delete and restore in the new language.
        app.SelectItem("连接与设置");
        app.First("界面语言（立即生效）", ControlType.ComboBox);
        app.Language("English");
        app.First("Save connection settings", ControlType.Button);
        app.SelectItem("Data management");
        app.Click("Choose fleet");
        app.First("Select available fleets (multiple)", ControlType.Text);
        app.First("Airbus A320 · A320 · 2", ControlType.CheckBox).Toggle();
        app.Click("Preview changes");
        app.Click("Apply to drafts");
        app.First("Assign fleets", ControlType.Button);
        app.Click("Delete selected");
        app.Click("Confirm deletion");
        app.First("Pending deletion", ControlType.Text);
        Assert.True(app.First("ID", ControlType.Edit).IsReadOnly(), "Deleted row is editable");
        // Restoring shows a preview of the rows it brings back; the change applies only after Confirm.
        app.Click("Restore deleted");
        app.Click("Confirm");
        app.First("Unsubmitted draft", ControlType.Text);

        // The review covers the whole workspace: each changed resource has its own CSV export, and a demo workspace offers
        // only the simulated submission, never the live API one.
        app.SelectItem("Change review");
        app.First("Export Routes CSV", ControlType.Button);
        Assert.True(app.Shown("Simulate workspace submission", ControlType.Button), "Demo submission missing from the review");
        Assert.False(app.Shown("Submit workspace changes", ControlType.Button), "Live API submission offered for a demo workspace");

        app.Close();
    }
}
