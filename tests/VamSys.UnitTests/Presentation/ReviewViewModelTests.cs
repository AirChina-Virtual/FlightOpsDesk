using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class ReviewViewModelTests
{
    static DataRow Row(params (string, string)[] fields) => new() { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };

    static ReviewEntry Entry(ResourceKind resource, ReviewDisposition disposition = ReviewDisposition.Ready, MessageDescriptor? reason = null)
        => new(PlannedChange.Wrap(new ChangeItem { Resource = resource, Kind = ChangeKind.Update, Before = Row(("Name", "old")), After = Row(("Name", "new")) }), disposition, reason);

    static (ReviewViewModel Model, List<ResourceKind> Exported, LocalizationService Language) Shown(Workspace workspace, WorkspaceReviewPlan plan, bool canEdit = true, bool canResolve = true)
    {
        var language = new LocalizationService();
        language.SetLanguage("en-US");
        var exported = new List<ResourceKind>();
        var model = new ReviewViewModel(workspace, language);
        model.Show(plan, canEdit, canResolve, kind => { exported.Add(kind); return Task.CompletedTask; });
        return (model, exported, language);
    }

    [Fact]
    public void The_page_prepares_until_the_plan_is_shown()
    {
        var model = new ReviewViewModel(new Workspace(), new LocalizationService());
        Assert.True(model.IsPreparing);
        Assert.False(model.IsShown);
        Assert.Empty(model.Rows);
        Assert.False(model.ShowsNothingToReview);
    }

    [Fact]
    public async Task Each_resource_with_changes_gets_a_heading_with_its_export_followed_by_its_changes_in_resource_order()
    {
        var plan = new WorkspaceReviewPlan([Entry(ResourceKind.Routes), Entry(ResourceKind.Fleets), Entry(ResourceKind.Routes)], []);
        var (model, exported, _) = Shown(new Workspace { Mode = RunMode.Demo }, plan);
        Assert.True(model.IsShown);
        var order = Enum.GetValues<ResourceKind>();
        var expected = new[] { ResourceKind.Fleets, ResourceKind.Routes }.OrderBy(k => Array.IndexOf(order, k)).ToArray();
        Assert.Equal(expected[0], model.Rows[0].Resource);
        Assert.True(model.Rows[0].IsHeading);
        Assert.Equal([false, true, false, false], model.Rows.Skip(1).Select(r => r.IsHeading));
        Assert.All(model.Rows.Where(r => !r.IsHeading), r => Assert.Null(r.ExportCommand));
        foreach (var heading in model.Rows.Where(r => r.IsHeading))
            await heading.ExportCommand!.ExecuteAsync(null);
        Assert.Equal(expected, exported);
        // The heading is the translated resource name, so the export button reads "Export Routes CSV".
        var routes = model.Rows.Single(r => r.IsHeading && r.Resource == ResourceKind.Routes);
        Assert.Equal("Routes", routes.Heading);
        Assert.Equal("Export Routes CSV", routes.ExportLabel);
    }

    [Theory]
    [InlineData(RunMode.Demo, true, true)]
    [InlineData(RunMode.Online, true, true)]
    [InlineData(RunMode.Offline, true, false)]
    [InlineData(RunMode.Demo, false, false)]
    public void Submission_needs_an_editable_window_ready_changes_and_a_mode_that_can_send(RunMode mode, bool canEdit, bool expected)
    {
        var (model, _, _) = Shown(new Workspace { Mode = mode }, new WorkspaceReviewPlan([Entry(ResourceKind.Fleets)], []), canEdit);
        Assert.Equal(expected, model.CanSubmit);
        Assert.Equal(mode == RunMode.Offline, model.ShowsConnectHint);
        Assert.Equal(mode == RunMode.Demo ? "Simulate workspace submission" : "Submit workspace changes", model.SubmitLabel);
    }

    [Fact]
    public void Nothing_ready_or_any_blocker_means_nothing_can_be_submitted()
    {
        var workspace = new Workspace { Mode = RunMode.Demo };
        Assert.False(Shown(workspace, new WorkspaceReviewPlan([Entry(ResourceKind.Fleets, ReviewDisposition.Deferred)], [])).Model.CanSubmit);
        Assert.False(Shown(workspace, new WorkspaceReviewPlan([Entry(ResourceKind.Fleets)], [Messages.Define("ReviewConnect")])).Model.CanSubmit);
    }

    [Fact]
    public void Three_blockers_are_shown_on_the_page_and_the_rest_behind_the_issues_button()
    {
        var blockers = Enumerable.Range(0, 5).Select(_ => Messages.Define("ReviewConnect")).ToList();
        var (model, _, language) = Shown(new Workspace(), new WorkspaceReviewPlan([], blockers));
        Assert.Equal(3, model.ShownBlockers.Count);
        Assert.Equal(5, model.AllBlockers.Count);
        Assert.True(model.ShowsBlockers);
        Assert.True(model.ShowsIssues);
        Assert.Equal(language.Get("ReviewIssues", ("count", 5)), model.IssuesLabel);

        var (few, _, _) = Shown(new Workspace(), new WorkspaceReviewPlan([], blockers.Take(3).ToList()));
        Assert.False(few.ShowsIssues);
        var (none, _, _) = Shown(new Workspace(), new WorkspaceReviewPlan([], []));
        Assert.False(none.ShowsBlockers);
    }

    [Fact]
    public void Conflicts_can_be_resolved_only_when_there_are_some_and_the_window_allows_it()
    {
        var workspace = new Workspace();
        Assert.False(Shown(workspace, new WorkspaceReviewPlan([], [])).Model.CanResolveConflicts);
        workspace.Resources[ResourceKind.Fleets].Conflicts.Add(new MergeConflict(Guid.NewGuid(), "Name", "a", "b", "c"));
        workspace.Resources[ResourceKind.Routes].Conflicts.Add(new MergeConflict(Guid.NewGuid(), "Name", "a", "b", "c"));
        var (model, _, language) = Shown(workspace, new WorkspaceReviewPlan([], []));
        Assert.True(model.CanResolveConflicts);
        Assert.Equal(language.Get("ApiConflicts", ("count", 2)), model.ConflictsLabel);
        Assert.False(Shown(workspace, new WorkspaceReviewPlan([], []), canResolve: false).Model.CanResolveConflicts);
    }

    [Fact]
    public void An_empty_review_says_there_is_nothing_to_export()
    {
        var (model, _, _) = Shown(new Workspace(), new WorkspaceReviewPlan([], []));
        Assert.True(model.ShowsNothingToReview);
        Assert.Equal("No changes to export", model.NothingToReview);
    }

    [Fact]
    public void A_change_reads_its_kind_key_and_each_changed_field_and_a_deletion_says_so()
    {
        var language = new LocalizationService();
        language.SetLanguage("en-US");
        // The planner records the changed fields on the item; the text lists those, with the value before.
        var update = PlannedChange.Wrap(new ChangeItem
        {
            Resource = ResourceKind.Fleets, Kind = ChangeKind.Update, Before = Row(("Name", "old")), After = Row(("Name", "new")),
            Fields = new() { ["Name"] = new(FieldIntent.Set, "new") }
        });
        var text = ReviewTexts.ChangeText(update, language);
        Assert.StartsWith(language.Get("Enum_ChangeKind_Update") + " · ", text, StringComparison.Ordinal);
        Assert.EndsWith(ApiOperationPolicy.FieldLabel("Name", language) + ": old → new", text, StringComparison.Ordinal);
        var delete = PlannedChange.Wrap(new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Delete, Before = Row(("Name", "old")), After = Row(("Name", "old")) });
        Assert.EndsWith("\n" + language.Get("Text_5ABD08A949"), ReviewTexts.ChangeText(delete, language), StringComparison.Ordinal);
    }

    [Fact]
    public void A_language_refresh_translates_the_header_and_every_row()
    {
        var language = new LocalizationService();
        var model = new ReviewViewModel(new Workspace { Mode = RunMode.Demo }, language);
        model.Show(new WorkspaceReviewPlan([Entry(ResourceKind.Fleets)], []), true, true, _ => Task.CompletedTask);
        var chinese = (model.Title, model.Rows[0].Heading, model.Rows[1].State);
        var page = new List<string?>();
        var rows = new List<string?>();
        model.PropertyChanged += (_, e) => page.Add(e.PropertyName);
        model.Rows[1].PropertyChanged += (_, e) => rows.Add(e.PropertyName);
        language.SetLanguage("en-US");
        model.RefreshLanguage();
        Assert.NotEqual(chinese, (model.Title, model.Rows[0].Heading, model.Rows[1].State));
        Assert.Contains(nameof(ReviewViewModel.SubmitLabel), page);
        Assert.Contains(nameof(ReviewViewModel.ShownBlockers), page);
        Assert.Equal([nameof(ReviewRowViewModel.Heading), nameof(ReviewRowViewModel.ExportLabel), nameof(ReviewRowViewModel.State), nameof(ReviewRowViewModel.Body), nameof(ReviewRowViewModel.Reason)], rows);
    }
}
