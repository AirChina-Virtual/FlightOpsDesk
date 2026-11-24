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
    public Dictionary<string, string[]> FieldErrors { get; init; } = [];
    public Dictionary<string, string> TraceHeaders { get; init; } = [];
    public string? RequestBody { get; init; }
    public string? ResponseBody { get; init; }
    public bool BodyTruncated { get; init; }
    public string? BodySha256 { get; init; }
    [JsonIgnore]
    public string Summary {
        get
        {
            var summary = string.Join(" · ", new[]{ErrorId is null?null:$"error_id: {ErrorId}",ServerMessage,
            string.Join("; ",FieldErrors.Select(p=>$"{p.Key}: {string.Join("; ",p.Value)}"))}.Where(s => !string.IsNullOrWhiteSpace(s)));
            return summary.Length > 2048 ? summary[..2048] + "…" : summary;
        }
    }
}

public sealed record ApiFieldDifference(string Field, string? Expected, string? Actual);
public sealed record ApiSnapshotComparison(string? PreviousUpdatedAt, string? CurrentUpdatedAt, IReadOnlyList<ApiFieldDifference> BusinessFields);
public sealed record AirlineParameter(string Id, string AirlineId, string IcaoCode, string IataCode, bool Approved, bool Restricted, IReadOnlyList<string> FleetIds)
{
    public string Label => $"{IcaoCode} / {IataCode} · {Id}";
}

// How an operation is released from the review's first-three sample (ApiOperationPolicy.ReleaseOf).
public enum SampleRelease { Sampled, Verified, UserDecided }

public static class ApiOperationPolicy
{
    public static bool IsJumpseatCreate(ChangeItem item) => item.Resource == ResourceKind.Routes && item.Kind == ChangeKind.Create && item.After.Get("Type") == "jumpseat";
    // Any open online item blocks refresh, new submissions and connection settings, so nothing is submitted twice.
    public static bool HasOpenOnlineItems(Workspace workspace) => workspace.Jobs.Any(j => j.Mode == RunMode.Online && OpenItems(j).Any());
    // The items of a task that may still be sent: pending, running or unknown ones, and skipped ones while "Continue" can
    // still send them, that is while everything they wait on has succeeded or is itself open. A skipped item that waits on
    // a failed, conflicting or manually confirmed item can never run in its task and is not open.
    public static IEnumerable<ChangeItem> OpenItems(BatchJob job)
    {
        var byId = new Dictionary<Guid, ChangeItem>();
        foreach (var item in job.Items)
            byId.TryAdd(item.Id, item);
        var open = new Dictionary<ChangeItem, bool>();
        foreach (var item in job.Items)
            if (IsOpen(item, []))
                yield return item;

        bool IsOpen(ChangeItem item, HashSet<ChangeItem> visiting)
        {
            if (item.State is ItemState.Pending or ItemState.Running or ItemState.Unknown)
                return true;
            if (item.State != ItemState.Skipped)
                return false;
            if (open.TryGetValue(item, out var known))
                return known;
            if (!visiting.Add(item))
                return false;
            var result = item.Dependencies.All(id => !byId.TryGetValue(id, out var dependency)
                || dependency.State == ItemState.Succeeded || IsOpen(dependency, visiting));
            open[item] = result;
            return result;
        }
    }
    // Every step is saved as in flight before it is sent, so a Pending item whose steps are all still prepared,
    // and that has no answered create or accepted write, never reached the server.
    public static bool CanDiscardUnsent(ChangeItem item) => item.State == ItemState.Pending && !item.CreateCompleted && !item.WriteAccepted
        && item.ApiSteps.All(s => s.State == BatchStepState.Prepared);
    public static bool IsDiscardedUnsent(ChangeItem item) => item.State == ItemState.Failed && item.Description?.Code == "ApiDiscardedUnsent";
    public static string Key(ChangeItem item) => item.Resource switch
    {
        ResourceKind.Aircraft when IndependentApiEvidence.IsMove(item) => "Aircraft:Update:FleetId:Move",
        ResourceKind.Routes when item.Kind == ChangeKind.Update && item.Fields.ContainsKey("Fleet IDs")
            && FleetSetKeys.TryGetValue((item.Before ?? item.After).Get("Type"), out var fleets) => fleets,
        ResourceKind.Routes when IsJumpseatCreate(item) => "Routes:Create:Jumpseat",
        ResourceKind.Routes when item.Kind == ChangeKind.Create && RouteCreateKeys.TryGetValue(item.After.Get("Type"), out var create) => create,
        ResourceKind.Routes when item.Kind == ChangeKind.Update && RouteUpdateKeys.TryGetValue(RouteUpdateType(item), out var update) => update,
        ResourceKind.Fleets when item.Kind == ChangeKind.Create && item.After.Get("Parameter IDs") != "" => "Fleets:Create:ParameterIds",
        _ => PlainKey(item.Resource, item.Kind)
    };
    // Route creates and updates are keyed per route type. scheduled keeps the plain keys (Routes:Create, Routes:Update) that
    // saved verification marks and old jobs name; a jumpseat create keeps Routes:Create:Jumpseat. A type outside
    // Schemas.RouteTypes also keeps the plain key: the planner refuses it before anything is sent.
    static readonly IReadOnlyDictionary<string, string> RouteCreateKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cargo"] = "Routes:Create:Cargo",
        ["charter"] = "Routes:Create:Charter",
        ["training"] = "Routes:Create:Training",
        ["vfr"] = "Routes:Create:Vfr",
        ["repositioning"] = "Routes:Create:Repositioning",
    };
    static readonly IReadOnlyDictionary<string, string> RouteUpdateKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cargo"] = "Routes:Update:Cargo",
        ["charter"] = "Routes:Update:Charter",
        ["training"] = "Routes:Update:Training",
        ["vfr"] = "Routes:Update:Vfr",
        ["repositioning"] = "Routes:Update:Repositioning",
        ["jumpseat"] = "Routes:Update:Jumpseat",
    };
    // An update is named by the stored type (Before). When it changes the type, the new type names it unless that is
    // scheduled: a change that involves a non-scheduled type never takes the scheduled key (scheduled -> cargo is a cargo
    // update, cargo -> scheduled stays one), and between two non-scheduled types the result decides.
    static string RouteUpdateType(ChangeItem item)
    {
        var before = (item.Before ?? item.After).Get("Type");
        var after = item.After.Get("Type");
        return after != before && after != "scheduled" && Schemas.RouteTypes.Contains(after) ? after : before;
    }
    // A route's fleet-set update is keyed per route type: scheduled keeps its original key (saved verification marks and
    // old jobs name it). The planner only lets the set change while the type stays the same, so the stored type (Before)
    // names the operation. A jumpseat (or an unknown type) has no fleet-set key: the planner always refuses that change
    // (ApiFleetSet), so such an item falls back to the route's plain update key instead of a key nothing could ever send.
    static readonly IReadOnlyDictionary<string, string> FleetSetKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["scheduled"] = "Routes:Update:FleetIds:Scheduled",
        ["cargo"] = "Routes:Update:FleetIds:Cargo",
        ["charter"] = "Routes:Update:FleetIds:Charter",
        ["training"] = "Routes:Update:FleetIds:Training",
        ["vfr"] = "Routes:Update:FleetIds:Vfr",
        ["repositioning"] = "Routes:Update:FleetIds:Repositioning",
    };

    // Operations sent in full in every workspace, without the first-three sample and whatever the workspace has verified
    // itself (decided by the user on 2026-11-24). Two kinds, kept apart because the review explains them differently:
    //   Verified     proved by real software writes read back in a live workspace: the VerifiedOperations of the vCCA
    //                workspace, read on 2026-11-24 (records in docs/CAPABILITY-MATRIX.md).
    //   UserDecided  no software write evidence; opened by the user's decision. Every write is still read back and checked,
    //                and none of them counts as IndependentApiEvidence.
    // Anything else (in practice only Fleets:Create, which only an old job without the parameter requirement still makes)
    // keeps the sample until the workspace has verified it.
    static readonly IReadOnlyDictionary<string, SampleRelease> Released = new Dictionary<string, SampleRelease>(StringComparer.Ordinal)
    {
        // Verified (vCCA workspace, 2026-11-24)
        ["Airports:Create"] = SampleRelease.Verified,
        ["Airports:Update"] = SampleRelease.Verified,
        ["Airports:Delete"] = SampleRelease.Verified,
        ["Fleets:Create:ParameterIds"] = SampleRelease.Verified,
        ["Fleets:Update"] = SampleRelease.Verified,
        ["Fleets:Delete"] = SampleRelease.Verified,
        ["Aircraft:Create"] = SampleRelease.Verified,
        ["Aircraft:Update"] = SampleRelease.Verified,
        ["Aircraft:Delete"] = SampleRelease.Verified,
        ["Aircraft:Update:FleetId:Move"] = SampleRelease.Verified,
        ["Routings:Create"] = SampleRelease.Verified,
        ["Routings:Update"] = SampleRelease.Verified,
        ["Routings:Delete"] = SampleRelease.Verified,
        ["Routes:Create"] = SampleRelease.Verified,
        ["Routes:Create:Jumpseat"] = SampleRelease.Verified,
        ["Routes:Update"] = SampleRelease.Verified,
        ["Routes:Update:FleetIds:Scheduled"] = SampleRelease.Verified,
        ["Routes:Delete"] = SampleRelease.Verified,
        // UserDecided (2026-11-24): the non-scheduled route types and plain jumpseat updates
        ["Routes:Create:Cargo"] = SampleRelease.UserDecided,
        ["Routes:Create:Charter"] = SampleRelease.UserDecided,
        ["Routes:Create:Training"] = SampleRelease.UserDecided,
        ["Routes:Create:Vfr"] = SampleRelease.UserDecided,
        ["Routes:Create:Repositioning"] = SampleRelease.UserDecided,
        ["Routes:Update:Cargo"] = SampleRelease.UserDecided,
        ["Routes:Update:Charter"] = SampleRelease.UserDecided,
        ["Routes:Update:Training"] = SampleRelease.UserDecided,
        ["Routes:Update:Vfr"] = SampleRelease.UserDecided,
        ["Routes:Update:Repositioning"] = SampleRelease.UserDecided,
        ["Routes:Update:Jumpseat"] = SampleRelease.UserDecided,
        ["Routes:Update:FleetIds:Cargo"] = SampleRelease.UserDecided,
        ["Routes:Update:FleetIds:Charter"] = SampleRelease.UserDecided,
        ["Routes:Update:FleetIds:Training"] = SampleRelease.UserDecided,
        ["Routes:Update:FleetIds:Vfr"] = SampleRelease.UserDecided,
        ["Routes:Update:FleetIds:Repositioning"] = SampleRelease.UserDecided,
    };
    public static SampleRelease ReleaseOf(string key) => Released.GetValueOrDefault(key, SampleRelease.Sampled);
    // The default key (resource, a colon, kind) for every defined pair, built once: the review asks for one key per change.
    static readonly string[,] PlainKeys = BuildPlainKeys();
    static string[,] BuildPlainKeys()
    {
        var keys = new string[(int)Enum.GetValues<ResourceKind>().Max() + 1, (int)Enum.GetValues<ChangeKind>().Max() + 1];
        foreach (var resource in Enum.GetValues<ResourceKind>())
            foreach (var kind in Enum.GetValues<ChangeKind>())
                keys[(int)resource, (int)kind] = string.Concat(resource.ToString(), ":", kind.ToString());
        return keys;
    }
    static string PlainKey(ResourceKind resource, ChangeKind kind)
        => (uint)resource < PlainKeys.GetLength(0) && (uint)kind < PlainKeys.GetLength(1) ? PlainKeys[(int)resource, (int)kind] : string.Concat(resource.ToString(), ":", kind.ToString());
    public static string JumpseatSignature(DataRow row) => "jumpseat:" + JsonSerializer.Serialize(new[] { row.Get("Departure Time (HH:MM)"), row.Get("Arrival Time (HH:MM)"), row.Get("Start Date"), row.Get("End Date") });
    public static string FieldLabel(string field, ILocalizationService language) => field switch
    {
        "Freight" => language.Get("FreightKg"),
        "Max Freight" => language.Get("MaxFreightKg"),
        "Parameter IDs" => language.Get("AirlineParameter"),
        _ => field
    };
}
