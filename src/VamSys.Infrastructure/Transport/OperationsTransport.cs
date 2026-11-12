using VamSys.Core;
using System.Net.Http;
using System.Text.Json;

namespace VamSys.Infrastructure;

// Facade over the transport pieces (request pipeline, budget, token, paging) with the API callers use.
public sealed class OperationsTransport(HttpClient client, Uri origin, string vaKey, TokenProvider tokens, RequestCoordinator? coordinator = null)
{
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = RequestPipeline.WriteTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FlightOpsDesk/1.0");
        return client;
    }
    readonly RequestPipeline pipeline = new(client, origin, vaKey, tokens, coordinator ?? RequestCoordinator.Shared);
    public ApiDiagnostic? LastResponse => pipeline.LastResponse;
    internal ApiDiagnostic DescribeReadback(ApiDiagnostic diagnostic, string body) => ApiDiagnosticCapture.Body(diagnostic, body, tokens.SensitiveValues);
    internal DateTimeOffset UtcNow => pipeline.Clock.GetUtcNow();
    internal TimeSpan RequestTimeout => pipeline.RequestTimeout;
    internal Task DelayAsync(TimeSpan delay, CancellationToken ct) => pipeline.DelayAsync(delay, ct);
    public Task<JsonDocument> GetAsync(Uri uri, CancellationToken ct) => pipeline.SendReadAsync(uri, null, ct);
    // GET goes through the retrying read path; every other method is attempted once.
    public Task<JsonDocument> SendAsync(HttpMethod method, Uri uri, object? body, CancellationToken ct, Func<DateTimeOffset, Task>? beforeSend = null)
    {
        if (method == HttpMethod.Get)
            return pipeline.SendReadAsync(uri, body, ct, beforeSend);
        Interlocked.Increment(ref writes);
        return pipeline.SendWriteAsync(method, uri, body, ct, beforeSend);
    }
    // How many writes this transport attempted (sent or not): rows read before a write may no longer be the server's.
    long writes;
    internal long WritesAttempted => Interlocked.Read(ref writes);
    // The documented default of 15 rows costs one rate-limited request per 15 rows; 0 keeps the server default.
    public int PageSize { get; init; } = 100;
    public IAsyncEnumerable<JsonElement> ReadPagesAsync(Uri first, CancellationToken ct)
        => new Pager(origin, GetAsync).ReadAsync(first, PageSize, ct);
}
