using System.Net;
using VamSys.Core;
using Phase = VamSys.Infrastructure.OperationsBatchExecutor.ExecutionPhase;

namespace VamSys.Infrastructure;

// Turns an exception thrown while an item was being executed into the item's state. What a failure means depends on
// how far the item got (the phase) and on whether the server may have processed the request:
//   not sent (rate-limit wait, token refresh, cancellation)   back to prepared; Pending, or Unknown after an answered POST
//   401 / 403                                                 rejected before processing: pause the job
//   429                                                       rejected before processing: back to Pending
//   5xx and timeouts while writing                            the server may have processed it: Unknown, never repeated
//   422 and other statuses                                    Failed (and Pending/Unknown where a retry or a check is safe)
// Nothing here sends a request. A failed save (PersistenceException) is not handled here: it ends the run.
internal sealed class FailureHandler(JobRun run)
{
    // Returns true when the job must stop (authentication), after the job and item have been saved.
    public async Task<bool> HandleAsync(Exception exception, ItemRun r)
    {
        var item = r.Item;
        var job = run.Job;
        var phase = r.Phase;
        switch (exception)
        {
            case RequestNotSentException error:
                {
                    // Nothing reached the server, so the step is prepared again. Only a step after an
                    // answered POST keeps the item Unknown, to be resumed without re-creating.
                    PrepareUnsent(item);
                    var auth = error.InnerException is ApiAccessException;
                    item.State = item.CreateCompleted || item.WriteAccepted ? ItemState.Unknown : ItemState.Pending;
                    item.SetMessage(Messages.Define(item.State == ItemState.Unknown ? "ApiResumeCreate" : auth ? "ApiAuth" : "ApiNotSent"));
                    if (auth)
                    {
                        if (item.State == ItemState.Pending)
                            run.Queries.Release(item);
                        job.SetStatus(JobStatus.Paused, Messages.Define("ApiAuth"));
                        await run.Sink.SaveAsync();
                        return true;
                    }
                    return false;
                }
            case ApiAccessException error:
                {
                    var access = error.Details is { } evidence ? evidence with { Phase = phase.ToString() } : null;
                    if (phase is Phase.Recovering or Phase.Verifying)
                        item.ReadbackDetails = access;
                    else
                        item.DiagnosticDetails = access;
                    // Like a 429, an authentication rejection happens before the write is processed.
                    var rejected = phase == Phase.Writing && error.Status is 401 or 403;
                    if (rejected)
                        PrepareUnsent(item);
                    item.State = phase == Phase.Preflight || rejected && !item.CreateCompleted && !item.WriteAccepted ? ItemState.Pending : ItemState.Unknown;
                    if (rejected && item.State == ItemState.Pending)
                        run.Queries.Release(item);
                    item.SetMessage(Messages.Define(rejected && item.State == ItemState.Unknown ? "ApiResumeCreate" : "ApiAuth"));
                    job.SetStatus(JobStatus.Paused, Messages.Define("ApiAuth"));
                    await run.Sink.SaveAsync();
                    return true;
                }
            case ApiResponseException e:
                {
                    var details = e.Details is { } evidence ? evidence with { Phase = phase.ToString() } : null;
                    if (phase is Phase.Recovering or Phase.Verifying)
                        item.ReadbackDetails = details;
                    else
                    { item.Diagnostic = e.Diagnostic; item.DiagnosticDetails = details; }
                    if (e.Status == HttpStatusCode.UnprocessableEntity)
                        run.Api.InvalidateParameters();
                    // A 429 is rejected before processing, so an unaccepted write can safely return to Pending.
                    var throttled = e.Status == HttpStatusCode.TooManyRequests && phase is Phase.Preflight or Phase.Writing;
                    item.State = phase is Phase.Recovering or Phase.Verifying || item.CreateCompleted || item.WriteAccepted
                        || phase == Phase.Writing && (int)e.Status >= 500 ? ItemState.Unknown
                        : throttled || phase == Phase.Preflight && (int)e.Status >= 500 ? ItemState.Pending : ItemState.Failed;
                    if (phase == Phase.Writing && (throttled || item.State is ItemState.Pending or ItemState.Failed && !item.CreateCompleted && !item.WriteAccepted))
                        PrepareUnsent(item);
                    item.SetMessage(throttled && item.State == ItemState.Pending ? Messages.Define("ApiRateLimited")
                        : throttled && item.State == ItemState.Unknown ? Messages.Define("ApiResumeCreate")
                        : e.Details != null ? Messages.Define(item.State == ItemState.Unknown ? "ApiUnknownError" : "ApiRejected", ("status", (int)e.Status), ("reason", e.Details.Summary))
                        : Messages.Define("ApiHttp", ("status", (int)e.Status)));
                    return false;
                }
            case OperationCanceledException or HttpRequestException or TimeoutException or System.Text.Json.JsonException:
                // Cancelled, timed out or answered with something unreadable: before a write nothing happened, after it the outcome is unknown.
                item.State = phase != Phase.Preflight ? ItemState.Unknown : ItemState.Pending;
                item.SetMessage(Messages.Define(phase == Phase.Preflight ? "ApiPreflight" : "ApiUnknown"));
                return false;
            default:
                item.State = phase != Phase.Preflight ? ItemState.Unknown : ItemState.Failed;
                item.SetMessage(MessageErrors.Describe(exception));
                return false;
        }
    }

    // Steps that never reached the server, or that it rejected before processing, are prepared
    // again; a later create step targets the created record when it is sent.
    internal static void PrepareUnsent(ChangeItem item)
    {
        for (var n = 0; n < item.ApiSteps.Count; n++)
        {
            var step = item.ApiSteps[n];
            if (step.State != BatchStepState.InFlight)
                continue;
            step.State = BatchStepState.Prepared;
            if (item.Kind == ChangeKind.Create && n > 0)
                step.Path = "{created}";
        }
    }
}
