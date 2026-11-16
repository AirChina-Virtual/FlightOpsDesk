using VamSys.Core;

namespace VamSys.Presentation;

// What a data table cell shows and whether it may be edited, independent of the controls that show it.
public static class DataGridRules
{
    // Deleted rows are read-only. Online, a row the server knows keeps its airline parameter, and a jumpseat route keeps
    // its callsign and flight number, which the API does not preserve. A jumpseat has no fleets (the server drops them):
    // its empty fleet cell cannot be filled; one that still holds fleets (pasted, imported) stays editable to be cleared.
    // The same goes for the other cells the server does not keep for a jumpseat (JumpseatBlank).
    public static bool CellReadOnly(DataRow row, string field, RunMode mode) => Schemas.Deleted(row) || JumpseatBlank(row, field)
        || mode == RunMode.Online && row.Identity != null
        && (field == "Parameter IDs" || row.Get("Type") == "jumpseat" && (field is "Callsign" or "Flight Number"));

    public static bool JumpseatFleets(DataRow row, string field) => field == "Fleet IDs" && row.Get("Type") == "jumpseat" && row.Get(field) == "";

    // The empty cells of a jumpseat that stay empty: fleets, callsign and flight number are never kept; remarks are not
    // kept from the create (2026-11-16, route 14327585), so a new jumpseat's are left out too.
    public static bool JumpseatBlank(DataRow row, string field) => row.Get("Type") == "jumpseat" && row.Get(field) == ""
        && (field is "Fleet IDs" or "Callsign" or "Flight Number" || field == "Remarks" && row.Identity == null);

    // The status column: pending deletion, a conflict to review, new, an unsubmitted change, or unchanged.
    public static string RowStatus(DataRow row, IReadOnlyDictionary<Guid, DataRow> baseline, IReadOnlySet<Guid> conflicts, ILocalizationService language)
    {
        if (Schemas.Deleted(row))
            return language.Get("Text_EE96DC1EBE");
        if (conflicts.Contains(row.LocalId))
            return language.Get("ApiRowConflict");
        if (!baseline.TryGetValue(row.LocalId, out var before))
            return language.Get("Text_4EF7F62706");
        return language.Get(ChangePlanner.GetChangeKind(row, before) != null ? "ApiRowDraft" : "Text_20D2382F10");
    }

    // The fleet column: each referenced fleet by its description, an unknown one by its token, and a placeholder when empty.
    public static string FleetLabel(string value, IReadOnlyDictionary<string, FleetOption> labels, ILocalizationService language)
    {
        var label = string.Join("; ", FleetAssignmentService.Tokens(value).Select(t => labels.TryGetValue(t, out var option) ? option.Describe(language) : language.Get("Text_C221144914") + t));
        return label.Length > 0 ? label : language.Get("Text_F274578907");
    }
}
