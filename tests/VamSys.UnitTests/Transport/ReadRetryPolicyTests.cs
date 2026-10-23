using System.Net;
using System.Reflection;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Transport;

/// <summary>I01: only GET may be retried, and the write entry of the pipeline cannot be given a retry policy.</summary>
public class ReadRetryPolicyTests
{
    static readonly ReadRetryPolicy Policy = ReadRetryPolicy.Default;

    [Theory]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, false)] [InlineData(4, false)]
    public void Connection_errors_are_retried_three_times(int attempt, bool retry)
        => Assert.Equal(retry, Policy.ShouldRetry(new HttpRequestException(), attempt, TestContext.Current.CancellationToken));

    [Theory]
    [InlineData(0, true)] [InlineData(2, true)] [InlineData(3, false)]
    public void Client_timeouts_are_retried_but_cancellation_by_the_caller_is_not(int attempt, bool retry)
    {
        var timeout = new TaskCanceledException("timeout", new TimeoutException());
        Assert.Equal(retry, Policy.ShouldRetry(timeout, attempt, TestContext.Current.CancellationToken));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.False(Policy.ShouldRetry(timeout, attempt, cancel.Token));
    }

    [Fact]
    public void Other_exceptions_are_never_retried()
    {
        Assert.False(Policy.ShouldRetry(new InvalidOperationException(), 0, TestContext.Current.CancellationToken));
        Assert.False(Policy.ShouldRetry(new OperationCanceledException(), 0, TestContext.Current.CancellationToken));
        Assert.False(Policy.ShouldRetry(new TimeoutException(), 0, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(429, true)] [InlineData(500, true)] [InlineData(502, true)] [InlineData(503, true)] [InlineData(599, true)]
    [InlineData(200, false)] [InlineData(204, false)] [InlineData(400, false)] [InlineData(401, false)] [InlineData(403, false)]
    [InlineData(404, false)] [InlineData(422, false)]
    public void Only_throttling_and_server_errors_are_retried(int status, bool retry)
    {
        Assert.Equal(retry, Policy.ShouldRetry((HttpStatusCode)status, 0));
        Assert.False(Policy.ShouldRetry((HttpStatusCode)status, ReadRetryPolicy.MaximumRetries));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 2)] [InlineData(2, 4)]
    public void Backoff_doubles(int attempt, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), Policy.Backoff(attempt));

    [Fact]
    public void Retries_are_capped_at_three() => Assert.Equal(3, ReadRetryPolicy.MaximumRetries);

    [Fact]
    public void No_member_outside_the_pipeline_core_accepts_a_retry_policy()
    {
        // The write entry takes no ReadRetryPolicy and the shared core is private, so a write cannot opt into retries.
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var offenders = typeof(OperationsTransport).Assembly.GetTypes()
            .SelectMany(t => t.GetMembers(flags).OfType<MethodBase>())
            .Where(m => !m.IsPrivate && m.GetParameters().Any(p => p.ParameterType == typeof(ReadRetryPolicy)))
            .Select(m => m.DeclaringType!.Name + "." + m.Name)
            .ToArray();
        Assert.Empty(offenders);
        var pipeline = typeof(OperationsTransport).Assembly.GetType("VamSys.Infrastructure.RequestPipeline")!;
        var write = pipeline.GetMethod("SendWriteAsync", flags)!;
        Assert.DoesNotContain(write.GetParameters(), p => p.ParameterType == typeof(ReadRetryPolicy));
    }
}
