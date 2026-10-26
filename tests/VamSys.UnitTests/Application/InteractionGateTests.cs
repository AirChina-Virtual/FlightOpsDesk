using VamSys.Core;

namespace VamSys.UnitTests.Application;

public class InteractionGateTests
{
    // The expected values are the conditions the window used to write out by hand, one per query.
    public sealed record Combination(WindowCloseState Window, bool Busy, bool Local, bool Querying, bool Blocked, int Actions, int Dialogs)
    {
        public bool Open => Window == WindowCloseState.Open;
        public override string ToString() => $"{Window} busy={Busy} local={Local} query={Querying} blocked={Blocked} actions={Actions} dialogs={Dialogs}";
    }

    public static IEnumerable<object[]> Combinations()
    {
        foreach (var window in Enum.GetValues<WindowCloseState>())
        foreach (var busy in new[] { false, true })
        foreach (var local in new[] { false, true })
        foreach (var querying in new[] { false, true })
        foreach (var blocked in new[] { false, true })
        foreach (var actions in new[] { 0, 2 })
        foreach (var dialogs in new[] { 0, 1 })
            yield return [window, busy, local, querying, blocked, actions, dialogs];
    }

    // Enters every state through the public scopes, in an order each entry accepts, and keeps them alive.
    static async Task<(InteractionGate Gate, List<IDisposable> Scopes, TaskCompletionSource? Pending)> Enter(Combination c)
    {
        var gate = new InteractionGate();
        var scopes = new List<IDisposable>();
        if (c.Busy)
            scopes.Add(gate.TryBeginWork() ?? throw new InvalidOperationException("busy not entered"));
        for (var n = 0; n < c.Actions; n++)
            scopes.Add(gate.TryBeginUserAction() ?? throw new InvalidOperationException("action not entered"));
        for (var n = 0; n < c.Dialogs; n++)
            scopes.Add(gate.BeginDialog());
        if (c.Local)
            scopes.Add(gate.BeginReviewLock());
        gate.SetQuerying(c.Querying);
        if (c.Blocked)
            gate.BlockStorage();
        TaskCompletionSource? pending = null;
        if (c.Window != WindowCloseState.Open)
        {
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var closing = gate.Closing.RequestAsync(false, () => pending.Task, _ => { });
            if (c.Window == WindowCloseState.Closed)
            {
                pending.SetResult();
                Assert.True(await closing);
            }
        }
        Assert.Equal(c.Window, gate.Closing.State);
        return (gate, scopes, pending);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task Every_query_equals_the_condition_the_window_used_to_combine_by_hand(WindowCloseState window, bool busy, bool local, bool querying, bool blocked, int actions, int dialogs)
    {
        var c = new Combination(window, busy, local, querying, blocked, actions, dialogs);
        var (gate, scopes, _) = await Enter(c);
        Assert.Equal(c.Open, gate.IsOpen);
        Assert.Equal(c.Open && !c.Busy && !c.Local && !c.Querying && !c.Blocked, gate.CanEdit);
        Assert.Equal(c.Open && !c.Local, gate.CanNavigate);
        Assert.Equal(c.Open && !c.Busy && !c.Local, gate.CanSwitchContext);
        Assert.Equal(c.Open && !c.Busy && !c.Local, gate.CanStartWork);
        Assert.Equal(c.Open && !c.Busy && !c.Local, gate.CanStartQuery);
        Assert.Equal(!c.Querying && !c.Local, gate.CanInvokeCommand);
        Assert.Equal(c.Open && !c.Busy, gate.CanAct);
        Assert.Equal(c.Open && !c.Busy && !c.Local && !c.Querying, gate.CanRunTask);
        Assert.Equal(c.Open, gate.CanStartUserAction);
        Assert.Equal(c.Open, gate.ControlsEnabled);
        Assert.Equal(!c.Busy, gate.CanRequestOnline);
        Assert.Equal(!c.Busy && !c.Querying, gate.CanLoadOnShow);
        Assert.Equal(!c.Busy, gate.CanLoadOnBuild);
        Assert.Equal(!c.Busy && !c.Local, gate.CanResolveConflicts);
        Assert.Equal(c.Busy || c.Querying || c.Local, gate.ShowsProgress);
        Assert.Equal(c.Busy, gate.ShowsWorkProgress);
        Assert.Equal(c.Busy || c.Local || c.Actions > 0 || c.Dialogs > 0, gate.CloseBlockedByActivity);
        Assert.Equal(!c.Busy && !c.Local && !c.Querying && c.Actions == 0 && c.Dialogs == 0, gate.IsIdle);
        Assert.Equal(c.Busy, gate.WorkInProgress);
        Assert.Equal(c.Local, gate.LocalOperationActive);
        Assert.Equal(c.Querying, gate.QueryInProgress);
        Assert.Equal(c.Blocked, gate.StorageBlocked);
        Assert.Equal(c.Actions, gate.RunningUserActions);
        Assert.Equal(c.Dialogs, gate.OpenDialogs);
        Assert.Equal(c.Window == WindowCloseState.Closed, gate.HasClosed);
        foreach (var scope in scopes)
            scope.Dispose();
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task Entering_work_a_local_operation_or_a_user_action_follows_the_old_guards(WindowCloseState window, bool busy, bool local, bool querying, bool blocked, int actions, int dialogs)
    {
        var c = new Combination(window, busy, local, querying, blocked, actions, dialogs);
        var (gate, scopes, _) = await Enter(c);
        var canWork = c.Open && !c.Busy && !c.Local;
        if (canWork && c.Blocked)
            Assert.Equal("StorageReload", MessageErrors.Describe(Assert.Throws<InvalidOperationException>(() => gate.TryBeginWork())).Code);
        else
        {
            using var work = gate.TryBeginWork();
            Assert.Equal(canWork, work is not null);
            Assert.Equal(canWork || c.Busy, gate.WorkInProgress);
        }
        using (var scope = gate.TryBeginLocal())
            Assert.Equal(c.Open && !c.Busy && !c.Local && !c.Querying && !c.Blocked, scope is not null);
        using (var action = gate.TryBeginUserAction())
            Assert.Equal(c.Open, action is not null);
        foreach (var scope in scopes)
            scope.Dispose();
    }

    [Fact]
    public void Scopes_end_their_state_exactly_once()
    {
        var gate = new InteractionGate();
        var work = gate.TryBeginWork()!;
        Assert.True(gate.WorkInProgress);
        Assert.Null(gate.TryBeginWork());
        work.Dispose();
        work.Dispose();
        Assert.False(gate.WorkInProgress);
        var dialog = gate.BeginDialog();
        var again = gate.BeginDialog();
        dialog.Dispose();
        dialog.Dispose();
        Assert.Equal(1, gate.OpenDialogs);
        again.Dispose();
        Assert.True(gate.IsIdle);
    }

    [Fact]
    public void A_local_operation_that_ends_while_a_review_worker_reads_leaves_the_lock_to_the_worker()
    {
        var gate = new InteractionGate();
        var local = gate.TryBeginLocal()!;
        Assert.True(gate.LocalOperationActive);
        Assert.False(gate.CanEdit);
        var worker = gate.BeginReviewLock();
        Assert.False(worker.TookLocal);
        local.Dispose();
        Assert.False(local.Released);
        Assert.True(gate.LocalOperationActive);
        Assert.True(gate.ShowsProgress);
        worker.Dispose();
        Assert.True(worker.ReleasedLocal);
        Assert.False(gate.LocalOperationActive);
        Assert.True(gate.CanEdit);
        Assert.False(gate.ShowsProgress);
    }

    [Fact]
    public void A_local_operation_that_ends_first_unlocks_and_a_worker_that_started_alone_unlocks_itself()
    {
        var gate = new InteractionGate();
        var local = gate.TryBeginLocal()!;
        local.Dispose();
        Assert.True(local.Released);
        Assert.False(gate.LocalOperationActive);
        var worker = gate.BeginReviewLock();
        Assert.True(worker.TookLocal);
        Assert.True(gate.LocalOperationActive);
        Assert.Null(gate.TryBeginLocal());
        var nested = gate.BeginReviewLock();
        Assert.False(nested.TookLocal);
        worker.Dispose();
        Assert.False(worker.ReleasedLocal);
        Assert.True(gate.LocalOperationActive);
        nested.Dispose();
        Assert.True(nested.ReleasedLocal);
        Assert.False(gate.LocalOperationActive);
    }

    [Fact]
    public async Task A_close_that_fails_reopens_the_window_and_a_close_that_succeeds_lets_the_next_event_through()
    {
        var gate = new InteractionGate();
        var frozen = new List<bool>();
        await Assert.ThrowsAsync<IOException>(() => gate.Closing.RequestAsync(false, () => throw new IOException("save"), frozen.Add));
        Assert.True(gate.IsOpen);
        Assert.False(gate.HasClosed);
        Assert.Equal([true, false], frozen);
        Assert.True(await gate.Closing.RequestAsync(false, () => Task.CompletedTask, frozen.Add));
        Assert.True(gate.HasClosed);
        Assert.False(gate.CanAct);
        Assert.Null(gate.TryBeginUserAction());
    }

    [Fact]
    public void Blocked_storage_stays_blocked_and_stops_editing_but_not_viewing()
    {
        var gate = new InteractionGate();
        Assert.True(gate.CanEdit);
        gate.BlockStorage();
        Assert.False(gate.CanEdit);
        Assert.True(gate.CanNavigate);
        Assert.Equal("StorageReload", MessageErrors.Describe(Assert.Throws<InvalidOperationException>(gate.ThrowIfStorageBlocked)).Code);
        Assert.Null(gate.TryBeginLocal());
    }
}
