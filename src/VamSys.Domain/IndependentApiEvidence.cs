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
        if (contractHash != ContractHash)
            return false;
        if (!Enum.IsDefined(item.Resource))
            return false;
        var type = (item.Before ?? item.After).Get("Type");
        if (item.Resource == ResourceKind.Routes && type is not ("scheduled" or "jumpseat"))
            return false;
        if (item.Resource == ResourceKind.Fleets && item.Kind == ChangeKind.Create && item.After.Get("Type (pax/cargo/...)") is not ("pax-cargo" or "cargo"))
            return false;
        if (item.Kind == ChangeKind.Delete)
            return true;
        if (item.Kind is not (ChangeKind.Create or ChangeKind.Update))
            return false;
        // Which fields were exercised per operation is the Evidence column of the field table.
        var move = IsMove(item);
        foreach (var p in item.Fields)
            if (p.Value.Intent != FieldIntent.Unspecified && p.Key != "_delete" && !(item.Kind == ChangeKind.Create && string.IsNullOrEmpty(p.Value.Value))
                && !FieldSpecs.InEvidence(item.Resource, p.Key, item.Kind, move, type))
                return false;
        return true;
    }
}

public sealed record ApiDeletionVerification(string Outcome, DateTimeOffset CheckedAt, int? WriteStatus, int? ReadStatus, bool WasManuallyConfirmed = false);
// Records how a user resolved an Unknown item by hand. It never counts as API verification.
// Source: AcceptServerValue (accepted write, read-back differs) or SoftDeleteWithoutReceipt (no write receipt, GET 404).
public sealed record ApiManualResolution(string Source, DateTimeOffset ConfirmedAt, int? ReadStatus, bool WriteReceipt);
