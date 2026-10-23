using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VamSys.Core;

namespace VamSys.Infrastructure;

public static partial class ApiDiagnosticCapture
{
    public const int MaximumBodyBytes = 1024 * 1024;
    static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
        {"authorization","proxy-authorization","cookie","set-cookie","access_token","refresh_token","client_secret","client_id","password","secret","apikey","api_key","api-key"};
    static string Mask(string value, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s.Length))
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        value = Regex.Replace(value, @"(?i)Bearer\s+[A-Za-z0-9._~+/-]+=*", "Bearer [REDACTED]");
        // Servers may echo a header inside a JSON message rather than under a dedicated secret key.
        return Regex.Replace(value, "(?im)\\b(proxy-authorization|authorization|set-cookie|cookie|access_token|refresh_token|client_secret|client_id|password|secret|api[_-]?key)([\\\"\\s:=]+)[^\\r\\n]+", "$1$2[REDACTED]");
    }
    public static string Redact(string text, IEnumerable<string>? secrets = null)
    {
        var values = (secrets ?? []).ToArray();
        try
        {
            var node = JsonNode.Parse(text);
            JsonNode? Clean(JsonNode? value)
            {
                if (value is JsonObject obj)
                    return new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, SecretKeys.Contains(p.Key) ? JsonValue.Create("[REDACTED]") : Clean(p.Value))));
                if (value is JsonArray array)
                    return new JsonArray(array.Select(Clean).ToArray());
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var word))
                {
                    // Diagnostic bodies are themselves serialized JSON strings in an export. Preserve their
                    // structure and sanitized-body hash instead of treating quoted keys as header text.
                    var trimmed = word.TrimStart();
                    return JsonValue.Create(trimmed.StartsWith('{') || trimmed.StartsWith('[') ? Redact(word, values) : Mask(word, values));
                }
                return value?.DeepClone();
            }
            return Clean(node)?.ToJsonString() ?? "null";
        }
        catch (JsonException)
        {
            return Mask(text, values);
        }
    }
    static string Bound(string text, out bool truncated)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        truncated = bytes.Length > MaximumBodyBytes;
        if (!truncated)
            return text;
        var size = MaximumBodyBytes;
        var utf8 = new UTF8Encoding(false, true);
        while (true)
            try
            { return utf8.GetString(bytes, 0, size); }
            catch (DecoderFallbackException) { size--; }
    }
    public static ApiDiagnostic Body(ApiDiagnostic diagnostic, string body, IEnumerable<string>? secrets = null)
    {
        var safe = Redact(body, secrets);
        var bounded = Bound(safe, out var truncated);
        return diagnostic with { ResponseBody = bounded, BodyTruncated = truncated, BodySha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(safe))) };
    }
    internal static ApiDiagnostic Capture(HttpMethod method, Uri uri, object? requestBody, HttpResponseMessage response, string text, DateTimeOffset started, DateTimeOffset finished, IEnumerable<string> secrets)
    {
        var safe = response.IsSuccessStatusCode && method == HttpMethod.Get ? "" : Redact(text, secrets);
        string? message = null, errorId = null;
        var fields = new Dictionary<string, string[]>();
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                using var json = JsonDocument.Parse(safe);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new JsonException();
                if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    message = m.GetString();
                if (root.TryGetProperty("error_id", out var id))
                    errorId = id.ToString();
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                    foreach (var field in errors.EnumerateObject())
                        fields[field.Name] = field.Value.ValueKind == JsonValueKind.Array ? field.Value.EnumerateArray().Select(v => v.ToString()).ToArray() : [field.Value.ToString()];
            }
            catch (JsonException) { }
        }
        var headers = new Dictionary<string, string>();
        foreach (var key in new[] { "x-request-id", "request-id", "x-correlation-id", "traceparent", "cf-ray" })
            if (response.Headers.TryGetValues(key, out var values))
                headers[key] = Mask(string.Join(",", values), secrets);
        // The path holds only resource names and IDs. Masking it would replace any ID or "/v3/" that
        // happens to equal a short numeric Client ID and break exact path evidence; the query is still masked.
        var diagnostic = new ApiDiagnostic
        {
            Method = method.Method,
            Path = uri.AbsolutePath + Mask(uri.Query, secrets),
            Status = (int)response.StatusCode,
            StartedAt = started,
            FinishedAt = finished,
            ServerDate = response.Headers.Date,
            ErrorId = errorId,
            ServerMessage = message,
            FieldErrors = fields,
            TraceHeaders = headers,
            RequestBody = requestBody == null ? null : Bound(Redact(JsonSerializer.Serialize(requestBody), secrets), out _)
        };
        return !response.IsSuccessStatusCode || method != HttpMethod.Get ? Body(diagnostic, safe) : diagnostic;
    }
}

public static class ApiDiagnosticExport
{
    public static string Serialize(BatchJob job) => ApiDiagnosticCapture.Redact(JsonSerializer.Serialize(new
    {
        formatVersion = 1,
        job.Id,
        job.Created,
        job.Mode,
        job.StatusCode,
        items = job.Items.Select(item => new
        {
            item.Id,
            item.Resource,
            item.Kind,
            item.State,
            item.RemoteId,
            item.Description,
            Diagnostic = item.Diagnostic == null ? null : ApiDiagnosticCapture.Redact(item.Diagnostic),
            item.DiagnosticDetails,
            item.ReadbackDetails,
            item.VerificationDifferences,
            item.PreCreateCandidateIds,
            item.PreflightComparison,
            item.DeletionVerification,
            item.ManualResolution,
            steps = item.ApiSteps
        })
    }, new JsonSerializerOptions { WriteIndented = true }));
}
