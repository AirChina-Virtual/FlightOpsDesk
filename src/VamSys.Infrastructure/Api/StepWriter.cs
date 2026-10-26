using System.Globalization;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

// Sends the planned steps of one item, one request at a time, and keeps the durable record of each step.
//
// The order is the write-safety rule and must not change: a step is marked in flight and saved BEFORE it is sent, the
// answered POST's id is saved BEFORE the response is parsed any further, and every answered step is saved before the
// next one starts. Nothing is ever sent twice: a POST that already created its record is skipped on a later call.
internal sealed class StepWriter(ApiContext ctx)
{
    public async Task<string?> WriteAsync(ChangeItem change, List<ApiStep> steps, Func<Task> persist, CancellationToken ct)
    {
        var transport = ctx.Transport;
        change.ApiExpectedValues = ApiSteps.ExpectedValues(steps);
        if (change.ApiSteps.Count != steps.Count)
            ApiSteps.PrepareSteps(change, steps);
        for (int index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.Method == HttpMethod.Post && change.CreateCompleted)
                continue;
            // A second step of a create addresses the record the POST created.
            var path = step.Path == "{created}" ? ctx.Paths.Path(change.Resource, change.After, change.RemoteId) : step.Path;
            ContractValidator.ValidateCompatibility(step.Body);
            var record = change.ApiSteps[index];
            record.Path = path;
            record.State = BatchStepState.InFlight;
            record.StartedAt = transport.UtcNow;
            record.FinishedAt = null;
            await persist();
            JsonDocument result;
            // A jumpseat create can be answered without an id that survives; its request window is saved when the request really starts.
            Func<DateTimeOffset, Task>? start = ApiOperationPolicy.IsJumpseatCreate(change) && step.Method == HttpMethod.Post ? async when =>
                { record.StartedAt = when; record.DeadlineAt = transport.RequestTimeout > TimeSpan.Zero ? when + transport.RequestTimeout : null; await persist(); }
            : null;
            try
            { result = await transport.SendAsync(step.Method, new(OperationsAdapter.BaseUri, path), step.Method == HttpMethod.Delete ? null : step.Body, ct, start); }
            finally { record.FinishedAt = transport.UtcNow; }
            change.DiagnosticDetails = transport.LastResponse is { } evidence ? evidence with { Phase = "Writing" } : change.DiagnosticDetails;
            using var response = result;
            if (step.Method == HttpMethod.Post)
            {
                // Keep the created ID durable before a full parse can fail on an unexpected response shape.
                if (response.RootElement.ValueKind == JsonValueKind.Object && response.RootElement.TryGetProperty("id", out var createdId) && long.TryParse(createdId.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId) && parsedId > 0)
                {
                    change.RemoteId = parsedId.ToString(CultureInfo.InvariantCulture);
                    change.CreateCompleted = true;
                    record.RemoteId = change.RemoteId;
                    record.State = BatchStepState.ResponseReceived;
                    await persist();
                }
                var created = ctx.Mappers.For(change.Resource).FromJson(response.RootElement);
                change.RemoteId = created.Identity!.RemoteId;
                change.After = change.After.WithIdentity(created.Identity);
                change.CreateCompleted = true;
                record.RemoteId = change.RemoteId;
            }
            record.State = BatchStepState.ResponseReceived;
            change.WriteAccepted = index == steps.Count - 1;
            await persist();
        }
        return change.RemoteId ?? change.After.Identity?.RemoteId;
    }
}
