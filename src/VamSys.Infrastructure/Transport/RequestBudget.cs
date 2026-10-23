using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace VamSys.Infrastructure;

// Owns the per-VA budgets and the clock they share. The process-wide instance makes every transport
// that talks to the same VA draw on one budget, so requests never run in parallel for one VA.
public sealed class RequestCoordinator(TimeProvider? timeProvider = null)
{
    public static RequestCoordinator Shared { get; } = new();
    public TimeProvider Clock { get; } = timeProvider ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, RequestBudget> budgets = new();
    internal RequestBudget BudgetFor(string vaKey) => budgets.GetOrAdd(vaKey, _ => new(Clock));
}

// Rate-limit state for one VA: strictly one request at a time, at least one second between requests,
// Retry-After and X-RateLimit-Remaining=0 push the next permitted time out. Bodies and URLs play no part.
internal sealed class RequestBudget(TimeProvider clock)
{
    readonly SemaphoreSlim gate = new(1);
    DateTimeOffset next;
    bool backoff;
    public TimeProvider Clock => clock;
    public Task EnterAsync(CancellationToken ct) => gate.WaitAsync(ct);
    public void Leave() => gate.Release();
    // Waits until the budget allows the next request.
    public async Task WaitForTurnAsync(CancellationToken ct)
    {
        var delay = next - clock.GetUtcNow();
        if (delay > TimeSpan.Zero)
            await DelayAsync(delay, ct, backoff);
    }
    // The request is about to be handed to the client: reserve the one second spacing.
    public void Started()
    {
        next = clock.GetUtcNow().AddSeconds(1);
        backoff = false;
    }
    // A read failed before any response: wait 2^attempt seconds before the next try.
    public void RetryAfterFailure(int attempt)
    {
        backoff = true;
        next = clock.GetUtcNow().AddSeconds(Math.Pow(2, attempt));
    }
    public void Observe(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0")
            next = clock.GetUtcNow().AddMinutes(1);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.FromSeconds(60);
            var until = clock.GetUtcNow() + (retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(1));
            if (until > next)
                next = until;
        }
    }
    public async Task DelayAsync(TimeSpan delay, CancellationToken ct, bool isBackoff = false)
    {
        var start = clock.GetTimestamp();
        try
        { await Task.Delay(delay, clock, ct); }
        finally { PerformanceRun.Current?.Wait(clock.GetElapsedTime(start), isBackoff); }
    }
}
