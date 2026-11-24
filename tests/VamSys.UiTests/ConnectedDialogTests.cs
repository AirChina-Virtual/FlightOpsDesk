using System.Text.Json.Nodes;
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

    internal static AppWindow Connected(string name, Scenario scenario, out LoopbackOperationsServer server, out FakeRemote remote)
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

    internal static void NoWrites(FakeRemote remote, int before)
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

    // 2.5: connected, with the airline parameters read (they approve fleet 4 only), the Fleet IDs cell of a route names its
    // unapproved fleets on hover; once every fleet is approved the hint is gone. The edits go through the cell's own fleet
    // picker; the seeded draft is "4,6". Nothing is written to the server.
    [WindowFact]
    public void The_fleet_cell_names_an_unapproved_fleet_on_hover_until_every_fleet_is_approved()
    {
        using var app = Connected("connected-fleet-hint", Scenario.UnapprovedFleet, out var server, out var remote);
        using var owner = server;
        const string Choose = "选择机型", Apply = "应用", Spare = "Fleet 6 spare · A320 · ", Marker = "尚未获准";
        const string Expected = "这些机队尚未获准使用本航线呼号和班号的前缀，提交前会被拦下：Fleet 6 spare (6)";
        var before = remote.Server.Requests.Count;
        app.SelectItem("数据管理");
        app.SelectResource("航线");
        app.Until(() => app.Rows().Length > 0, "the routes are shown");
        // The route's row as the QA pipe reads it in one read, or null while the table does not show it (after each fleet choice
        // the page refills the table). The checks below wait through such a moment; reading "no row" as "no hint" would pass
        // a check too early. This test once took the row count from UI Automation, read that many QA rows one by one and
        // failed at once when none was the route: twice in full runs, right after a fleet choice. A route that stays away now
        // fails the check by timeout, with the number of reads that missed it in the state.
        var missed = 0;
        JsonNode? Route()
        {
            if (app.Query("data-row-find:Callsign=CAO100") is { } row && row.Bool("shown"))
                return row;
            missed++;
            return null;
        }
        bool RouteShows(Func<JsonNode, bool> holds) => Route() is { } route && holds(route);
        // The hint the cell's model holds (what the tooltip will show).
        static string? ModelHint(JsonNode route) => route["hints"]?["Fleet IDs"]?.ToString();
        // The fleets as a set: the picker decides their order.
        static string Fleets(JsonNode route) => string.Join(",", (route["fields"]!.Str("Fleet IDs") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        // The route's own row in the window, found by its callsign cell: the QA row numbers (data-row) are in the page's order,
        // which need not be the order UI Automation lists the rows in, and another route has unapproved fleets too.
        AutomationElement FleetButton() => app.Wait(() => app.Rows().Where(r => Below(r, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, "Callsign"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))).Any(c => c.Text() == "CAO100"))
            .Select(r => Below(r, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, Choose), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))).FirstOrDefault())
            .FirstOrDefault(b => b != null), "the fleet button of route CAO100");
        void ToggleSpare(string after)
        {
            FleetButton().Invoke();
            app.Wait(() => app.FindAll(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox))
                .FirstOrDefault(c => c.Current.Name.StartsWith(Spare, StringComparison.Ordinal)), "fleet 6 in the picker").Toggle();
            app.DialogPrimary(Apply).Invoke();
            app.DialogClosed();
            app.Until(() => RouteShows(r => Fleets(r) == after), $"the route's fleets become {after}");
        }
        // The hint the pointer brings up on the route's fleet cell, or null when none comes within the limit.
        // The window is brought to the front (injected pointer moves reach only the foreground window), then the pointer
        // moves onto the cell and rests there. Retried every 1.5 s, as a person would move the mouse again.
        string? Hint(TimeSpan limit)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < limit)
            {
                app.Foreground();
                // The Fleet IDs column is the route's eleventh, right of the window at first: the table is scrolled to it.
                app.HoverOn(app.ScrollIntoTableView(FleetButton, "the route's fleet cell"), "the route's fleet cell", away: false);
                if (Poll(() => app.OpenToolTip() is { } tip && tip.Contains(Marker, StringComparison.Ordinal) ? tip : null, TimeSpan.FromSeconds(1.5)) is { } shown)
                    return shown;
            }
            return null;
        }
        var quiet = TimeSpan.FromSeconds(3); // six times the hint's delay

        app.Context = () => (Route() is { } route ? $"route fleets {Fleets(route)} | model hint '{ModelHint(route)}'" : "route CAO100 not shown")
            + $" | reads without the route {missed} | open tooltip '{app.OpenToolTip()}' | {app.LastClick}";
        app.Until(() => RouteShows(r => Fleets(r) == "4,6"), "the seeded draft");
        app.Until(() => RouteShows(r => ModelHint(r) == Expected), "the seeded draft's cell names fleet 6");
        ToggleSpare("4");
        app.Until(() => RouteShows(r => ModelHint(r) is null), "no hint once every fleet is approved");
        Assert.Null(Hint(quiet));
        ToggleSpare("4,6");
        app.Until(() => RouteShows(r => ModelHint(r) == Expected), "the cell names fleet 6 again");
        var shown = Hint(DefaultWait);
        Assert.True(shown == Expected, $"The hovered fleet cell shows tooltip '{shown}' instead of the hint; {app.Context!()}");
        ToggleSpare("4");
        app.Until(() => RouteShows(r => ModelHint(r) is null), "no hint again");
        Assert.Null(Hint(quiet));
        app.Close();

        NoWrites(remote, before);
        Assert.Equal("4", Stored(app.DataDirectory).Resources[ResourceKind.Routes].Draft.Single(r => r.Identity?.RemoteId == "10").Get("Fleet IDs"));
    }

    // 5.2 E5: the data menu refreshes only the page's resource and what it refers to.
    [WindowFact]
    public void The_data_menu_refreshes_only_the_routes_with_their_airports_and_fleets()
    {
        using var app = Connected("connected-refresh-resource", Scenario.UnapprovedFleet, out var server, out var remote);
        using var owner = server;
        app.SelectItem("数据管理");
        app.SelectResource("航线");
        var before = remote.Server.Requests.Count;
        app.One("DataRefresh").Expand();
        var only = "只刷新航线"; // composed from the resource name at run time
        app.First(only, ControlType.MenuItem).Invoke();
        app.StatusStarting("API 数据已刷新");
        var requests = remote.Server.Requests.Skip(before).ToList();
        app.Close();

        Assert.All(requests, r => Assert.Equal("GET", r.Method));
        var paths = requests.Select(r => r.Path).ToList();
        Assert.Contains(paths, p => p.EndsWith("/airports", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("/fleet", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("/routes", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("/aircraft", StringComparison.Ordinal) || p.Contains("/routings", StringComparison.Ordinal));
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

    // The end-of-round lookup settles a create whose answer carried no ID, and the items skipped because of it run in that
    // same round. In the window: connecting, the review page, its confirmation and the task center (the submission runs
    // the program's own executor against the mock server). The drafts are seeded; the server misbehaves only on the
    // fleet's POST (ConnectedUiFixture.UnreadableFleetCreate). The headless counterpart is
    // ConnectedUiFixtureTests.Dependents_of_a_create_settled_at_the_end_of_the_round_run_in_that_round.
    [WindowFact]
    public void Dependents_of_a_create_settled_at_the_end_of_the_round_run_in_that_round()
    {
        using var app = Connected("connected-same-round", Scenario.DependentsOfUnreadableCreate, out var server, out var remote);
        using var owner = server;
        const string Submit = "提交工作区修改", Completed = "已完成并核对", Resume = "继续";
        var before = remote.Server.Requests.Count;
        // A failure says what the review holds back and why (each entry's disposition and reason, and any blocker).
        var status = app.Context;
        app.Context = () => $"{status!()} | review {app.Query("review-state", TimeSpan.FromSeconds(10)).ToJsonString()}";
        app.SelectItem("变更审阅");
        app.Wait(() => app.FindNamed(Submit, ControlType.Button).FirstOrDefault(b => b.Current.IsEnabled), "the review is ready").Invoke();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        app.DialogPrimary(Submit).Invoke();
        app.Until(() => app.First("任务中心", ControlType.ListItem).IsSelected(), "the task center opens");
        // While the task runs the card can show 继续 and 核对未知结果: items are pending, and the fleet is unknown until the
        // end-of-round lookup. They must be disabled then (the window is busy with the task: gate.CanAct is false; a click
        // would be refused). Once the task reads 已完成并核对 they must go within a few seconds (the card's update is
        // flushed on the presentation timer). Every change of what is shown is recorded with the time since the submission,
        // whether the button was enabled and the status text at that moment, so a failure says which of the two it was.
        // This samples about every 100 ms: it does not prove a button was never enabled between two samples.
        var samples = new List<(long Ms, string Label, bool Shown, bool Enabled, string Status)>();
        void Sample()
        {
            var text = app.Name("StatusLabel");
            foreach (var label in new[] { Resume, Review })
            {
                var button = app.FindNamed(label, ControlType.Button).FirstOrDefault();
                var (shown, enabled) = (button != null, button?.Current.IsEnabled ?? false);
                var last = samples.LastOrDefault(s => s.Label == label);
                // The first record of a label is when it first shows; after that, every change of shown, enabled or status.
                if (last.Label is null ? shown : (last.Shown, last.Enabled, last.Status) != (shown, enabled, text))
                    samples.Add((clock.ElapsedMilliseconds, label, shown, enabled, text));
            }
        }
        string Seen() => string.Join("; ", samples.Select(s => $"{s.Ms} ms {s.Label} {(s.Shown ? s.Enabled ? "enabled" : "disabled" : "gone")} (status '{s.Status}')"));
        bool Finished() => app.Name("StatusLabel").StartsWith(Completed, StringComparison.Ordinal);
        app.Until(() => { Sample(); return Finished(); }, "the task completes and is verified", TimeSpan.FromSeconds(60));
        var completedAt = clock.ElapsedMilliseconds;
        var cleared = Poll(() => { Sample(); return app.Shown(Resume, ControlType.Button) || app.Shown(Review, ControlType.Button) ? null : "cleared"; }, TimeSpan.FromSeconds(5));
        app.Close();

        Assert.True(cleared != null, $"The finished task still offers to resume or to check unknown results 5 s after '{Completed}' ({completedAt} ms): {Seen()}");
        Assert.False(samples.Any(s => s.Shown && s.Enabled && !s.Status.StartsWith(Completed, StringComparison.Ordinal)), $"A button was enabled while the task ran: {Seen()}");
        var writes = remote.Server.Requests.Skip(before).Where(r => r.Method != "GET").ToList();
        Assert.All(writes, r => Assert.Equal("POST", r.Method));
        Assert.Equal(3, writes.Count);
        Assert.EndsWith("/fleet", writes[0].Path, StringComparison.Ordinal);
        Assert.EndsWith("/fleet/90/aircraft", writes[1].Path, StringComparison.Ordinal);
        Assert.EndsWith("/routes", writes[2].Path, StringComparison.Ordinal);
        var job = Stored(app.DataDirectory).Jobs.Single();
        Assert.Equal(JobStatus.Completed, job.StatusCode);
        Assert.All(job.Items, i => Assert.Equal(ItemState.Succeeded, i.State));
        var fleet = job.Items.Single(i => i.Resource == ResourceKind.Fleets);
        var found = Assert.Single(fleet.RecoveryCandidates);
        Assert.Equal(("90", "Automatic"), (found.RemoteId, found.Source));
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
