namespace VamSys.Core;

public enum BatchStepState { Prepared, InFlight, ResponseReceived }
public sealed class BatchStep
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public Dictionary<string, System.Text.Json.JsonElement> Body { get; set; } = [];
    public BatchStepState State { get; set; }
    public string? RemoteId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
}
