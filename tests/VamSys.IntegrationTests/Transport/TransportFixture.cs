using System.Net;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Transport;

/// <summary>An OperationsTransport over <see cref="FakeOperationsServer"/> with virtual time and a private rate-limit budget.</summary>
sealed class TransportFixture
{
    public readonly FakeOperationsServer Server = new();
    public readonly TimeProvider Time;
    public readonly RequestCoordinator Coordinator;
    public readonly OperationsTransport Transport;
    public int TokenCalls;
    public Func<Task<AccessToken>>? TokenFailure;
    public TimeSpan TokenLifetime = TimeSpan.FromHours(1);
    public readonly TokenProvider Tokens;
    public readonly HttpClient Client;

    public TransportFixture(TimeProvider? time = null, RequestCoordinator? coordinator = null, string vaKey = "va")
    {
        Time = time ?? new InstantTime();
        Coordinator = coordinator ?? new RequestCoordinator(Time);
        Client = Server.CreateClient();
        Tokens = new TokenProvider(_ =>
        {
            Interlocked.Increment(ref TokenCalls);
            return TokenFailure?.Invoke() ?? Task.FromResult(new AccessToken("token-" + TokenCalls, Time.GetUtcNow() + TokenLifetime));
        }, Time);
        Transport = new OperationsTransport(Client, OperationsAdapter.BaseUri, vaKey, Tokens, Coordinator);
    }

    public static Uri Url(string relative) => new(OperationsAdapter.BaseUri, relative);
    public static FakeResponse Retry(int status, int seconds) => new((HttpStatusCode)status, "{}", new Dictionary<string, string> { ["Retry-After"] = seconds.ToString() });
}
