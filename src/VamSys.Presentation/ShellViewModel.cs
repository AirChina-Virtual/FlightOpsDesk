using CommunityToolkit.Mvvm.ComponentModel;
using VamSys.Core;

namespace VamSys.Presentation;

// The pages of the navigation pane, in pane order.
public enum ShellPage { Workspace, Data, Import, Review, Tasks, Settings }

public static class ShellPages
{
    static readonly string[] Tags = ["workspace", "data", "import", "review", "tasks", "settings"];
    static readonly string[] TitleKeys = ["Text_3797982942", "Text_9BB882C622", "Text_D52C17622D", "Text_10CCC8AAA2", "Text_00B514C36A", "Text_C2B958136D"];

    public static IReadOnlyList<ShellPage> All { get; } = Enum.GetValues<ShellPage>();
    // The tag a navigation item carries; an unknown or missing tag means the workspace page.
    public static string Tag(this ShellPage page) => Tags[(int)page];
    public static ShellPage FromTag(string? tag) => Array.IndexOf(Tags, tag) is var n and >= 0 ? (ShellPage)n : ShellPage.Workspace;
    // The page title, which is also the navigation item's label.
    public static string TitleKey(this ShellPage page) => TitleKeys[(int)page];
    public static int PaneIndex(this ShellPage page) => (int)page;
}

// The window shell: which page is shown and what the title bar, page header and resource bar say about it.
// Every page change goes through the interaction gate; the submission hand-over has its own method because it must
// reach the task page while its local operation still holds the lock.
public sealed class ShellViewModel(InteractionGate gate) : ObservableObject
{
    ShellPage page = ShellPage.Workspace;

    public ShellPage Page => page;
    public string PageTitleKey => page.TitleKey();
    // The resource picker and snapshot label belong to the pages that work on one resource.
    public bool ShowsResourceBar => page is ShellPage.Data or ShellPage.Import;

    public static string ModeBadgeKey(RunMode mode) => mode switch
    {
        RunMode.Demo => "Text_692F811146",
        RunMode.Online => "Text_C4C1F8C6F4",
        _ => "Text_6C352EE0B0"
    };

    // A page change the person asked for (pane selection or a command). Ignored while a local operation holds the lock.
    public bool TryNavigate(ShellPage target)
    {
        if (!gate.CanNavigate)
            return false;
        Set(target);
        return true;
    }

    // Submission hand-over: called while the submission's local operation still holds the lock, so the pane selection
    // that follows is ignored by TryNavigate. The page is switched here, before the unlock, so the final render shows
    // the task page and cannot rebuild the review page.
    public void ShowTasksBeforeUnlock() => Set(ShellPage.Tasks);

    void Set(ShellPage target)
    {
        if (page == target)
            return;
        page = target;
        OnPropertyChanged(nameof(Page));
        OnPropertyChanged(nameof(PageTitleKey));
        OnPropertyChanged(nameof(ShowsResourceBar));
    }
}
