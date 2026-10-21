namespace VamSys.Core;

// Release policy derived from the isolated vCCA tests. This is not a workspace
// success marker and never changes saved jobs. Unlisted variants retain sampling.
public static class IndependentApiEvidence
{
    public const string ContractHash = "1EC1A869FC3C6F4D70A7A6ECD50170D5D915551794E796F9F34D5AFB944D78C2";
    public const string EvidenceId = "vcca-isolated-20261020";
    public static bool IsMove(ChangeItem item) => item.Resource == ResourceKind.Aircraft && item.Kind == ChangeKind.Update && item.Fields.ContainsKey("Fleet ID");
    public static bool IsSoftDelete(ChangeItem item) => item.Kind == ChangeKind.Delete && item.Resource is ResourceKind.Airports or ResourceKind.Routes;
    public static bool CanCheckDeletion(ChangeItem item) => IsSoftDelete(item) && item.State is ItemState.Unknown or ItemState.ManuallyConfirmed;

    public static bool Covers(ChangeItem item, string? contractHash)
    {
        if (contractHash != ContractHash) return false;
        if (!Enum.IsDefined(item.Resource)) return false;
        var type = (item.Before ?? item.After).Get("Type");
        if (item.Resource == ResourceKind.Routes && type is not ("scheduled" or "jumpseat")) return false;
        if(item.Resource==ResourceKind.Fleets&&item.Kind==ChangeKind.Create&&item.After.Get("Type (pax/cargo/...)") is not ("pax-cargo" or "cargo"))return false;
        if (item.Kind == ChangeKind.Delete) return true;
        if (item.Kind is not (ChangeKind.Create or ChangeKind.Update)) return false;
        string[] allowed = (item.Resource, item.Kind) switch
        {
            (ResourceKind.Airports, ChangeKind.Create) => ["ICAO/IATA", "Name"],
            (ResourceKind.Airports, ChangeKind.Update) => ["Name"],
            (ResourceKind.Fleets, ChangeKind.Create) => ["Name", "Type Code", "Type (pax/cargo/...)", "Max Passengers", "Max Freight", "Hide in Phoenix", "Parameter IDs"],
            (ResourceKind.Fleets, ChangeKind.Update) => ["Name", "Max Passengers", "Max Freight"],
            (ResourceKind.Aircraft, ChangeKind.Create) => ["Fleet ID", "Name", "Registration", "Passengers", "Freight", "Internal Remarks"],
            (ResourceKind.Aircraft, ChangeKind.Update) when IsMove(item) => ["Fleet ID"],
            (ResourceKind.Aircraft, ChangeKind.Update) => ["Name", "Freight"],
            (ResourceKind.Routings, ChangeKind.Create) => ["Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)", "Route String", "Remarks"],
            (ResourceKind.Routings, ChangeKind.Update) => ["Remarks"],
            (ResourceKind.Routes, ChangeKind.Create) => ["Type", "Callsign", "Flight Number", "Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)",
                "Departure Time (HH:MM)", "Arrival Time (HH:MM)", "Routing", "Is Hidden", "Start Date", "End Date", "Remarks", "Internal Remarks", "Service Days"],
            (ResourceKind.Routes, ChangeKind.Update) when type == "scheduled" => ["Remarks", "Cost Index", "Departure Time (HH:MM)", "Arrival Time (HH:MM)", "Fleet IDs"],
            (ResourceKind.Routes, ChangeKind.Update) => ["Remarks", "Cost Index", "Departure Time (HH:MM)", "Arrival Time (HH:MM)"],
            _ => []
        };
        if(item.Resource==ResourceKind.Routes&&item.Kind==ChangeKind.Create&&type=="jumpseat")allowed=[..allowed,"Cost Index"];
        if(item.Resource==ResourceKind.Routes&&item.Kind==ChangeKind.Create&&type=="scheduled")allowed=[..allowed,"Fleet IDs"];
        return item.Fields.Where(p => p.Value.Intent != FieldIntent.Unspecified && p.Key != "_delete" && !(item.Kind == ChangeKind.Create && string.IsNullOrEmpty(p.Value.Value)))
            .All(p => allowed.Contains(p.Key, StringComparer.Ordinal));
    }
}

public sealed record ApiDeletionVerification(string Outcome, DateTimeOffset CheckedAt, int? WriteStatus, int? ReadStatus, bool WasManuallyConfirmed = false);
// Records how a user resolved an Unknown item by hand. It never counts as API verification.
// Source: AcceptServerValue (accepted write, read-back differs) or SoftDeleteWithoutReceipt (no write receipt, GET 404).
public sealed record ApiManualResolution(string Source, DateTimeOffset ConfirmedAt, int? ReadStatus, bool WriteReceipt);
