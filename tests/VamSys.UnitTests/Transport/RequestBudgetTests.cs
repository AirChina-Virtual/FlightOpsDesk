using System.Net;
using System.Net.Http.Headers;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Transport;

/// <summary>I03: one request at a time, at least 1 s apart, Retry-After and an empty server budget are obeyed.</summary>
public class RequestBudgetTests
{
    static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);

    // One complete turn: queue, wait for the permitted time, reserve the spacing, "send", leave.
    static async Task<DateTimeOffset> Turn(RequestBudget budget, TimeProvider clock, CancellationToken? cancel = null, Action<RequestBudget>? afterSend = null)
    {
        var ct = cancel ?? TestContext.Current.CancellationToken;
        await budget.EnterAsync(ct);
        try
        {
            await budget.WaitForTurnAsync(ct);
            budget.Started();
            var at = clock.GetUtcNow();
            afterSend?.Invoke(budget);
            return at;
        }
        finally { budget.Leave(); }
    }

    static async Task<DateTimeOffset> Drive(TestTime clock, Task<DateTimeOffset> task)
    { await clock.Drive(task); return await task; }

    static HttpResponseMessage Response(HttpStatusCode status, Action<HttpResponseMessage>? configure = null)
    { var r = new HttpResponseMessage(status); configure?.Invoke(r); return r; }

    [Fact]
    public async Task First_request_is_not_delayed_and_later_ones_are_spaced_by_one_second()
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        var first = await Drive(clock, Turn(budget, clock));
        Assert.Equal(clock.GetUtcNow(), first);
        var start = clock.GetUtcNow();
        var second = Turn(budget, clock);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(start + Spacing, await second);
    }

    [Fact]
    public async Task Sixty_one_requests_take_at_least_sixty_seconds_and_never_closer_than_one_second()
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        var stamps = new List<DateTimeOffset>();
        async Task Run() { for (var i = 0; i < 61; i++) stamps.Add(await Turn(budget, clock)); }
        await clock.Drive(Run());
        Assert.Equal(61, stamps.Count);
        Assert.True((stamps[^1] - stamps[0]) >= TimeSpan.FromSeconds(60));
        Assert.All(stamps.Zip(stamps.Skip(1)), pair => Assert.True(pair.Second - pair.First >= Spacing));
    }

    [Fact]
    public async Task Instances_for_one_va_share_one_budget_and_other_vas_do_not()
    {
        var clock = new TestTime();
        var coordinator = new RequestCoordinator(clock);
        Assert.Same(coordinator.BudgetFor("va"), coordinator.BudgetFor("va"));
        Assert.NotSame(coordinator.BudgetFor("va"), coordinator.BudgetFor("other"));
        var a = coordinator.BudgetFor("va");
        var b = coordinator.BudgetFor("va");
        var stamps = new List<DateTimeOffset>();
        async Task Run() { for (var i = 0; i < 6; i++) stamps.Add(await Turn(i % 2 == 0 ? a : b, clock)); }
        await clock.Drive(Run());
        Assert.All(stamps.Zip(stamps.Skip(1)), pair => Assert.True(pair.Second - pair.First >= Spacing));
        // A different VA is independent: its first turn is immediate even right after the shared one.
        var before = clock.GetUtcNow();
        Assert.Equal(before, await Turn(coordinator.BudgetFor("other"), clock).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Requests_of_one_va_never_overlap()
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        await budget.EnterAsync(TestContext.Current.CancellationToken);
        var queued = budget.EnterAsync(TestContext.Current.CancellationToken);
        Assert.False(queued.IsCompleted);
        budget.Leave();
        await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        budget.Leave();
    }

    [Fact]
    public void Shared_coordinator_is_process_wide() => Assert.Same(RequestCoordinator.Shared, RequestCoordinator.Shared);

    [Fact]
    public async Task Cancelling_a_queued_request_does_not_spend_the_budget()
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        await budget.EnterAsync(TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();
        var queued = Turn(budget, clock, cancel.Token);
        var last = Turn(budget, clock);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        budget.Leave();
        var stamp = await Drive(clock, last);
        Assert.Equal(clock.GetUtcNow(), stamp);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_the_spacing_leaves_the_budget_usable()
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        await Drive(clock, Turn(budget, clock));
        using var cancel = new CancellationTokenSource();
        var waiting = Turn(budget, clock, cancel.Token);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var next = await Drive(clock, Turn(budget, clock));
        Assert.True(next - DateTimeOffset.UnixEpoch >= Spacing);
    }

    [Fact]
    public async Task Cancelled_token_never_enters()
    {
        var budget = new RequestCoordinator(new TestTime()).BudgetFor("va");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.EnterAsync(new CancellationToken(true)));
    }

    [Theory]
    [InlineData(5)] [InlineData(30)] [InlineData(120)]
    public async Task Retry_After_seconds_delays_the_next_request(int seconds)
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        var start = clock.GetUtcNow();
        await Drive(clock, Turn(budget, clock, afterSend: b => b.Observe(Response(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds))))));
        var next = await Drive(clock, Turn(budget, clock));
        Assert.Equal(start + TimeSpan.FromSeconds(seconds), next);
    }

    [Fact]
    public async Task Retry_After_http_date_delays_until_that_time()
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        var start = clock.GetUtcNow();
        await Drive(clock, Turn(budget, clock, afterSend: b => b.Observe(Response(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new(start.AddSeconds(45))))));
        var next = await Drive(clock, Turn(budget, clock));
        Assert.Equal(start.AddSeconds(45), next);
    }

    [Fact]
    public async Task Retry_After_in_the_past_or_zero_still_waits_one_second()
    {
        foreach (var header in new RetryConditionHeaderValue[] { new(TimeSpan.Zero), new(DateTimeOffset.UnixEpoch.AddSeconds(-100)) })
        {
            var clock = new TestTime();
            var budget = new RequestCoordinator(clock).BudgetFor("va");
            await Drive(clock, Turn(budget, clock, afterSend: b => b.Observe(Response(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = header))));
            var next = await Drive(clock, Turn(budget, clock));
            Assert.Equal(DateTimeOffset.UnixEpoch + Spacing, next);
        }
    }

    [Fact]
    public async Task Missing_Retry_After_on_429_waits_sixty_seconds()
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        await Drive(clock, Turn(budget, clock, afterSend: b => b.Observe(Response(HttpStatusCode.TooManyRequests))));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(60), await Drive(clock, Turn(budget, clock)));
    }

    [Fact]
    public async Task A_shorter_Retry_After_never_shortens_a_longer_wait()
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        await Drive(clock, Turn(budget, clock, afterSend: b =>
        {
            b.Observe(Response(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(50))));
            b.Observe(Response(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(5))));
        }));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(50), await Drive(clock, Turn(budget, clock)));
    }

    [Fact]
    public async Task Empty_server_budget_waits_a_full_minute_on_any_status()
    {
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
        {
            var clock = new TestTime();
            var budget = new RequestCoordinator(clock).BudgetFor("va");
            await Drive(clock, Turn(budget, clock, afterSend: b => b.Observe(Response(status, r => r.Headers.Add("X-RateLimit-Remaining", "0")))));
            Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(1), await Drive(clock, Turn(budget, clock)));
        }
    }

    [Fact]
    public async Task Remaining_budget_above_zero_only_keeps_the_one_second_spacing()
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        await Drive(clock, Turn(budget, clock, afterSend: b => b.Observe(Response(HttpStatusCode.OK, r => r.Headers.Add("X-RateLimit-Remaining", "10")))));
        Assert.Equal(DateTimeOffset.UnixEpoch + Spacing, await Drive(clock, Turn(budget, clock)));
    }

    [Fact]
    public async Task Server_budget_and_Retry_After_combine_to_the_longer_wait()
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        await Drive(clock, Turn(budget, clock, afterSend: b => b.Observe(Response(HttpStatusCode.TooManyRequests, r =>
        { r.Headers.RetryAfter = new(TimeSpan.FromSeconds(5)); r.Headers.Add("X-RateLimit-Remaining", "0"); }))));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(1), await Drive(clock, Turn(budget, clock)));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 2)] [InlineData(2, 4)]
    public async Task Read_failure_backoff_doubles_with_each_attempt(int attempt, int seconds)
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        await Drive(clock, Turn(budget, clock, afterSend: b => b.RetryAfterFailure(attempt)));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(seconds), await Drive(clock, Turn(budget, clock)));
    }

    [Fact]
    public async Task Waits_are_reported_as_rate_wait_or_backoff()
    {
        var clock = new TestTime();
        var budget = new RequestCoordinator(clock).BudgetFor("va");
        using var metrics = PerformanceRun.Begin(Guid.NewGuid(), Guid.NewGuid());
        await Drive(clock, Turn(budget, clock));
        await Drive(clock, Turn(budget, clock, afterSend: b => b.RetryAfterFailure(1)));
        await Drive(clock, Turn(budget, clock));
        Assert.Equal(1000 + 0, metrics.RateWaitMs, 1);
        Assert.Equal(2000, metrics.BackoffMs, 1);
    }

    [Fact]
    public async Task Instant_time_still_computes_the_waits()
    {
        var time = new InstantTime();
        var budget = new RequestCoordinator(time).BudgetFor("va");
        var first = await Turn(budget, time);
        var second = await Turn(budget, time, afterSend: b => b.Observe(Response(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(30)))));
        var third = await Turn(budget, time);
        Assert.True(second - first >= Spacing);
        Assert.True(third - second >= TimeSpan.FromSeconds(30));
    }
}
