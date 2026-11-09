using System.Windows.Automation;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// Changing the data location through the real settings page, folder picker and restart. Runs in the QA package, whose
/// default directory can be redirected, so the real %LOCALAPPDATA% is never touched.
/// </summary>
public class DataLocationTests
{
    [WindowFact]
    public void The_data_moves_the_program_restarts_on_it_and_the_old_database_is_set_aside()
    {
        using var first = StartWithDefaultDirectory("data-location", DataUiFixture.SeedRecovery, out var defaults, out var target);
        first.SelectItem("连接与设置");
        first.WaitData(defaults);
        first.ClickUntilFilePicker("更改位置…");
        first.ChooseFolder(target);
        first.DialogPrimary("复制并重启").Invoke();

        using var second = first.FollowRestart(target);
        second.SelectItem("连接与设置");
        second.WaitData(target);
        Assert.True(File.Exists(Path.Combine(target, "workspaces.db")));
        second.Until(() => !File.Exists(Path.Combine(defaults, "workspaces.db")), "the old database is set aside");
        Assert.Single(Directory.GetFiles(defaults, "workspaces-moved-*.db"));
        second.Screenshot("settings-after-move.png");

        // And back: the set-aside database is not reopened, the current data is copied.
        second.Click("恢复默认位置");
        second.DialogPrimary("复制并重启").Invoke();
        using var third = second.FollowRestart(defaults);
        third.SelectItem("连接与设置");
        third.WaitData(defaults);
        third.Until(() => !File.Exists(Path.Combine(target, "workspaces.db")), "the second location is set aside");
        third.Close();
    }

    [WindowFact]
    public void A_missing_location_offers_a_retry_and_the_default_instead_of_an_empty_database()
    {
        using var first = StartWithDefaultDirectory("data-location-missing", DataUiFixture.SeedRecovery, out var defaults, out var target);
        first.SelectItem("连接与设置");
        first.ClickUntilFilePicker("更改位置…");
        first.ChooseFolder(target);
        first.DialogPrimary("复制并重启").Invoke();
        using var second = first.FollowRestart(target);
        second.WaitText("工作区");
        second.Close();

        var hidden = target + "-unplugged";
        Directory.Move(target, hidden);
        using var third = second.Reopen();
        third.WaitText("找不到数据位置");
        Assert.False(Directory.Exists(target), "No empty database may appear at the missing location");
        third.Screenshot("location-missing.png");
        Directory.Move(hidden, target);
        // Both languages in one label: composed at run time, so not an inventory string.
        var retry = "重试 / Retry";
        third.Click(retry);
        third.WaitText("工作区");
    }

    [WindowFact]
    public void A_location_set_by_the_environment_variable_cannot_be_changed_here()
    {
        using var app = Start("data-location-managed");
        app.SelectItem("连接与设置");
        app.WaitData(app.DataDirectory);
        Assert.False(app.Button("更改位置…").Current.IsEnabled);
        Assert.False(app.Button("恢复默认位置").Current.IsEnabled);
        app.Close();
    }
}
