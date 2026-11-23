using System.Windows.Automation;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.ConnectedDialogTests;
using static VamSys.UiTests.Driver.AppWindow;
using Scenario = VamSys.TestSupport.ConnectedUiFixture.Scenario;

namespace VamSys.UiTests;

/// <summary>
/// 2.5: the fleet cells' hints under a moving pointer, in the QA package connected to the mock Operations server as in
/// <see cref="ConnectedDialogTests"/>. A class of its own because it is about pointer input and the window staying up rather
/// than a connected dialog. It was first split off because the fleet-hint test there seemed to depend on the order of tests;
/// that failure came from the test itself and is fixed (docs/TEST-REPORT.md, 2026-11-23), and both pass in either class.
/// </summary>
public class FleetHintSweepTests
{
    // 2.5: the pointer sweeping back and forth over the fleet buttons (hovering a hint-bearing one long enough for its tooltip
    // to open, passing others quickly) leaves the window running; making the tooltip on hover once crashed it here. A
    // language switch then changes the text of the cell's own tooltip (FleetCell.Hint keeps one tooltip per button).
    [WindowFact]
    public void Sweeping_over_fleet_buttons_keeps_the_window_and_a_language_switch_updates_the_hint()
    {
        using var app = Connected("connected-fleet-sweep", Scenario.UnapprovedFleet, out var server, out var remote);
        using var owner = server;
        const string Chinese = "尚未获准", English = "not approved", Choose = "选择机型", ChooseEnglish = "Choose fleet";
        var before = remote.Server.Requests.Count;
        app.SelectItem("数据管理");
        app.SelectResource("航线");
        app.Until(() => app.Rows().Length > 0, "the routes are shown");
        AutomationElement[] FleetButtons(string name) => app.Rows()
            .Select(r => Below(r, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, name),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))).FirstOrDefault())
            .OfType<AutomationElement>().ToArray();
        int Hinted() => Enumerable.Range(0, app.Rows().Length).Count(i => app.Query("data-row:" + i)["hints"]?["Fleet IDs"] is not null);
        app.Until(() => Hinted() >= 2, "two routes with unapproved fleets");
        var buttons = app.Wait(() => FleetButtons(Choose) is { Length: >= 3 } found ? found : null, "three fleet buttons");
        app.Context = () => $"running {app.Running} | open tooltip '{app.OpenToolTip()}' | {app.LastClick}";

        app.Foreground();
        for (var pass = 0; pass < 4; pass++)
        {
            var order = pass % 2 == 0 ? buttons : buttons.Reverse().ToArray();
            for (var i = 0; i < order.Length; i++)
            {
                app.HoverOn(order[i], $"fleet button {i} (pass {pass})", away: false);
                Thread.Sleep((i + pass) % 2 == 0 ? 700 : 60);
            }
            Assert.True(app.Running, $"The window ended while the pointer swept over the fleet buttons (pass {pass}).");
        }

        // A tooltip shown for a fleet button with the hint's text, or null when none comes within the limit.
        string? Shown(string name, string marker)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < DefaultWait)
            {
                app.Foreground();
                foreach (var button in FleetButtons(name))
                {
                    app.HoverOn(button, "a fleet button", away: false);
                    if (Poll(() => app.OpenToolTip() is { } tip && tip.Contains(marker, StringComparison.Ordinal) ? tip : null, TimeSpan.FromSeconds(1.5)) is { } shown)
                        return shown;
                }
            }
            return null;
        }
        Assert.NotNull(Shown(Choose, Chinese));
        Assert.True(app.Running, "The window ended after the sweep.");

        app.SelectItem("连接与设置");
        app.Language("English");
        app.SelectItem("Data management");
        app.SelectResource("Routes");
        app.Until(() => app.Rows().Length > 0, "the routes are shown in English");
        var english = Shown(ChooseEnglish, English);
        Assert.True(english is not null && !english.Contains(Chinese, StringComparison.Ordinal),
            $"The fleet hint after the language switch is '{english}'; {app.Context!()}");
        Assert.True(app.Running, "The window ended after the language switch.");
        // Leave the program as the other tests expect it: in Chinese, the pointer off the table.
        app.SelectItem("Connection & settings");
        app.Language("简体中文");
        app.Foreground();
        app.Close();
        NoWrites(remote, before);
    }
}
