using System.Windows.Automation;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// Saving connection settings and credentials in the product package; only made-up credentials, and connect is never
/// invoked. Replaces scripts/test-credentials-ui.ps1 and scripts/test-settings-save-ui.ps1.
/// </summary>
public class ConnectionSettingsTests
{
    [WindowFact]
    public void Credentials_alone_update_with_an_unresolved_task_and_an_unsaved_time_setting()
    {
        using var app = Start("credentials", DataUiFixture.SeedRecovery, Package.Product);

        app.SelectItem("连接与设置");
        app.Edit("Client ID", "rotated-ui-client");
        app.Edit("Client Secret（留空保留已有凭据）", "ui-test-secret-not-real");
        app.First("航线使用机场本地时间（离线保持原值，不推测机场时区）", ControlType.CheckBox).Toggle();
        app.Click("仅更新 API 凭据");
        app.First("凭据已更新，请重新连接并核验同一 VA 后恢复任务。", ControlType.Text);

        app.SelectItem("任务中心");
        app.First("核对未知结果", ControlType.Button);
        app.SelectItem("连接与设置");
        app.Language("English");
        app.First("Update API credentials only", ControlType.Button);
        Assert.Equal("rotated-ui-client", app.Value("Client ID"));

        app.Close();
    }

    [WindowFact]
    public void Ordinary_settings_normalize_a_padded_ID_and_a_blank_secret_resaves_in_both_languages()
    {
        using var app = Start("settings-save", package: Package.Product);
        void SavesTrimmed(string button)
        {
            app.Edit("Client ID", " 123 ");
            app.Click(button);
            app.Until(() => app.Value("Client ID") == "123", "the saved Client ID is trimmed");
        }

        app.SelectItem("连接与设置");
        app.Edit("Client Secret（留空保留已有凭据）", " secret ");
        SavesTrimmed("保存连接设置");
        SavesTrimmed("保存连接设置");
        app.Language("English");
        SavesTrimmed("Save connection settings");

        app.Close();
    }
}
