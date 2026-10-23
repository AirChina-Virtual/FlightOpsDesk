using VamSys.Core;
using System.Net;

namespace VamSys.Infrastructure;

public sealed class ApiResponseException(HttpStatusCode status, string diagnostic, ApiDiagnostic? details = null) : Exception($"HTTP {(int)status}")
{
    public HttpStatusCode Status { get; } = status;
    public string Diagnostic { get; } = diagnostic;
    public ApiDiagnostic? Details { get; } = details;
}
// A write that failed before its HTTP request was handed to the client (rate-limit wait,
// token refresh or cancellation). The server cannot have processed it.
public sealed class RequestNotSentException(Exception inner) : Exception(inner.Message, inner);
