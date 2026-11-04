using System.Windows.Automation;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// The unknown-result review in the product package, offline and without credentials: correcting a legacy candidate ID,
/// reopening the dialog, keeping it across a restart, and the pending-identity details in both languages. Replaces
/// scripts/test-recovery-ui.ps1.
/// </summary>
public class RecoveryTests
{
    static string ShownRemoteId(AppWindow app) =>
        app.Wait(() => app.FindNamed("Remote ID", ControlType.Edit).FirstOrDefault(), "the Remote ID editor").Text();

    // RuntimeId can be reused after a local page refresh, so this observes the completed save and the closed editor; the
    // assertions after it reopen the dialog and restart the program.
    static void SaveCandidate(AppWindow app)
    {
        app.Click("保存候选 ID");
        app.Until(() => app.FindNamed("核对未知结果", ControlType.Button).FirstOrDefault() is { Current.IsEnabled: true }
            && !app.Shown("Remote ID", ControlType.Edit)
            && app.Shown("候选 ID 已保存。连接后恢复任务，仅回读核对。", ControlType.Text), "the candidate is saved and the editor closed");
    }

    static void ChineseDetailsShowThePendingIdentities(AppWindow app)
    {
        app.SelectItem("任务中心");
        app.Click("详情");
        app.Until(() => app.HasText("存在尚未核对成功的机场创建"), "the pending airport creation");
        app.Until(() => app.HasText("判重身份涉及尚未核对的写入"), "the pending identity");
        app.Click("关闭");
    }

    [WindowFact]
    public void A_candidate_ID_is_corrected_reopened_kept_across_a_restart_and_shown_in_both_languages()
    {
        using var first = Start("recovery", DataUiFixture.SeedRecovery, Package.Product);
        ChineseDetailsShowThePendingIdentities(first);
        first.Click("核对未知结果");
        Assert.Equal("102", ShownRemoteId(first));
        first.Edit("Remote ID", "103");
        SaveCandidate(first);
        first.Click("核对未知结果");
        Assert.Equal("103", ShownRemoteId(first));
        first.Edit("Remote ID", "101");
        SaveCandidate(first);
        first.Close();

        using var app = first.Reopen();
        ChineseDetailsShowThePendingIdentities(app);
        app.Click("核对未知结果");
        Assert.Equal("101", ShownRemoteId(app));
        app.Click("取消");

        app.SelectItem("连接与设置");
        app.Language("English");
        app.SelectItem("Task center");
        app.Click("Details");
        app.Until(() => app.HasText("An airport creation is still unverified."), "the pending airport creation in English");
        app.Until(() => app.HasText("This identity may be affected by an unverified write."), "the pending identity in English");
        app.Click("Close");
        app.Click("Review unknown results");
        Assert.Equal("101", ShownRemoteId(app));
        app.First("Save candidate ID", ControlType.Button);
        app.Click("Cancel");

        app.Close();
    }
}
