using System.Windows.Automation;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>The interactions the redesign introduced, with a screenshot of each page for a person to look at.</summary>
public class RedesignTests
{
    [WindowFact]
    public void The_conditions_apply_by_themselves_the_selection_brings_its_commands_and_the_shortcuts_work()
    {
        using var app = Start("redesign");
        app.WaitText("工作区");
        app.Click("创建演示工作区");
        // A new demo workspace has nothing pending, so the overview sends it to the data.
        app.First("打开数据管理", ControlType.Button);
        app.Screenshot("workspace.png");

        app.SelectItem("数据管理");
        app.Until(() => app.Rows().Length > 1, "rows are shown");
        var all = app.Rows().Length;
        // The commands for selected records stay in one place and wait for a selection.
        Assert.False(app.Enabled("DataBatchMenu"));
        app.Rows()[0].Select();
        app.Until(() => app.Enabled("DataBatchMenu") && app.Name("DataBatchMenu").Contains('1', StringComparison.Ordinal), "a selection enables the menu and shows its count");
        app.One("DataBatchMenu").Expand();
        app.First("删除所选", ControlType.MenuItem);
        app.Screenshot("data-selected.png");
        app.One("DataBatchMenu").Collapse();
        app.Rows()[0].Unselect();
        app.Until(() => !app.Enabled("DataBatchMenu"), "the menu waits for a selection again");

        // Undo and redo from the keyboard change the data, and F5 queries again.
        var original = app.Value("Callsign");
        app.Edit("Callsign", "DEM999");
        app.Foreground();
        PressWithControl((byte)'Z');
        app.Until(() => app.Value("Callsign") == original, "Ctrl+Z undoes the edit");
        PressWithControl((byte)'Y');
        app.Until(() => app.Value("Callsign") == "DEM999", "Ctrl+Y redoes the edit");
        PressWithControl((byte)'Z');
        app.Until(() => app.Value("Callsign") == original, "Ctrl+Z undoes it again");
        const byte f5 = 0x74;
        Press(f5);
        app.StatusStarting("已加载 12 条结果");

        // Typing applies without a button.
        app.Edit("搜索全部字段", "no record has this text");
        app.WaitText("没有符合条件的记录");
        app.Screenshot("data-empty.png");
        app.Edit("搜索全部字段", "");
        app.Until(() => app.Rows().Length == all, "clearing the search brings the rows back");

        // Ctrl+F reaches the search box from anywhere on the page.
        app.Query("state");
        app.Foreground();
        PressWithControl((byte)'F');
        app.Until(() => app.One("DataSearch").Current.HasKeyboardFocus, "Ctrl+F focuses the search box");

        app.SelectItem("变更审阅");
        app.WaitText("审阅整个工作区");
        app.Screenshot("review.png");
        app.SelectItem("任务中心");
        app.WaitText("还没有任务");
        app.Screenshot("tasks-empty.png");
        app.SelectItem("连接与设置");
        app.WaitText("数据存放位置");
        app.Screenshot("settings.png");
        app.Close();
    }
}
