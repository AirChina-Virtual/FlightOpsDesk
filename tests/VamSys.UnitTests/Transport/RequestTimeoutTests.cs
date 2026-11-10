using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Transport;

/// <summary>E3: a read that times out is read again, a write that times out becomes Unknown, so writes get 90 s and reads 30 s.</summary>
public class RequestTimeoutTests
{
    static RequestPipeline Pipeline(HttpClient client) => new(client, new Uri("https://vamsys.io/"), "va",
        new TokenProvider(_ => Task.FromResult(new AccessToken("t", DateTimeOffset.UtcNow.AddHours(1)))), InstantTime.Requests());

    [Fact]
    public void The_program_client_lets_a_write_run_for_90_seconds_and_a_read_for_30()
    {
        using var client = OperationsTransport.CreateHttpClient();
        var pipeline = Pipeline(client);
        Assert.Equal(TimeSpan.FromSeconds(90), client.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(90), pipeline.TimeoutFor(write: true));
        Assert.Equal(TimeSpan.FromSeconds(30), pipeline.TimeoutFor(write: false));
        // the end of a jumpseat create's request window is saved from the write limit
        Assert.Equal(TimeSpan.FromSeconds(90), pipeline.RequestTimeout);
    }

    [Theory]
    [InlineData(10, 10, 10)]
    [InlineData(60, 60, 30)]
    [InlineData(-1, 90, 30)]
    public void A_shorter_client_timeout_stays_the_ceiling(int clientSeconds, int write, int read)
    {
        using var client = new HttpClient { Timeout = clientSeconds < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(clientSeconds) };
        var pipeline = Pipeline(client);
        Assert.Equal(TimeSpan.FromSeconds(write), pipeline.TimeoutFor(write: true));
        Assert.Equal(TimeSpan.FromSeconds(read), pipeline.TimeoutFor(write: false));
    }
}
