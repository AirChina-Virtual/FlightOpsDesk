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
        // Without a selection the bar shows the everyday commands; a selection swaps in its count and its commands.
        Assert.Empty(app.Find("DataDelete"));
        app.One("DataRefresh");
        app.Rows()[0].Select();
        app.Until(() => app.Find("DataDelete").Length > 0 && app.Name("DataSelectionCount").Contains('1', StringComparison.Ordinal), "a selection shows its commands and count");
        app.First("删除所选", ControlType.Button);
        Assert.Empty(app.Find("DataRefresh"));
        app.Screenshot("data-selected.png");
        app.Rows()[0].Unselect();
        app.Until(() => app.Find("DataDelete").Length == 0 && app.Find("DataRefresh").Length > 0, "the everyday commands come back");

        // Undo and redo from the keyboard change the data, and F5 queries again.
        var original = app.Value("Callsign");
        app.Edit("Callsign", "DEM999");
        app.Context = () => { var f = AutomationElement.FocusedElement; return $"focused id '{f?.Current.AutomationId}' name '{f?.Current.Name}' type {f?.Current.ControlType.ProgrammaticName} class {f?.Current.ClassName}"; };
        app.Foreground();
        // The keys go to the table, not to a cell editor that is still open (its own Ctrl+Z undoes its typing).
        app.Rows()[0].SetFocus();
        app.Until(() => AutomationElement.FocusedElement?.Current.ClassName != "TextBox", "the focus is on the table");
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
