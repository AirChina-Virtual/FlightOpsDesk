using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

public record AccessToken(string Value, DateTimeOffset ExpiresAt);
public sealed record OperationsCredentials(string ClientId, string Secret);
public sealed class TokenProvider(Func<CancellationToken, Task<AccessToken>> acquire, TimeProvider? timeProvider=null)
{
    internal string[] CredentialValues { get; private init; } = [];
    internal IEnumerable<string> SensitiveValues => cached is null?CredentialValues:CredentialValues.Append(cached.Value);
    readonly TimeProvider clock=timeProvider ?? TimeProvider.System;
    readonly SemaphoreSlim gate = new(1); AccessToken? cached;
    public async Task<string> GetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { if (cached is null || cached.ExpiresAt <= clock.GetUtcNow().AddMinutes(1)) cached = await acquire(ct); return cached.Value; }
        finally { gate.Release(); }
    }
    public void Invalidate() => cached = null;
    public static TokenProvider ClientCredentials(HttpClient client, OperationsCredentials credentials, TimeProvider? timeProvider=null) => new(async ct =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://vamsys.io/oauth/token");
        request.Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["grant_type"] = "client_credentials", ["client_id"] = credentials.ClientId, ["client_secret"] = credentials.Secret, ["scope"] = "*" });
        request.Headers.Accept.Add(new("application/json"));
        PerformanceRun.Current?.Request("OAuth","POST");
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ApiAccessException(Messages.Define("ApiAuth"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var token = json.RootElement.GetProperty("access_token").GetString();
        var seconds = json.RootElement.GetProperty("expires_in").GetInt64();
        if (string.IsNullOrWhiteSpace(token) || seconds <= 0 || !json.RootElement.GetProperty("token_type").GetString()!.Equals("Bearer", StringComparison.OrdinalIgnoreCase)) throw new ApiAccessException(Messages.Define("ApiAuth"));
        return new AccessToken(token, (timeProvider ?? TimeProvider.System).GetUtcNow().AddSeconds(seconds));
    // A numeric Client ID is a public identifier that collides with record IDs and numbers in
    // diagnostic bodies, so it is masked by key (client_id) only. Secret and token stay literal.
    },timeProvider){CredentialValues=credentials.ClientId.All(char.IsAsciiDigit)?[credentials.Secret]:[credentials.ClientId,credentials.Secret]};
}
public sealed class ApiResponseException(HttpStatusCode status, string diagnostic,ApiDiagnostic? details=null) : Exception($"HTTP {(int)status}")
{
    public HttpStatusCode Status { get; } = status;
    public string Diagnostic { get; } = diagnostic;
    public ApiDiagnostic? Details { get; } = details;
}
// A write that failed before its HTTP request was handed to the client (rate-limit wait,
// token refresh or cancellation). The server cannot have processed it.
public sealed class RequestNotSentException(Exception inner) : Exception(inner.Message, inner);
public sealed class OperationsTransport(HttpClient client, Uri origin, string vaKey, TokenProvider tokens, RequestCoordinator? coordinator=null)
{
    public static HttpClient CreateHttpClient()
    {
        var client=new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FlightOpsDesk/1.0");return client;
    }
    public ApiDiagnostic? LastResponse { get; private set; }
    internal ApiDiagnostic DescribeReadback(ApiDiagnostic diagnostic,string body)=>ApiDiagnosticCapture.Body(diagnostic,body,tokens.SensitiveValues);
    readonly RequestCoordinator requests=coordinator ?? RequestCoordinator.Shared;
    TimeProvider Clock=>requests.Clock;
    internal DateTimeOffset UtcNow=>Clock.GetUtcNow();
    internal TimeSpan RequestTimeout=>client.Timeout;
    async Task Delay(TimeSpan delay,CancellationToken ct,bool backoff=false)
    {
        var start=Clock.GetTimestamp();
        try { await Task.Delay(delay,Clock,ct); }
        finally { PerformanceRun.Current?.Wait(Clock.GetElapsedTime(start),backoff); }
    }
    static string Resource(Uri uri) => uri.AbsolutePath.Contains("/aircraft")?"Aircraft":uri.AbsolutePath.Contains("/airports")?"Airports":uri.AbsolutePath.Contains("/routings")?"Routings":uri.AbsolutePath.Contains("/routes")?"Routes":uri.AbsolutePath.Contains("/fleet")?"Fleets":"Other";
    static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port;
    public Task<JsonDocument> GetAsync(Uri uri, CancellationToken ct) => SendAsync(HttpMethod.Get, uri, null, ct);
    public async Task<JsonDocument> SendAsync(HttpMethod method, Uri uri, object? body, CancellationToken ct,Func<DateTimeOffset,Task>? beforeSend=null)
    {
        if (uri.Scheme != "https" || !SameOrigin(origin, uri) || uri.UserInfo != "" || (origin.AbsolutePath != "/" && !uri.AbsolutePath.StartsWith(origin.AbsolutePath, StringComparison.Ordinal))) throw new InvalidOperationException(Messages.Define("Text_A69A8C3CDE"));
        var budget = requests.Budgets.GetOrAdd(vaKey, _ => new());
        bool read = method == HttpMethod.Get;
        for (int attempt = 0; ; attempt++)
        {
            try { await budget.Gate.WaitAsync(ct); }
            catch (Exception e) when (!read) { throw new RequestNotSentException(e); }
            HttpResponseMessage response;
            DateTimeOffset started=Clock.GetUtcNow();
            var sent=false;
            try
            {
                var delay = budget.Next - Clock.GetUtcNow();
                if (delay > TimeSpan.Zero) await Delay(delay, ct,budget.Backoff);
                var token = await tokens.GetAsync(ct);
                budget.Next = Clock.GetUtcNow().AddSeconds(1); budget.Backoff=false;
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new("application/json"));
                if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                PerformanceRun.Current?.Request(Resource(uri),method.Method);
                started=Clock.GetUtcNow();
                if(beforeSend!=null)await beforeSend(started);
                sent=true;
                try { response = await client.SendAsync(request, ct); }
                catch (HttpRequestException) when (read && attempt < 3) { PerformanceRun.Current?.Retry(); budget.Backoff=true; budget.Next = Clock.GetUtcNow().AddSeconds(Math.Pow(2, attempt)); continue; }
                catch (TaskCanceledException) when (read && !ct.IsCancellationRequested && attempt < 3) { PerformanceRun.Current?.Retry(); budget.Backoff=true; budget.Next = Clock.GetUtcNow().AddSeconds(Math.Pow(2, attempt)); continue; }
                if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0") budget.Next = Clock.GetUtcNow().AddMinutes(1);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - Clock.GetUtcNow()) ?? TimeSpan.FromSeconds(60);
                    var until=Clock.GetUtcNow() + (retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(1));
                    if(until>budget.Next) budget.Next=until;
                }
            }
            catch (Exception e) when (!read && !sent && e is not PersistenceException) { throw new RequestNotSentException(e); }
            finally { budget.Gate.Release(); }
            using (response)
            {
                if (read && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 3)
                { PerformanceRun.Current?.Retry(); await Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct,true); continue; }
                var text = await response.Content.ReadAsStringAsync(ct);
                LastResponse=ApiDiagnosticCapture.Capture(method,uri,body,response,text,started,Clock.GetUtcNow(),tokens.SensitiveValues);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                { tokens.Invalidate(); throw new ApiAccessException(Messages.Define("ApiAuth"),(int)response.StatusCode,LastResponse); }
                if (!response.IsSuccessStatusCode) throw new ApiResponseException(response.StatusCode,LastResponse.ResponseBody??"",LastResponse);
                return JsonDocument.Parse(response.StatusCode == HttpStatusCode.NoContent ? "null" : text);
            }
        }
    }
    // The documented default of 15 rows costs one rate-limited request per 15 rows; 0 keeps the server default.
    public int PageSize { get; init; } = 100;
    const string PageSizeKey = "page%5Bsize%5D";
    static bool HasPageSize(Uri uri) => uri.Query.Contains(PageSizeKey, StringComparison.OrdinalIgnoreCase) || Uri.UnescapeDataString(uri.Query).Contains("page[size]", StringComparison.OrdinalIgnoreCase);
    static Uri WithPageSize(Uri uri, int size) => size <= 0 || HasPageSize(uri) ? uri
        : new UriBuilder(uri) { Query = (uri.Query.Length > 1 ? uri.Query[1..] + "&" : "") + PageSizeKey + "=" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Uri;
    public async IAsyncEnumerable<JsonElement> ReadPagesAsync(Uri first, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Uri? next = first; var visited = new HashSet<string>(); var size = PageSize; var firstPage = true;
        while (next != null)
        {
            if (!visited.Add(next.AbsoluteUri)) throw new FormatException(Messages.Define("Text_122031269F"));
            var target = WithPageSize(next, size);
            JsonDocument json;
            try { json = await GetAsync(target, ct); }
            catch (ApiResponseException e) when (firstPage && target != next && e.Status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            {
                // An instance that rejects the page size still serves its documented default.
                size = 0; json = await GetAsync(next, ct);
            }
            firstPage = false;
            using var page = json;
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new FormatException(Messages.Define("Text_61CA285291"));
            PerformanceRun.Current?.Page();
            foreach (var row in data.EnumerateArray()) yield return row.Clone();
            next = null;
            if (json.RootElement.TryGetProperty("meta", out var meta))
            {
                if (!meta.TryGetProperty("next_cursor_url", out var cursor)) meta.TryGetProperty("next_page_url", out cursor);
                if (cursor.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(cursor.GetString())) next = new Uri(origin, cursor.GetString());
            }
        }
    }
}
public static class ApiContracts
{
    public const string Version = "3.0.0";
    public const string Sha256 = "1EC1A869FC3C6F4D70A7A6ECD50170D5D915551794E796F9F34D5AFB944D78C2";
    public static IReadOnlyList<ResourceCapability> Capabilities => Enum.GetValues<ResourceKind>()
        .Select(k => new ResourceCapability(k, true, true, Messages.Define("ApiCapability"))).ToArray();
}
public sealed class UnverifiedOperationsAdapter : IResourceReader, IResourceWriter
{
    public async IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind kind, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    { await Task.FromException(new NotSupportedException()); yield break; }
    public Task<DataRow?> FindAsync(ResourceKind kind, string id, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> WriteAsync(ChangeItem change, CancellationToken ct) => throw new NotSupportedException();
}
