using System.Net;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

// What a transport outcome means for write safety. Executors keep their own state decisions; this is
// the one place that says which side of "did the server process it" an outcome falls on.
// A record struct with static members rather than an enum, so the enum inventory stays unchanged.
public readonly record struct TransportOutcome
{
    TransportOutcome(string name) => Name = name;
    public string Name { get; }
    public override string ToString() => Name;
    // The request never reached the HTTP client (budget wait, token refresh, cancellation before send).
    public static readonly TransportOutcome NotSent = new("NotSent");
    // Answered 429, 401 or 403: refused before processing, so the write did not happen.
    public static readonly TransportOutcome RejectedNotProcessed = new("RejectedNotProcessed");
    // 2xx.
    public static readonly TransportOutcome Accepted = new("Accepted");
    // The server may or may not have processed it: 5xx, timeout, connection lost or cancelled after
    // send, unreadable response.
    public static readonly TransportOutcome Unknown = new("Unknown");
    // 422: the server rejected the field values.
    public static readonly TransportOutcome FieldError = new("FieldError");
    // Any other answer (other 4xx, 1xx, 3xx) or an unrelated exception.
    public static readonly TransportOutcome Other = new("Other");
}
public static class ResponseClassifier
{
    public static TransportOutcome Classify(HttpStatusCode status)
    {
        var code = (int)status;
        return code is >= 200 and <= 299 ? TransportOutcome.Accepted
            : status is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? TransportOutcome.RejectedNotProcessed
            : status == HttpStatusCode.UnprocessableEntity ? TransportOutcome.FieldError
            : code >= 500 ? TransportOutcome.Unknown
            : TransportOutcome.Other;
    }

    // sent: the request had been handed to HttpClient.SendAsync when the exception occurred.
    public static TransportOutcome Classify(Exception error, bool sent)
    {
        if (error is RequestNotSentException)
            return TransportOutcome.NotSent;
        // A persistence failure inside the before-send hook is not a transport outcome.
        if (error is PersistenceException)
            return TransportOutcome.Other;
        if (!sent)
            return TransportOutcome.NotSent;
        return error switch
        {
            ApiResponseException response => Classify(response.Status),
            ApiAccessException => TransportOutcome.RejectedNotProcessed,
            OperationCanceledException or HttpRequestException or TimeoutException or JsonException => TransportOutcome.Unknown,
            _ => TransportOutcome.Other
        };
    }
}
