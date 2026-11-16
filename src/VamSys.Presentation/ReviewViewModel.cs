using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VamSys.Core;

namespace VamSys.Presentation;

public static class ReviewTexts
{
    // One planned change as the review and the submission confirmation show it. It reads the reviewed rows directly:
    // showing the page does not build the full items. The first line names the record (kind, key and what it is),
    // the second what changes.
    public static string ChangeText(PlannedChange item, ILocalizationService language, ReviewSummaries? summaries = null)
        => $"{Headline(item, language, summaries)}\n" +
           (item.Kind == ChangeKind.Delete
               ? language.Get("Text_5ABD08A949")
               : string.Join("；", item.Fields().Select(f => $"{ApiOperationPolicy.FieldLabel(f.Key, language)}: {ReviewSummaries.Clip(item.Before?.Get(f.Key) ?? "∅")} → {ReviewSummaries.Clip(f.Value.Value)}")));

    // "Kind · key · summary"; a create has no key yet and a record with nothing to name it shows only its key.
    public static string Headline(PlannedChange item, ILocalizationService language, ReviewSummaries? summaries = null)
    {
        var text = language.Get("Enum_" + nameof(ChangeKind) + "_" + item.Kind);
        var key = Schemas.Key(item.Resource, item.After);
        if (key.Length > 0)
            text += " · " + key;
        var summary = (summaries ?? ReviewSummaries.Empty).Summary(item, language);
        return summary.Length > 0 ? text + " · " + summary : text;
    }
}

/// <summary>
/// What a reviewed record is, in one short line: an airport's name, a fleet's name and type code, an aircraft's registration,
/// name and fleet, a routing's airports and route string, a route's type, callsign, flight number and airports. A deletion
/// names the record being deleted (the snapshot row), anything else the row as it would be submitted. The fleet names an
/// aircraft refers to come from an index of the workspace's fleets built once, on the first aircraft.
/// </summary>
public sealed class ReviewSummaries(Workspace? workspace)
{
    public static readonly ReviewSummaries Empty = new(null);
    const int ValueLength = 40, SummaryLength = 120;
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";
    Dictionary<string, string>? fleets;

    public string Summary(ChangeItem item, ILocalizationService language) => Summary(PlannedChange.Wrap(item), language);

    public string Summary(PlannedChange item, ILocalizationService language)
    {
        var row = item.Kind == ChangeKind.Delete ? item.Before ?? item.After : item.After;
        var parts = new List<string>(4);
        switch (item.Resource)
        {
            case ResourceKind.Airports:
                Add(parts, row.Get("Name"));
                break;
            case ResourceKind.Fleets:
                var code = Clean(row.Get("Type Code"));
                Add(parts, Clean(row.Get("Name")) is { Length: > 0 } name ? code.Length > 0 ? $"{Clip(name)} ({code})" : name : code);
                break;
            case ResourceKind.Aircraft:
                Add(parts, row.Get("Registration"));
                Add(parts, row.Get("Name"));
                if (Clean(row.Get("Fleet ID")) is { Length: > 0 } fleet)
                    parts.Add(language.Get("ReviewSummaryFleet", ("fleet", FleetName(fleet))));
                break;
            case ResourceKind.Routings:
                Add(parts, Airports(row));
                Add(parts, row.Get("Route String"));
                break;
            case ResourceKind.Routes:
                Add(parts, row.Get("Type"));
                Add(parts, string.Join(" ", new[] { Clean(row.Get("Callsign")), Clean(row.Get("Flight Number")) }.Where(p => p.Length > 0)));
                Add(parts, Airports(row));
                break;
        }
        return Clip(string.Join(" · ", parts), SummaryLength);
    }

    static string Airports(DataRow row)
    {
        var (from, to) = (Clean(row.Get(Dep)), Clean(row.Get(Arr)));
        return from.Length == 0 && to.Length == 0 ? "" : $"{(from.Length > 0 ? from : "?")} → {(to.Length > 0 ? to : "?")}";
    }

    static void Add(List<string> parts, string value)
    {
        value = Clean(value);
        if (value.Length > 0)
            parts.Add(Clip(value));
    }

    // One line: line breaks and runs of white space become a single space.
    static string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        value = value.Trim();
        return value.AsSpan().IndexOfAny("\r\n\t") < 0 && !value.Contains("  ", StringComparison.Ordinal)
            ? value : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Shortens a long value to one compact piece ending with an ellipsis.</summary>
    public static string Clip(string? value, int length = ValueLength)
        => value is null || value.Length <= length ? value ?? "" : value[..(length - 1)].TrimEnd() + "…";

    // A fleet by its remote ID or by the local reference of a fleet not created yet; an unknown one shows the reference.
    string FleetName(string reference)
    {
        if (workspace is null)
            return Clip(reference);
        if (fleets is null)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (workspace.Resources.TryGetValue(ResourceKind.Fleets, out var data))
            {
                // Draft rows win over the snapshot: a renamed fleet shows its new name.
                foreach (var rows in new[] { data.Snapshot, data.Draft })
                    foreach (var fleet in rows)
                    {
                        var name = Clean(fleet.Get("Name"));
                        if (name.Length == 0)
                            continue;
                        if (Clean(fleet.Get("ID")) is { Length: > 0 } id)
                            index[id] = name;
                        index[LocalReference.Format(fleet.LocalId)] = name;
                    }
            }
            fleets = index;
        }
        return Clip(fleets.TryGetValue(reference, out var found) ? found : reference);
    }
}

// A line of the workspace review: either a resource heading with its CSV export, or one reviewed change.
public sealed class ReviewRowViewModel(ResourceKind resource, ReviewEntry? entry, ILocalizationService language, IAsyncRelayCommand? export,
    IReadOnlyList<string>? deniedFleets = null, IAsyncRelayCommand? removeFleets = null, Action? included = null, ReviewSummaries? summaries = null) : ObservableObject
{
    // 3.5: a heading's resource is part of this submission (all are by default).
    bool isIncluded = true;
    public bool IsIncluded { get => isIncluded; set { if (SetProperty(ref isIncluded, value)) included?.Invoke(); } }
    public bool ShowsInclude => included != null;
    public string IncludeLabel => language.Get("ReviewInclude", ("resource", Heading));
    public ResourceKind Resource { get; } = resource;
    public ReviewEntry? Entry { get; } = entry;
    public bool IsHeading => Entry is null;
    public string Heading => language.Format(Schemas.All[Resource].Name);
    public string ExportLabel => language.Get("ReviewExport", ("resource", Heading));
    public string State => Entry is null ? "" : language.Get("ReviewState_" + Entry.Disposition);
    public string Body => Entry is null ? "" : ReviewTexts.ChangeText(Entry.Planned, language, summaries);
    public string Reason => Entry?.Reason is null ? "" : language.Format(Entry.Reason);
    public IAsyncRelayCommand? ExportCommand { get; } = export;
    // 2.3: a route blocked only by fleets that are not approved for its prefixes offers to remove them (one undoable edit).
    public IReadOnlyList<string> DeniedFleets { get; } = deniedFleets ?? [];
    public bool ShowsRemoveFleets => RemoveFleetsCommand is not null && DeniedFleets.Count > 0;
    public string RemoveFleetsLabel => language.Get("ReviewRemoveFleets", ("count", DeniedFleets.Count));
    public IAsyncRelayCommand? RemoveFleetsCommand { get; } = removeFleets;

    public void RefreshLanguage()
    {
        foreach (var name in new[] { nameof(Heading), nameof(ExportLabel), nameof(State), nameof(Body), nameof(Reason), nameof(RemoveFleetsLabel), nameof(IncludeLabel) })
            OnPropertyChanged(name);
    }
}

// The review of the whole workspace. The page appears at once in its preparing state; the window builds the plan on a
// worker and shows it here. What may be pressed is decided when the plan is shown, as before.
public sealed class ReviewViewModel(Workspace workspace, ILocalizationService language) : ObservableObject
{
    WorkspaceReviewPlan? plan;
    int conflicts;

    public bool IsPreparing => plan is null;
    public bool IsShown => plan is not null;
    public string PreparingText => language.Get("ReviewPreparing");

    public IReadOnlyList<ReviewRowViewModel> Rows { get; private set; } = [];
    public bool CanSubmit { get; private set; }
    bool canEditShown;
    // 3.5: the resources ticked for this submission.
    public IReadOnlySet<ResourceKind> Chosen => Rows.Where(r => r.IsHeading && r.IsIncluded).Select(r => r.Resource).ToHashSet();
    void ChosenChanged()
    {
        CanSubmit = plan is not null && canEditShown && workspace.Mode != RunMode.Offline && plan.SubmitItemsFor(Chosen).Count > 0;
        OnPropertyChanged(nameof(CanSubmit));
    }
    public bool CanResolveConflicts { get; private set; }

    public string Title => language.Get("ReviewWorkspace");
    public string Target => language.Get("ApiTarget", ("va", workspace.Name), ("id", workspace.AirlineId ?? "?"));
    public string Counts => plan is null ? "" : language.Get("ReviewCounts", ("total", plan.Entries.Count), ("ready", plan.SubmitCount),
        ("deferred", plan.Entries.Count(e => e.Disposition == ReviewDisposition.Deferred)),
        ("blocked", plan.Entries.Count(e => e.Disposition == ReviewDisposition.Blocked)));
    public string Mode => language.Get("ReviewMode", ("mode", language.Get("Enum_" + nameof(RunMode) + "_" + workspace.Mode)));
    public bool ShowsConnectHint => workspace.Mode == RunMode.Offline;
    public string ConnectHint => language.Get("ReviewConnect");
    // The first three blockers are shown on the page; all of them are listed behind the issues button.
    // 3.1: the issues (conflicts, open task items, validation) block only their own entries; they are still listed here.
    public IReadOnlyList<string> ShownBlockers => plan is null ? [] : plan.Issues.Take(3).Select(language.Format).ToList();
    public bool ShowsBlockers => plan?.Issues.Count > 0;
    public IReadOnlyList<string> AllBlockers => plan is null ? [] : plan.Issues.Select(language.Format).ToList();
    public bool ShowsIssues => plan?.Issues.Count > 3;
    public string IssuesLabel => language.Get("ReviewIssues", ("count", plan?.Issues.Count ?? 0));
    public string SubmitLabel => language.Get(workspace.Mode == RunMode.Demo ? "ReviewDemoSubmit" : "ReviewSubmit");
    public string ConflictsLabel => language.Get("ApiConflicts", ("count", conflicts));
    public bool ShowsNothingToReview => plan is not null && Rows.Count == 0;
    public string NothingToReview => language.Get("Text_90484F47B3");

    // Fills the page: a heading per resource with changes, in resource order, followed by its changes.
    // deniedFleets / removeFleets (2.3): the unapproved fleets of a blocked route, and the action that removes them.
    public void Show(WorkspaceReviewPlan shown, bool canEdit, bool canResolveConflicts, Func<ResourceKind, Task> export,
        Func<ReviewEntry, IReadOnlyList<string>>? deniedFleets = null, Func<ReviewEntry, IReadOnlyList<string>, Task>? removeFleets = null)
    {
        plan = shown;
        conflicts = workspace.Resources.Sum(p => p.Value.Conflicts.Count);
        canEditShown = canEdit;
        CanSubmit = canEdit && shown.SubmitCount > 0 && workspace.Mode != RunMode.Offline;
        CanResolveConflicts = canResolveConflicts && conflicts > 0;
        var rows = new List<ReviewRowViewModel>();
        var summaries = new ReviewSummaries(workspace);
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var entries = shown.Entries.Where(e => e.Planned.Resource == kind).ToArray();
            if (entries.Length == 0)
                continue;
            var resource = kind;
            // Only a resource with something ready to send can be left out of the submission.
            var ready = entries.Any(e => e.Disposition == ReviewDisposition.Ready);
            rows.Add(new(kind, null, language, new AsyncRelayCommand(() => export(resource)), included: ready && canEdit ? ChosenChanged : null));
            foreach (var entry in entries)
            {
                var denied = canEdit && removeFleets != null && deniedFleets != null && IsPrefixBlock(entry) ? deniedFleets(entry) : [];
                rows.Add(new ReviewRowViewModel(kind, entry, language, null, denied,
                    denied.Count > 0 ? new AsyncRelayCommand(() => removeFleets!(entry, denied)) : null, summaries: summaries));
            }
        }
        Rows = rows;
        OnPropertyChanged(string.Empty);
    }

    static bool IsPrefixBlock(ReviewEntry entry) => entry.Disposition == ReviewDisposition.Blocked && entry.Planned.Resource == ResourceKind.Routes
        && entry.Reason?.Code is "ApiPrefixDenied" or "ApiPrefixDeniedFleets";

    static readonly string[] Texts = [nameof(PreparingText), nameof(Title), nameof(Target), nameof(Counts), nameof(Mode), nameof(ConnectHint),
        nameof(ShownBlockers), nameof(IssuesLabel), nameof(SubmitLabel), nameof(ConflictsLabel), nameof(NothingToReview)];

    public void RefreshLanguage()
    {
        foreach (var name in Texts)
            OnPropertyChanged(name);
        foreach (var row in Rows)
            row.RefreshLanguage();
    }
}
