using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Transport;

/// <summary>
/// I01 / F15: the single mapping from an HTTP outcome or exception to "did the server process it".
/// The executor keeps its own state decisions; these tables lock the categories those decisions rely on.
/// </summary>
public class ResponseClassifierTests
{
    static readonly TransportOutcome NotSent = TransportOutcome.NotSent;
    static readonly TransportOutcome Rejected = TransportOutcome.RejectedNotProcessed;
    static readonly TransportOutcome Accepted = TransportOutcome.Accepted;
    static readonly TransportOutcome Unknown = TransportOutcome.Unknown;
    static readonly TransportOutcome FieldError = TransportOutcome.FieldError;
    static readonly TransportOutcome Other = TransportOutcome.Other;

    [Fact]
    public void Every_status_code_from_100_to_599_has_the_documented_category()
    {
        for (var code = 100; code <= 599; code++)
        {
            var expected = code is >= 200 and <= 299 ? Accepted
                : code is 429 or 401 or 403 ? Rejected
                : code == 422 ? FieldError
                : code >= 500 ? Unknown
                : Other;
            Assert.True(expected == ResponseClassifier.Classify((HttpStatusCode)code), $"HTTP {code}");
        }
    }

    [Theory]
    [InlineData(200)] [InlineData(201)] [InlineData(204)]
    public void Success_statuses_are_accepted(int status) => Assert.Equal(Accepted, ResponseClassifier.Classify((HttpStatusCode)status));

    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(429)]
    public void Throttling_and_authentication_refusals_are_rejected_before_processing(int status)
        => Assert.Equal(Rejected, ResponseClassifier.Classify((HttpStatusCode)status));

    [Theory]
    [InlineData(500)] [InlineData(502)] [InlineData(503)] [InlineData(504)]
    public void Server_errors_leave_the_outcome_unknown(int status) => Assert.Equal(Unknown, ResponseClassifier.Classify((HttpStatusCode)status));

    [Fact]
    public void Validation_failure_is_a_field_error() => Assert.Equal(FieldError, ResponseClassifier.Classify(HttpStatusCode.UnprocessableEntity));

    [Theory]
    [InlineData(400)] [InlineData(404)] [InlineData(405)] [InlineData(409)] [InlineData(408)] [InlineData(302)]
    public void Other_client_answers_are_other(int status) => Assert.Equal(Other, ResponseClassifier.Classify((HttpStatusCode)status));

    public static TheoryData<string, Exception, bool, string> Exceptions() => new()
    {
        // Before the request was handed to the client nothing can have been processed, whatever failed.
        { "cancelled before send", new OperationCanceledException(), false, nameof(TransportOutcome.NotSent) },
        { "task cancelled before send", new TaskCanceledException(), false, nameof(TransportOutcome.NotSent) },
        { "token endpoint down before send", new HttpRequestException("token"), false, nameof(TransportOutcome.NotSent) },
        { "token rejected before send", new ApiAccessException("denied", 401), false, nameof(TransportOutcome.NotSent) },
        { "timeout before send", new TimeoutException(), false, nameof(TransportOutcome.NotSent) },
        { "invalid token response before send", new JsonException(), false, nameof(TransportOutcome.NotSent) },
        { "already wrapped", new RequestNotSentException(new InvalidOperationException()), true, nameof(TransportOutcome.NotSent) },
        { "already wrapped before send", new RequestNotSentException(new InvalidOperationException()), false, nameof(TransportOutcome.NotSent) },
        // A persistence failure of the before-send hook is not a transport outcome.
        { "persistence before send", new PersistenceException(new IOException("disk")), false, nameof(TransportOutcome.Other) },
        { "persistence after send", new PersistenceException(new IOException("disk")), true, nameof(TransportOutcome.Other) },
        // After the request left, the server may have processed it.
        { "client timeout after send", new TaskCanceledException("timeout", new TimeoutException()), true, nameof(TransportOutcome.Unknown) },
        { "cancelled after send", new OperationCanceledException(), true, nameof(TransportOutcome.Unknown) },
        { "connection lost after send", new HttpRequestException("reset"), true, nameof(TransportOutcome.Unknown) },
        { "timeout exception after send", new TimeoutException(), true, nameof(TransportOutcome.Unknown) },
        { "unreadable body after send", new JsonException(), true, nameof(TransportOutcome.Unknown) },
        { "api 429", new ApiResponseException(HttpStatusCode.TooManyRequests, ""), true, nameof(TransportOutcome.RejectedNotProcessed) },
        { "api 422", new ApiResponseException(HttpStatusCode.UnprocessableEntity, ""), true, nameof(TransportOutcome.FieldError) },
        { "api 500", new ApiResponseException(HttpStatusCode.InternalServerError, ""), true, nameof(TransportOutcome.Unknown) },
        { "api 404", new ApiResponseException(HttpStatusCode.NotFound, ""), true, nameof(TransportOutcome.Other) },
        { "api 401", new ApiAccessException("denied", 401), true, nameof(TransportOutcome.RejectedNotProcessed) },
        { "api 403", new ApiAccessException("denied", 403), true, nameof(TransportOutcome.RejectedNotProcessed) },
        { "unrelated after send", new InvalidOperationException(), true, nameof(TransportOutcome.Other) },
    };

    [Theory]
    [MemberData(nameof(Exceptions))]
    public void Exceptions_are_classified_by_whether_the_request_was_sent(string name, Exception error, bool sent, string expected)
        => Assert.True(expected == ResponseClassifier.Classify(error, sent).Name, name);

    [Fact]
    public void Categories_are_distinct_and_named()
    {
        var all = new[] { NotSent, Rejected, Accepted, Unknown, FieldError, Other };
        Assert.Equal(6, all.Distinct().Count());
        Assert.Equal("RejectedNotProcessed", Rejected.ToString());
    }
}
