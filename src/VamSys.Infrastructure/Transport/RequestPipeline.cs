using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

// One authenticated exchange with the Operations API: validate the URL, take the VA budget, get a token,
// send, interpret the answer. Reads and writes enter through different methods; only the read entry
// supplies a ReadRetryPolicy, and the shared core is private, so a write is never retried (I01).
internal sealed class RequestPipeline(HttpClient client, Uri origin, string vaKey, TokenProvider tokens, RequestCoordinator requests)
{
    readonly RequestBudget budget = requests.BudgetFor(vaKey);
    public ApiDiagnostic? LastResponse { get; private set; }
    public TimeProvider Clock => requests.Clock;
    // E3: a read that times out is simply read again, a write that times out becomes Unknown, so writes get longer.
    // The client's own Timeout stays the ceiling for both (CreateHttpClient sets it to the write limit).
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30), WriteTimeout = TimeSpan.FromSeconds(90);
    public TimeSpan TimeoutFor(bool write)
    {
        var limit = write ? WriteTimeout : ReadTimeout;
        return client.Timeout == Timeout.InfiniteTimeSpan || client.Timeout > limit ? limit : client.Timeout;
    }
    // How long a write may take: the end of a jumpseat create's request window is saved from it.
    public TimeSpan RequestTimeout => TimeoutFor(true);
    // A pause on this VA's clock (counted as waiting in the metrics); it sends nothing and takes no budget.
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => budget.DelayAsync(delay, ct);
    public IEnumerable<string> SensitiveValues => tokens.SensitiveValues;

    static string Resource(Uri uri) => uri.AbsolutePath.Contains("/aircraft") ? "Aircraft" : uri.AbsolutePath.Contains("/airports") ? "Airports" : uri.AbsolutePath.Contains("/routings") ? "Routings" : uri.AbsolutePath.Contains("/routes") ? "Routes" : uri.AbsolutePath.Contains("/fleet") ? "Fleets" : "Other";

    public Task<JsonDocument> SendReadAsync(Uri uri, object? body, CancellationToken ct, Func<DateTimeOffset, Task>? beforeSend = null)
        => ExecuteAsync(HttpMethod.Get, uri, body, ct, beforeSend, ReadRetryPolicy.Default);

    // No retry parameter: a write is attempted once.
    public Task<JsonDocument> SendWriteAsync(HttpMethod method, Uri uri, object? body, CancellationToken ct, Func<DateTimeOffset, Task>? beforeSend = null)
        => ExecuteAsync(method, uri, body, ct, beforeSend, null);

    async Task<JsonDocument> ExecuteAsync(HttpMethod method, Uri uri, object? body, CancellationToken ct, Func<DateTimeOffset, Task>? beforeSend, ReadRetryPolicy? retry)
    {
        OriginPolicy.Ensure(origin, uri);
        var write = retry is null;
        for (int attempt = 0; ; attempt++)
        {
            try
            { await budget.EnterAsync(ct); }
            catch (Exception e) when (write) { throw new RequestNotSentException(e); }
            HttpResponseMessage response;
            DateTimeOffset started = Clock.GetUtcNow();
            var sent = false;
            try
            {
                await budget.WaitForTurnAsync(ct);
                var token = await tokens.GetAsync(ct);
                budget.Started();
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new("application/json"));
                if (body != null)
                    request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                PerformanceRun.Current?.Request(Resource(uri), method.Method);
                started = Clock.GetUtcNow();
                if (beforeSend != null)
                    await beforeSend(started);
                sent = true;
                try
                { response = await SendWithinAsync(request, write, ct); }
                catch (Exception e) when (retry?.ShouldRetry(e, attempt, ct) == true) { PerformanceRun.Current?.Retry(); budget.RetryAfterFailure(attempt); continue; }
                budget.Observe(response);
            }
            catch (Exception e) when (write && ResponseClassifier.Classify(e, sent) == TransportOutcome.NotSent) { throw new RequestNotSentException(e); }
            finally { budget.Leave(); }
            using (response)
            {
                if (retry?.ShouldRetry(response.StatusCode, attempt) == true)
                { PerformanceRun.Current?.Retry(); await budget.DelayAsync(retry.Backoff(attempt), ct, true); continue; }
                var text = await response.Content.ReadAsStringAsync(ct);
                LastResponse = ApiDiagnosticCapture.Capture(method, uri, body, response, text, started, Clock.GetUtcNow(), tokens.SensitiveValues);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                { tokens.Invalidate(); throw new ApiAccessException(Messages.Define("ApiAuth"), (int)response.StatusCode, LastResponse); }
                if (!response.IsSuccessStatusCode)
                    throw new ApiResponseException(response.StatusCode, LastResponse.ResponseBody ?? "", LastResponse);
                return JsonDocument.Parse(response.StatusCode == HttpStatusCode.NoContent ? "null" : text);
            }
        }
    }

    // Sends with the method's own time limit when it is shorter than the client's. Running out of it fails like the
    // client's own timeout (a TaskCanceledException over a TimeoutException), never like the caller's cancellation.
    async Task<HttpResponseMessage> SendWithinAsync(HttpRequestMessage request, bool write, CancellationToken ct)
    {
        var limit = TimeoutFor(write);
        if (limit == client.Timeout)
            return await client.SendAsync(request, ct);
        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
        within.CancelAfter(limit);
        try
        { return await client.SendAsync(request, within.Token); }
        catch (OperationCanceledException e) when (within.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TaskCanceledException($"The request was canceled due to the configured timeout of {limit.TotalSeconds} seconds elapsing.",
                new TimeoutException(e.Message, e));
        }
    }
}
