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
        Assert.Equal(language.Get(Schemas.All[ResourceKind.Fleets].Name), card.Title);
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
        Assert.Equal([nameof(WorkspaceViewModel.Intro), nameof(WorkspaceViewModel.NewWorkspaceLabel), nameof(WorkspaceViewModel.DemoWorkspaceLabel), nameof(WorkspaceViewModel.StorageNote)], pageChanges);
        Assert.Equal([nameof(ResourceCardViewModel.Title), nameof(ResourceCardViewModel.Summary), nameof(ResourceCardViewModel.OpenLabel)], cardChanges);
    }
}
