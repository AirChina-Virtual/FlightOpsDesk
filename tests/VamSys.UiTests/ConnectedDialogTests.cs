using System.Windows.Automation;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;
using Scenario = VamSys.TestSupport.ConnectedUiFixture.Scenario;

namespace VamSys.UiTests;

/// <summary>
/// 4.1: the dialogs that only appear while connected, in the QA package pointed at a mock Operations server on this
/// machine (<see cref="LoopbackOperationsServer"/>). Each test seeds one task by running the real executor against the
/// fake server (<see cref="ConnectedUiFixture"/>), connects in the settings, opens the dialog, answers it, and checks
/// what was saved. Nothing reaches a real server; the mock server records every request.
/// </summary>
public class ConnectedDialogTests
{
    const string Review = "核对未知结果", Close = "关闭";

    static AppWindow Connected(string name, Scenario scenario, out LoopbackOperationsServer server, out FakeRemote remote)
    {
        FakeRemote? seeded = null;
        var app = StartConnected(name, directory => (seeded = ConnectedUiFixture.Seed(directory, scenario)).Server, out server);
        remote = seeded!;
        app.Context = () => app.Name("StatusLabel");
        app.SelectItem("连接与设置");
        app.Click("测试 Operations 连接");
        app.StatusStarting("Operations 连接成功");
        return app;
    }

    static Workspace Stored(string directory)
    {
        var store = new WorkspaceStore(directory);
        return store.Load(store.List().Single(w => w.Name == "QA connected").Id);
    }

    static ChangeItem StoredItem(AppWindow app) => Stored(app.DataDirectory).Jobs.Single().Items.Single();

    static void NoWrites(FakeRemote remote, int before)
        => Assert.All(remote.Server.Requests.Skip(before), r => Assert.Equal("GET", r.Method));

    static AutomationElement OpenReview(AppWindow app, string primary)
    {
        app.SelectItem("任务中心");
        return app.ClickUntilDialog(Review, primary);
    }

    [WindowFact]
    public void Online_validation_names_the_unapproved_fleet_and_the_review_removes_it_in_one_undoable_edit()
    {
        using var app = Connected("connected-validation", Scenario.UnapprovedFleet, out var server, out var remote);
        using var owner = server;
        app.SelectItem("数据管理");
        app.SelectResource("航线");
        app.ClickUntilDialog("校验", Close);
        app.WaitText("逐行校验结果");
        app.WaitData("Fleet 6 spare");
        app.DialogPrimary(Close).Invoke();
        app.DialogClosed();

        app.SelectItem("变更审阅");
        var remove = "移除未获准的 1 个机队"; // composed from the count at run time
        app.Click(remove);
        app.StatusStarting("已从航线移除 1 个未获准的机队");
        var writes = remote.Server.Requests.Count(r => r.Method != "GET");
        app.Close();

        Assert.Equal(0, writes);
        var routes = Stored(app.DataDirectory).Resources[ResourceKind.Routes];
        Assert.Equal("4", routes.Draft.Single(r => r.Identity?.RemoteId == "10").Get("Fleet IDs"));
        Assert.Single(routes.Undo);
    }

    [WindowFact]
    public void A_delete_answered_204_but_still_readable_is_confirmed_by_hand()
    {
        using var app = Connected("connected-manual-delete", Scenario.DeleteAnsweredButReadable, out var server, out var remote);
        using var owner = server;
        var before = remote.Server.Requests.Count;
        OpenReview(app, "已在 Orwell 核对");
        app.WaitData("103");
        app.DialogPrimary("已在 Orwell 核对").Invoke();
        app.DialogClosed();
        NothingUnknown(app);
        app.Close();

        NoWrites(remote, before);
        Assert.Equal("ApiManualResult", StoredItem(app).Description?.Code);
    }

    // The task card offers the review only while something is unknown; the database is read after the window closes.
    static void NothingUnknown(AppWindow app)
        => app.Until(() => !app.Shown(Review, ControlType.Button), "no unknown result is left");

    [WindowFact]
    public void A_delete_without_receipt_whose_record_is_gone_is_confirmed_as_such()
    {
        using var app = Connected("connected-no-receipt-gone", Scenario.NoReceiptGone, out var server, out var remote);
        using var owner = server;
        var before = remote.Server.Requests.Count;
        OpenReview(app, "已在 Orwell 核对");
        app.WaitText("没有写入回执；只读 GET 返回 404");
        app.DialogPrimary("已在 Orwell 核对").Invoke();
        app.DialogClosed();
        app.StatusStarting("人工确认删除（无写入回执）");
        app.Close();

        NoWrites(remote, before);
        var item = StoredItem(app);
        Assert.Equal(ItemState.ManuallyConfirmed, item.State);
        Assert.Equal("SoftDeleteWithoutReceipt", item.ManualResolution!.Source);
    }

    [WindowFact]
    public void A_delete_without_receipt_whose_record_reads_as_before_can_be_marked_not_applied()
    {
        using var app = Connected("connected-no-receipt-unchanged", Scenario.NoReceiptUnchanged, out var server, out var remote);
        using var owner = server;
        var before = remote.Server.Requests.Count;
        OpenReview(app, "标为未生效");
        app.WaitText("与修改前的数据一致");
        app.DialogPrimary("标为未生效").Invoke();
        app.DialogClosed();
        app.StatusStarting("删除没有写入回执，只读 GET 读到的记录与修改前一致");
        app.Close();

        NoWrites(remote, before);
        Assert.Equal(ItemState.Failed, StoredItem(app).State);
    }

    [WindowFact]
    public void A_delete_without_receipt_whose_record_was_changed_elsewhere_is_only_shown()
    {
        using var app = Connected("connected-no-receipt-readable", Scenario.NoReceiptStillReadable, out var server, out var remote);
        using var owner = server;
        var before = remote.Server.Requests.Count;
        OpenReview(app, Close);
        app.WaitText("仍能读到这条记录，但与修改前的数据不一致");
        app.WaitData("Manchester (edited elsewhere)");
        app.DialogPrimary(Close).Invoke();
        app.DialogClosed();
        app.Close();

        NoWrites(remote, before);
        Assert.Equal(ItemState.Unknown, StoredItem(app).State);
    }

    [WindowFact]
    public void An_unanswered_update_is_verified_not_applied_by_reading_only()
    {
        using var app = Connected("connected-not-applied", Scenario.UpdateNotApplied, out var server, out var remote);
        using var owner = server;
        var before = remote.Server.Requests.Count;
        OpenReview(app, "API 核对未生效");
        app.WaitText("结果尚未确认");
        app.DialogPrimary("API 核对未生效").Invoke();
        app.DialogClosed();
        app.StatusStarting("API 只读核对确认这次写入未生效");
        app.Close();

        NoWrites(remote, before);
        Assert.Contains(remote.Server.Requests.Skip(before), r => r.Path.EndsWith("/fleet/4", StringComparison.Ordinal));
        Assert.Equal(ItemState.Failed, StoredItem(app).State);
    }

    [WindowFact]
    public void A_value_the_server_kept_differently_can_be_accepted_without_writing()
    {
        using var app = Connected("connected-accept", Scenario.ServerKeepsOtherValue, out var server, out var remote);
        using var owner = server;
        var before = remote.Server.Requests.Count;
        OpenReview(app, "接受服务器当前值");
        app.WaitData("FLEET 4 RENAMED (server)");
        app.DialogPrimary("接受服务器当前值").Invoke();
        app.DialogClosed();
        app.StatusStarting("人工接受服务器当前值");
        app.Close();

        NoWrites(remote, before);
        var workspace = Stored(app.DataDirectory);
        Assert.Equal(ItemState.ManuallyConfirmed, workspace.Jobs.Single().Items.Single().State);
        Assert.Equal("FLEET 4 RENAMED (server)", workspace.Resources[ResourceKind.Fleets].Snapshot.Single(r => r.Identity?.RemoteId == "4").Get("Name"));
    }

    [WindowFact]
    public void A_value_that_matches_by_now_cannot_be_accepted_and_is_left_for_resume()
    {
        using var app = Connected("connected-cannot-accept", Scenario.ServerValueNowMatches, out var server, out var remote);
        using var owner = server;
        var before = remote.Server.Requests.Count;
        OpenReview(app, Close);
        app.WaitText("服务器当前值已与发送值一致");
        app.DialogPrimary(Close).Invoke();
        app.DialogClosed();
        app.Close();

        NoWrites(remote, before);
        Assert.Equal(ItemState.Unknown, StoredItem(app).State);
    }

    // E1: the program found two records the unanswered create could have made; the dialog lists both and fills in the first.
    [WindowFact]
    public void Two_records_a_lost_create_could_have_made_are_listed_and_the_chosen_one_is_bound_by_reading()
    {
        using var app = Connected("connected-create-candidates", Scenario.CreateWithTwoCandidates, out var server, out var remote);
        using var owner = server;
        var before = remote.Server.Requests.Count;
        OpenReview(app, "回读核对");
        app.WaitData("90, 91");
        Assert.Equal("90", app.Value("Remote ID"));
        app.DialogPrimary("回读核对").Invoke();
        app.DialogClosed();
        NothingUnknown(app);
        app.Close();

        NoWrites(remote, before);
        Assert.Equal("90", StoredItem(app).RemoteId);
    }

    // 2.4: a route the server refused for unapproved fleets; the task center removes them and opens the review.
    [WindowFact]
    public void A_route_refused_for_unapproved_fleets_is_corrected_from_the_task_center()
    {
        using var app = Connected("connected-refused-route", Scenario.RouteRefusedForFleets, out var server, out var remote);
        using var owner = server;
        var before = remote.Server.Requests.Count;
        app.SelectItem("任务中心");
        var remove = "移除未获准的机队（1 条航线）并重新审阅"; // composed from the count at run time
        app.Click(remove);
        app.StatusStarting("已从航线移除 2 个未获准的机队");
        app.WaitText("本次提交");
        app.Close();

        NoWrites(remote, before);
        var route = Stored(app.DataDirectory).Resources[ResourceKind.Routes].Draft.Single(r => r.Get("Callsign") == "CAO088");
        Assert.Equal("4", route.Get("Fleet IDs"));
    }

    // 1.6: two deletes in the same situation are confirmed with one answer.
    [WindowFact]
    public void Two_deletes_without_receipt_whose_records_are_gone_are_confirmed_together()
    {
        using var app = Connected("connected-group", Scenario.TwoDeletesWithoutReceiptGone, out var server, out var remote);
        using var owner = server;
        var before = remote.Server.Requests.Count;
        var all = "全部确认（2 项）"; // composed from the count at run time
        OpenReview(app, all);
        app.WaitText("逐条处理");
        app.WaitData("EGCC");
        app.WaitData("EGPH");
        app.DialogPrimary(all).Invoke();
        app.DialogClosed();
        app.StatusStarting("已批量处理 2／2 项");
        NothingUnknown(app);
        app.Close();

        NoWrites(remote, before);
        Assert.All(Stored(app.DataDirectory).Jobs.Single().Items, i => Assert.Equal("SoftDeleteWithoutReceipt", i.ManualResolution!.Source));
    }
}
