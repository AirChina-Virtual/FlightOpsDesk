using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class WorkspaceViewModelTests
{
    static DataRow Row(string name) => new() { Fields = new Dictionary<string, string> { ["Name"] = name } };

    [Fact]
    public void There_is_one_card_per_resource_in_the_workspace_order()
    {
        var workspace = new Workspace();
        var model = new WorkspaceViewModel(workspace, new LocalizationService(), _ => 0);
        Assert.Equal(workspace.Resources.Keys, model.Cards.Select(c => c.Kind));
    }

    [Fact]
    public void A_card_counts_the_drafts_and_asks_for_the_change_count_only_when_its_summary_is_shown()
    {
        var workspace = new Workspace();
        var fleets = workspace.Resources[ResourceKind.Fleets];
        fleets.Draft.Add(Row("a"));
        fleets.Draft.Add(Row("b"));
        var asked = new List<ResourceData>();
        var language = new LocalizationService();
        language.SetLanguage("en-US");
        var model = new WorkspaceViewModel(workspace, language, data => { asked.Add(data); return 1; });
        Assert.Empty(asked);
        var card = model.Cards.Single(c => c.Kind == ResourceKind.Fleets);
        Assert.Equal(language.Get("Text_B9B43D4DAA", ("arg0", 2), ("arg1", 1)), card.Summary);
        Assert.Equal([fleets], asked);
        // The resource name is a message: translated, never looked up as a key (which would show "[机队]").
        Assert.Equal("Fleets", card.Title);
        Assert.All(model.Cards, c => Assert.DoesNotContain("[", c.Title, StringComparison.Ordinal));
    }

    [Fact]
    public void A_language_refresh_translates_the_page_and_every_card()
    {
        var language = new LocalizationService();
        var model = new WorkspaceViewModel(new Workspace(), language, _ => 0);
        var chinese = model.NewWorkspaceLabel;
        var pageChanges = new List<string?>();
        var cardChanges = new List<string?>();
        model.PropertyChanged += (_, e) => pageChanges.Add(e.PropertyName);
        model.Cards[0].PropertyChanged += (_, e) => cardChanges.Add(e.PropertyName);
        language.SetLanguage("en-US");
        model.RefreshLanguage();
        Assert.NotEqual(chinese, model.NewWorkspaceLabel);
        Assert.Equal("Create demo workspace", model.DemoWorkspaceLabel);
        Assert.Equal([nameof(WorkspaceViewModel.Intro), nameof(WorkspaceViewModel.NewWorkspaceLabel), nameof(WorkspaceViewModel.DemoWorkspaceLabel), nameof(WorkspaceViewModel.StorageNote),
            nameof(WorkspaceViewModel.RecordsLabel), nameof(WorkspaceViewModel.PendingLabel), nameof(WorkspaceViewModel.TasksLabel),
            nameof(WorkspaceViewModel.NextTitle), nameof(WorkspaceViewModel.NextText), nameof(WorkspaceViewModel.NextAction)], pageChanges);
        Assert.Equal([nameof(ResourceCardViewModel.Title), nameof(ResourceCardViewModel.Summary), nameof(ResourceCardViewModel.OpenLabel)], cardChanges);
    }

    [Theory]
    [InlineData(RunMode.Offline, 0, ShellPage.Settings)]
    [InlineData(RunMode.Offline, 3, ShellPage.Review)]
    [InlineData(RunMode.Online, 3, ShellPage.Review)]
    [InlineData(RunMode.Online, 0, ShellPage.Data)]
    [InlineData(RunMode.Demo, 0, ShellPage.Data)]
    public void The_next_step_follows_the_workspace(RunMode mode, int changes, ShellPage expected)
    {
        var workspace = new Workspace { Mode = mode };
        var pending = workspace.Resources[ResourceKind.Fleets];
        var model = new WorkspaceViewModel(workspace, new LocalizationService(), data => ReferenceEquals(data, pending) ? changes : 0);
        Assert.Equal(expected, model.NextPage);
        Assert.DoesNotContain("[", model.NextText, StringComparison.Ordinal);
        Assert.DoesNotContain("[", model.NextAction, StringComparison.Ordinal);
    }

    [Fact]
    public void Unfinished_remote_writes_come_before_new_changes()
    {
        var workspace = new Workspace { Mode = RunMode.Online };
        workspace.Jobs.Add(new BatchJob { Mode = RunMode.Online, Items = [new ChangeItem { State = ItemState.Unknown }] });
        Assert.Equal(ShellPage.Tasks, new WorkspaceViewModel(workspace, new LocalizationService(), _ => 5).NextPage);
    }
}
