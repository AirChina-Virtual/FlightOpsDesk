using VamSys.Core;

namespace VamSys.UnitTests.Application;

/// <summary>F24: closing the window freezes the page, waits for the save, and on failure stays open so the user can retry.</summary>
public class WindowCloseControllerTests
{
    [Fact]
    public async Task A_close_freezes_saves_and_then_is_closed()
    {
        var close = new WindowCloseController();
        var log = new List<string>();

        var closed = await close.RequestAsync(false, () => { log.Add("save"); return Task.CompletedTask; }, frozen => log.Add("freeze " + frozen));

        Assert.True(closed);
        Assert.Equal(WindowCloseState.Closed, close.State);
        Assert.False(close.IsOpen);
        Assert.Equal(["freeze True", "save"], log);
    }

    [Fact]
    public async Task While_the_save_runs_the_window_is_saving_and_a_second_request_is_refused()
    {
        var close = new WindowCloseController();
        var release = new TaskCompletionSource();
        var saves = 0;

        var first = close.RequestAsync(false, async () => { saves++; await release.Task; }, _ => { });
        var second = await close.RequestAsync(false, () => { saves++; return Task.CompletedTask; }, _ => { });

        Assert.Equal(WindowCloseState.Saving, close.State);
        Assert.False(second);
        release.SetResult();
        Assert.True(await first);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task A_running_task_or_open_dialog_refuses_the_close_without_freezing_or_saving()
    {
        var close = new WindowCloseController();
        var touched = false;

        var closed = await close.RequestAsync(true, () => { touched = true; return Task.CompletedTask; }, _ => touched = true);

        Assert.False(closed);
        Assert.False(touched);
        Assert.Equal(WindowCloseState.Open, close.State);
        Assert.True(close.IsOpen);
    }

    [Fact]
    public async Task A_failed_save_reopens_the_window_unfreezes_it_and_rethrows_so_the_close_can_be_retried()
    {
        var close = new WindowCloseController();
        var freezes = new List<bool>();

        await Assert.ThrowsAsync<IOException>(() => close.RequestAsync(false, () => throw new IOException("disk"), freezes.Add));

        Assert.Equal(WindowCloseState.Open, close.State);
        Assert.Equal([true, false], freezes);
        Assert.True(await close.RequestAsync(false, () => Task.CompletedTask, freezes.Add));
        Assert.Equal(WindowCloseState.Closed, close.State);
    }

    [Fact]
    public async Task An_asynchronous_save_failure_is_handled_the_same_way()
    {
        var close = new WindowCloseController();

        await Assert.ThrowsAsync<InvalidOperationException>(() => close.RequestAsync(false, async () => { await Task.Yield(); throw new InvalidOperationException("late"); }, _ => { }));

        Assert.Equal(WindowCloseState.Open, close.State);
    }

    [Fact]
    public async Task Once_closed_a_further_request_is_accepted_without_saving_again()
    {
        var close = new WindowCloseController();
        await close.RequestAsync(false, () => Task.CompletedTask, _ => { });
        var saved = false;

        var again = await close.RequestAsync(true, () => { saved = true; return Task.CompletedTask; }, _ => { });

        Assert.True(again);
        Assert.False(saved);
    }

    [Fact]
    public void A_new_controller_starts_open()
    {
        var close = new WindowCloseController();

        Assert.Equal(WindowCloseState.Open, close.State);
        Assert.True(close.IsOpen);
    }
}
