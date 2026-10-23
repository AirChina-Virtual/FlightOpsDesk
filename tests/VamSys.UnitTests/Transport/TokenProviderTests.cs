using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Transport;

/// <summary>Token caching: reused until one minute before expiry, then re-acquired; invalidation forces a new one.</summary>
public class TokenProviderTests
{
    sealed class Issuer(TimeProvider clock, TimeSpan lifetime)
    {
        public int Calls;
        public Task<AccessToken> Acquire(CancellationToken ct)
        { var n = Interlocked.Increment(ref Calls); return Task.FromResult(new AccessToken("token-" + n, clock.GetUtcNow() + lifetime)); }
    }

    [Fact]
    public async Task A_valid_token_is_acquired_once_and_reused()
    {
        var clock = new TestTime();
        var issuer = new Issuer(clock, TimeSpan.FromHours(1));
        var provider = new TokenProvider(issuer.Acquire, clock);
        Assert.Equal("token-1", await provider.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal("token-1", await provider.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, issuer.Calls);
    }

    [Fact]
    public async Task A_token_is_replaced_from_one_minute_before_it_expires()
    {
        var clock = new TestTime();
        var issuer = new Issuer(clock, TimeSpan.FromMinutes(10));
        var provider = new TokenProvider(issuer.Acquire, clock);
        await provider.GetAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(539));
        Assert.Equal("token-1", await provider.GetAsync(TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("token-2", await provider.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, issuer.Calls);
    }

    [Fact]
    public async Task A_token_that_lives_less_than_a_minute_is_fetched_for_every_request()
    {
        var clock = new TestTime();
        var issuer = new Issuer(clock, TimeSpan.FromSeconds(30));
        var provider = new TokenProvider(issuer.Acquire, clock);
        await provider.GetAsync(TestContext.Current.CancellationToken);
        await provider.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, issuer.Calls);
    }

    [Fact]
    public async Task Invalidate_forces_a_new_token()
    {
        var clock = new TestTime();
        var issuer = new Issuer(clock, TimeSpan.FromHours(1));
        var provider = new TokenProvider(issuer.Acquire, clock);
        await provider.GetAsync(TestContext.Current.CancellationToken);
        provider.Invalidate();
        Assert.Equal("token-2", await provider.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_callers_share_one_acquisition()
    {
        var clock = new TestTime();
        var release = new TaskCompletionSource();
        var calls = 0;
        var provider = new TokenProvider(async ct =>
        { Interlocked.Increment(ref calls); await release.Task; return new AccessToken("t", clock.GetUtcNow().AddHours(1)); }, clock);
        var all = Enumerable.Range(0, 5).Select(_ => provider.GetAsync(TestContext.Current.CancellationToken)).ToArray();
        release.SetResult();
        Assert.All(await Task.WhenAll(all), value => Assert.Equal("t", value));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_failed_acquisition_is_not_cached()
    {
        var clock = new TestTime();
        var calls = 0;
        var provider = new TokenProvider(ct => ++calls == 1 ? throw new HttpRequestException("down") : Task.FromResult(new AccessToken("ok", clock.GetUtcNow().AddHours(1))), clock);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ok", await provider.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Waiting_for_the_token_can_be_cancelled()
    {
        var provider = new TokenProvider(ct => Task.FromResult(new AccessToken("t", DateTimeOffset.MaxValue)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync(new CancellationToken(true)));
    }
}
