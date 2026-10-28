using CommunityToolkit.Mvvm.ComponentModel;
using VamSys.Core;

namespace VamSys.Presentation;

// One resource of the workspace page: its name, how many rows it has and how many of them changed.
// The change count is asked for when the summary is shown, so the window's cached count is used and not recomputed.
public sealed class ResourceCardViewModel(ResourceKind kind, ResourceData data, ILocalizationService language, Func<ResourceData, int> countChanges) : ObservableObject
{
    public ResourceKind Kind { get; } = kind;
    public string Title => language.Get(Schemas.All[Kind].Name);
    public string Summary => language.Get("Text_B9B43D4DAA", ("arg0", data.Draft.Count), ("arg1", countChanges(data)));
    public string OpenLabel => language.Get("Text_C771248E51");

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(OpenLabel));
    }
}

// The workspace page: creating a workspace, and one card per resource in the workspace's own order.
// The page is rebuilt on every render; the window refreshes the shown one when the language changes.
public sealed class WorkspaceViewModel : ObservableObject
{
    readonly ILocalizationService language;

    public WorkspaceViewModel(Workspace workspace, ILocalizationService language, Func<ResourceData, int> countChanges)
    {
        this.language = language;
        Cards = workspace.Resources.Select(p => new ResourceCardViewModel(p.Key, p.Value, language, countChanges)).ToList();
    }

    public IReadOnlyList<ResourceCardViewModel> Cards { get; }
    public string Intro => language.Get("Text_2C33D5F7DE");
    public string NewWorkspaceLabel => language.Get("Text_946897D107");
    public string DemoWorkspaceLabel => language.Get("Text_57D4E0340D");
    public string StorageNote => language.Get("Text_8B0B7AD880");

    public void RefreshLanguage()
    {
        foreach (var name in new[] { nameof(Intro), nameof(NewWorkspaceLabel), nameof(DemoWorkspaceLabel), nameof(StorageNote) })
            OnPropertyChanged(name);
        foreach (var card in Cards)
            card.RefreshLanguage();
    }
}
