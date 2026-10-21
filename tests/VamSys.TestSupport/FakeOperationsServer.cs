using System.Net;
using System.Text;

namespace VamSys.TestSupport;

/// <summary>One request seen by <see cref="FakeOperationsServer"/>. Body is the exact string sent on the wire.</summary>
public sealed record RecordedRequest(int Order, string Method, string PathAndQuery, string Path, string? Body, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>A scripted reply. Json is sent with content type application/json.</summary>
public sealed record FakeResponse(HttpStatusCode Status = HttpStatusCode.OK, string? Json = "{}", IReadOnlyDictionary<string, string>? Headers = null)
{
    public static FakeResponse Ok(string json = "{}") => new(HttpStatusCode.OK, json);
    public static FakeResponse WithStatus(int status, string json = "{}") => new((HttpStatusCode)status, json);
}

/// <summary>
/// Fake Operations API. Script replies per method + path (absolute path such as "/api/v3/operations/fleet",
/// or a path relative to the operations base), queue several replies for one route, or supply a handler.
/// Every request is recorded in arrival order. Unscripted routes answer 404. /oauth/token is answered automatically
/// (and is not recorded unless <see cref="RecordTokenRequests"/> is set).
/// </summary>
public sealed class FakeOperationsServer : HttpMessageHandler
{
    public const string OperationsPrefix = "/api/v3/operations/";
    readonly object gate = new();
    readonly List<RecordedRequest> requests = [];
    readonly Dictionary<string, Queue<Func<RecordedRequest, FakeResponse>>> routes = new(StringComparer.Ordinal);
    readonly Dictionary<string, Func<RecordedRequest, FakeResponse>> fallbacks = new(StringComparer.Ordinal);

    public bool RecordTokenRequests { get; set; }
    public FakeResponse NotFound { get; set; } = FakeResponse.WithStatus(404, "{}");

    public IReadOnlyList<RecordedRequest> Requests { get { lock (gate) return requests.ToArray(); } }
    public IReadOnlyList<RecordedRequest> RequestsFor(HttpMethod method, string path)
    { var key = Key(method, path); lock (gate) return requests.Where(r => Key(r.Method, r.Path) == key).ToArray(); }

    /// <summary>Every call to the route gets this reply (after any queued one-shot replies are consumed).</summary>
    public FakeOperationsServer On(HttpMethod method, string path, FakeResponse response) => On(method, path, _ => response);
    public FakeOperationsServer On(HttpMethod method, string path, Func<RecordedRequest, FakeResponse> handler)
    { lock (gate) fallbacks[Key(method, path)] = handler; return this; }

    /// <summary>The next call to the route gets this reply once; queued replies are consumed in order.</summary>
    public FakeOperationsServer Enqueue(HttpMethod method, string path, params FakeResponse[] responses)
    {
        lock (gate)
        {
            var k = Key(method, path);
            if (!routes.TryGetValue(k, out var q))
                routes[k] = q = new();
            foreach (var r in responses)
                q.Enqueue(_ => r);
        }
        return this;
    }

    public void Reset() { lock (gate) { requests.Clear(); routes.Clear(); fallbacks.Clear(); } }

    public HttpClient CreateClient() => new(this) { BaseAddress = new Uri("https://fake.operations.test") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (uri.AbsolutePath == "/oauth/token" && !RecordTokenRequests)
            return Build(FakeResponse.Ok("{\"access_token\":\"synthetic-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"));
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
            headers[h.Key] = string.Join(",", h.Value);
        if (request.Content != null)
        foreach (var h in request.Content.Headers)
            headers[h.Key] = string.Join(",", h.Value);
        Func<RecordedRequest, FakeResponse>? handler = null;
        RecordedRequest recorded;
        lock (gate)
        {
            recorded = new(requests.Count, request.Method.Method, uri.PathAndQuery, uri.AbsolutePath, body, headers);
            requests.Add(recorded);
            var k = Key(recorded.Method, recorded.Path);
            if (routes.TryGetValue(k, out var q) && q.Count > 0)
                handler = q.Dequeue();
            else
                fallbacks.TryGetValue(k, out handler);
        }
        return Build(handler?.Invoke(recorded) ?? NotFound);
    }

    static HttpResponseMessage Build(FakeResponse r)
    {
        var response = new HttpResponseMessage(r.Status);
        if (r.Json != null)
            response.Content = new StringContent(r.Json, Encoding.UTF8, "application/json");
        if (r.Headers != null)
        foreach (var (k, v) in r.Headers)
            response.Headers.TryAddWithoutValidation(k, v);
        return response;
    }
    static string Key(HttpMethod m, string path) => Key(m.Method, path);
    static string Key(string method, string path) => method.ToUpperInvariant() + " " + (path.StartsWith('/') ? path : OperationsPrefix + path);
}

