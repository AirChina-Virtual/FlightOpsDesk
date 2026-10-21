using System.Text.Json;
using System.Text.Json.Serialization;

namespace VamSys.Core;

public sealed record ApiDiagnostic
{
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public int Status { get; init; }
    public string? Phase { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public DateTimeOffset? ServerDate { get; init; }
    public string? ErrorId { get; init; }
    public string? ServerMessage { get; init; }
    public Dictionary<string,string[]> FieldErrors { get; init; } = [];
    public Dictionary<string,string> TraceHeaders { get; init; } = [];
    public string? RequestBody { get; init; }
    public string? ResponseBody { get; init; }
    public bool BodyTruncated { get; init; }
    public string? BodySha256 { get; init; }
    [JsonIgnore] public string Summary
    {
        get {var summary=string.Join(" · ",new[]{ErrorId is null?null:$"error_id: {ErrorId}",ServerMessage,
            string.Join("; ",FieldErrors.Select(p=>$"{p.Key}: {string.Join("; ",p.Value)}"))}.Where(s=>!string.IsNullOrWhiteSpace(s)));return summary.Length>2048?summary[..2048]+"…":summary;}
    }
}

public sealed record ApiFieldDifference(string Field,string? Expected,string? Actual);
public sealed record ApiSnapshotComparison(string? PreviousUpdatedAt,string? CurrentUpdatedAt,IReadOnlyList<ApiFieldDifference> BusinessFields);
public sealed record AirlineParameter(string Id,string AirlineId,string IcaoCode,string IataCode,bool Approved,bool Restricted,IReadOnlyList<string> FleetIds)
{
    public string Label => $"{IcaoCode} / {IataCode} · {Id}";
}

public static class ApiOperationPolicy
{
    public static bool IsJumpseatCreate(ChangeItem item) => item.Resource==ResourceKind.Routes && item.Kind==ChangeKind.Create && item.After.Get("Type")=="jumpseat";
    // Any open online item blocks refresh, new submissions and connection settings, so nothing is submitted twice.
    public static bool HasOpenOnlineItems(Workspace workspace) => workspace.Jobs.Any(j=>j.Mode==RunMode.Online && j.Items.Any(i=>i.State is ItemState.Pending or ItemState.Running or ItemState.Unknown));
    // Every step is saved as in flight before it is sent, so a Pending item whose steps are all still prepared,
    // and that has no answered create or accepted write, never reached the server.
    public static bool CanDiscardUnsent(ChangeItem item) => item.State==ItemState.Pending && !item.CreateCompleted && !item.WriteAccepted
        && item.ApiSteps.All(s=>s.State==BatchStepState.Prepared);
    public static bool IsDiscardedUnsent(ChangeItem item) => item.State==ItemState.Failed && item.Description?.Code=="ApiDiscardedUnsent";
    public static string Key(ChangeItem item) => item.Resource switch
    {
        ResourceKind.Aircraft when IndependentApiEvidence.IsMove(item) => "Aircraft:Update:FleetId:Move",
        ResourceKind.Routes when item.Kind==ChangeKind.Update && item.Fields.ContainsKey("Fleet IDs") => "Routes:Update:FleetIds:Scheduled",
        ResourceKind.Routes when IsJumpseatCreate(item) => "Routes:Create:Jumpseat",
        ResourceKind.Fleets when item.Kind==ChangeKind.Create && item.After.Get("Parameter IDs")!="" => "Fleets:Create:ParameterIds",
        _ => $"{item.Resource}:{item.Kind}"
    };
    public static string JumpseatSignature(DataRow row) => "jumpseat:" + JsonSerializer.Serialize(new[]{row.Get("Departure Time (HH:MM)"),row.Get("Arrival Time (HH:MM)"),row.Get("Start Date"),row.Get("End Date")});
    public static string FieldLabel(string field,ILocalizationService language) => field switch
    {
        "Freight" => language.Get("FreightKg"), "Max Freight" => language.Get("MaxFreightKg"),
        "Parameter IDs" => language.Get("AirlineParameter"), _ => field
    };
}
