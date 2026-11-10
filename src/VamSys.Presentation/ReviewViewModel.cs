using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VamSys.Core;

namespace VamSys.Presentation;

public static class ReviewTexts
{
    // One planned change as the review and the submission confirmation show it. It reads the reviewed rows directly:
    // showing the page does not build the full items.
    public static string ChangeText(PlannedChange item, ILocalizationService language)
        => $"{language.Get("Enum_" + nameof(ChangeKind) + "_" + item.Kind)} · {Schemas.Key(item.Resource, item.After)}\n" +
           (item.Kind == ChangeKind.Delete
               ? language.Get("Text_5ABD08A949")
               : string.Join("；", item.Fields().Select(f => $"{ApiOperationPolicy.FieldLabel(f.Key, language)}: {item.Before?.Get(f.Key) ?? "∅"} → {f.Value.Value}")));
}

// A line of the workspace review: either a resource heading with its CSV export, or one reviewed change.
public sealed class ReviewRowViewModel(ResourceKind resource, ReviewEntry? entry, ILocalizationService language, IAsyncRelayCommand? export,
    IReadOnlyList<string>? deniedFleets = null, IAsyncRelayCommand? removeFleets = null) : ObservableObject
{
    public ResourceKind Resource { get; } = resource;
    public ReviewEntry? Entry { get; } = entry;
    public bool IsHeading => Entry is null;
    public string Heading => language.Format(Schemas.All[Resource].Name);
    public string ExportLabel => language.Get("ReviewExport", ("resource", Heading));
    public string State => Entry is null ? "" : language.Get("ReviewState_" + Entry.Disposition);
    public string Body => Entry is null ? "" : ReviewTexts.ChangeText(Entry.Planned, language);
    public string Reason => Entry?.Reason is null ? "" : language.Format(Entry.Reason);
    public IAsyncRelayCommand? ExportCommand { get; } = export;
    // 2.3: a route blocked only by fleets that are not approved for its prefixes offers to remove them (one undoable edit).
    public IReadOnlyList<string> DeniedFleets { get; } = deniedFleets ?? [];
    public bool ShowsRemoveFleets => RemoveFleetsCommand is not null && DeniedFleets.Count > 0;
    public string RemoveFleetsLabel => language.Get("ReviewRemoveFleets", ("count", DeniedFleets.Count));
    public IAsyncRelayCommand? RemoveFleetsCommand { get; } = removeFleets;

    public void RefreshLanguage()
    {
        foreach (var name in new[] { nameof(Heading), nameof(ExportLabel), nameof(State), nameof(Body), nameof(Reason), nameof(RemoveFleetsLabel) })
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
    public IReadOnlyList<string> ShownBlockers => plan is null ? [] : plan.Blockers.Take(3).Select(language.Format).ToList();
    public bool ShowsBlockers => plan?.Blockers.Count > 0;
    public IReadOnlyList<string> AllBlockers => plan is null ? [] : plan.Blockers.Select(language.Format).ToList();
    public bool ShowsIssues => plan?.Blockers.Count > 3;
    public string IssuesLabel => language.Get("ReviewIssues", ("count", plan?.Blockers.Count ?? 0));
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
        CanSubmit = canEdit && shown.SubmitCount > 0 && workspace.Mode != RunMode.Offline;
        CanResolveConflicts = canResolveConflicts && conflicts > 0;
        var rows = new List<ReviewRowViewModel>();
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var entries = shown.Entries.Where(e => e.Planned.Resource == kind).ToArray();
            if (entries.Length == 0)
                continue;
            var resource = kind;
            rows.Add(new(kind, null, language, new AsyncRelayCommand(() => export(resource))));
            foreach (var entry in entries)
            {
                var denied = canEdit && removeFleets != null && deniedFleets != null && IsPrefixBlock(entry) ? deniedFleets(entry) : [];
                rows.Add(new ReviewRowViewModel(kind, entry, language, null, denied,
                    denied.Count > 0 ? new AsyncRelayCommand(() => removeFleets!(entry, denied)) : null));
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
