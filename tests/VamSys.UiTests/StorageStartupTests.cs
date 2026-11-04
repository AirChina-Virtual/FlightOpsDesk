using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>Starting the product package on stored data: upgrade, a second instance, a newer format. Replaces scripts/test-storage-ui.ps1.</summary>
public class StorageStartupTests
{
    [WindowFact]
    public void An_old_database_upgrades_with_a_backup_and_a_second_instance_is_refused_in_both_languages()
    {
        using var primary = Start("storage-legacy", d => DataUiFixture.SeedStorage(d, future: false), Package.Product);
        primary.WaitText("Task center");
        primary.WaitText("Connection & settings");
        Assert.Single(Directory.GetFiles(primary.DataDirectory, "workspaces-before-v4-*.db"));

        using var duplicate = primary.Reopen();
        duplicate.WaitText("该数据目录已由另一个程序实例使用");
        duplicate.WaitText("Another application instance is using this data directory.");
        duplicate.Close();
        // The first instance stays usable.
        primary.WaitText("Task center");

        primary.Close();
    }

    [WindowFact]
    public void A_database_from_a_newer_version_is_refused_at_startup_in_both_languages()
    {
        using var future = Start("storage-future", d => DataUiFixture.SeedStorage(d, future: true), Package.Product);
        future.WaitText("数据库由更新版本创建");
        future.WaitText("This database requires a newer application version.");

        future.Close();
    }
}
