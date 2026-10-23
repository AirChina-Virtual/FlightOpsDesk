using System.Net;

namespace VamSys.Infrastructure;

// Backoff retry for GET requests only. The write path of the pipeline has no parameter of this type,
// so a write cannot be retried by construction (I01).
internal sealed class ReadRetryPolicy
{
    public static ReadRetryPolicy Default { get; } = new();
    public const int MaximumRetries = 3;
    // A transport failure before any response: connection error, or a timeout that was not the caller's cancellation.
    public bool ShouldRetry(Exception error, int attempt, CancellationToken ct) => attempt < MaximumRetries
        && (error is HttpRequestException || error is TaskCanceledException && !ct.IsCancellationRequested);
    public bool ShouldRetry(HttpStatusCode status, int attempt) => attempt < MaximumRetries
        && (status == HttpStatusCode.TooManyRequests || (int)status >= 500);
    public TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));
}
