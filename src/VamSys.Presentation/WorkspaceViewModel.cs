using CommunityToolkit.Mvvm.ComponentModel;
using VamSys.Core;

namespace VamSys.Presentation;

// One resource's card on the workspace page. It reads the resource when asked, so the page does not count changes
// before it shows them.
public sealed class ResourceCardViewModel(ResourceKind kind, ResourceData data, ILocalizationService language, Func<ResourceData, int> countChanges) : ObservableObject
{
    public ResourceKind Kind { get; } = kind;
    public string Title => language.Format(Schemas.All[Kind].Name);
    public string Summary => language.Get("Text_B9B43D4DAA", ("arg0", data.Draft.Count), ("arg1", countChanges(data)));
    public string OpenLabel => language.Get("Text_C771248E51");

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(OpenLabel));
    }
}

// The workspace page: what to do next, the workspace in numbers, one card per resource in the workspace's own order,
// and creating a workspace. The page is rebuilt on every render; the window refreshes the shown one when the language changes.
public sealed class WorkspaceViewModel : ObservableObject
{
    readonly ILocalizationService language;
    readonly Workspace workspace;
    readonly Func<ResourceData, int> countChanges;

    public WorkspaceViewModel(Workspace workspace, ILocalizationService language, Func<ResourceData, int> countChanges)
    {
        this.language = language;
        this.workspace = workspace;
        this.countChanges = countChanges;
        Cards = workspace.Resources.Select(p => new ResourceCardViewModel(p.Key, p.Value, language, countChanges)).ToList();
    }

    public IReadOnlyList<ResourceCardViewModel> Cards { get; }
    public string Intro => language.Get("Text_2C33D5F7DE");
    public string NewWorkspaceLabel => language.Get("Text_946897D107");
    public string DemoWorkspaceLabel => language.Get("Text_57D4E0340D");
    public string StorageNote => language.Get("Text_8B0B7AD880");

    public string Records => workspace.Resources.Values.Sum(d => d.Draft.Count).ToString("N0", language.DisplayCulture);
    public string Pending => PendingCount.ToString("N0", language.DisplayCulture);
    public string Tasks => workspace.Jobs.Count.ToString("N0", language.DisplayCulture);
    public string RecordsLabel => language.Get("OverviewRecords");
    public string PendingLabel => language.Get("OverviewPending");
    public string TasksLabel => language.Get("OverviewTasks");

    int PendingCount => workspace.Resources.Values.Sum(countChanges);

    // Unfinished remote writes come first: nothing new should be submitted before they are checked. Then the changes
    // waiting for review (the review page says when they need a connection), then connecting, then the data itself.
    public ShellPage NextPage => ApiOperationPolicy.HasOpenOnlineItems(workspace) ? ShellPage.Tasks
        : PendingCount > 0 ? ShellPage.Review
        : workspace.Mode == RunMode.Offline ? ShellPage.Settings
        : ShellPage.Data;
    public string NextTitle => language.Get("OverviewNextTitle");
    public string NextText => NextPage switch
    {
        ShellPage.Tasks => language.Get("NextStepTasks"),
        ShellPage.Review => language.Get("NextStepReview", ("count", PendingCount)),
        ShellPage.Settings => language.Get("NextStepConnect"),
        _ => language.Get("NextStepData")
    };
    public string NextAction => language.Get(NextPage switch
    {
        ShellPage.Tasks => "NextStepTasksAction",
        ShellPage.Review => "NextStepReviewAction",
        ShellPage.Settings => "NextStepConnectAction",
        _ => "NextStepDataAction"
    });

    public void RefreshLanguage()
    {
        foreach (var name in new[] { nameof(Intro), nameof(NewWorkspaceLabel), nameof(DemoWorkspaceLabel), nameof(StorageNote), nameof(RecordsLabel), nameof(PendingLabel), nameof(TasksLabel), nameof(NextTitle), nameof(NextText), nameof(NextAction) })
            OnPropertyChanged(name);
        foreach (var card in Cards)
            card.RefreshLanguage();
    }
}
