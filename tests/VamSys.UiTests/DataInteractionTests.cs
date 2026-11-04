using System.Text.Json.Nodes;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// The data page's density, unapplied query and batch preview lock, its layouts across window size, language, theme and
/// density (with screenshots for a person to look at), and the workspace review. Replaces scripts/test-ui-interactions.ps1.
/// </summary>
public class DataInteractionTests
{
    static int EnabledBatchActions(JsonNode state, string? target = null) =>
        state["batchEnabled"]!.AsArray().Count(a => a!.Bool("enabled") && (target is null || a!.Str("target") == target));

    [WindowTheory]
    [InlineData(20000)]
    [InlineData(50000)]
    public void Density_query_batch_lock_layouts_and_review_work_with_many_rows(int rows)
    {
        using var app = Start($"data-interactions-{rows}", d => DataUiFixture.Seed(d, rows));
        app.Context = () => app.Query("data-state").ToJsonString();

        var initial = app.Query("data-open")["state"]!;
        Assert.Equal(("comfortable", 40.0), (initial.Str("density"), initial.Number("actualRowHeight")));
        Assert.Equal(0, EnabledBatchActions(initial));
        Assert.False(initial.Bool("unapplied"), "Initial query incorrectly marked as unapplied");

        // A selection refresh leaves the typed page number alone.
        app.Query("data-select-first");
        app.Query("data-page:2");
        app.Query("data-select-first");
        Assert.Equal("9", app.Query("data-page-input:9").Str("pageInput"));

        // Density keeps the selection, the page and the position, and neither queries nor edits.
        var before = app.Query("data-scroll:30,120");
        var compact = app.Query("data-density:compact");
        app.Screenshot("after-density.png");
        Assert.Equal(32.0, compact.Number("actualRowHeight"));
        Assert.True(compact.Int("fullVisibleRows") > before.Int("fullVisibleRows"), "Compact view did not increase visible rows");
        Assert.Equal((2, 2, before.Str("anchor")), (compact.Int("selected"), compact.Int("page"), compact.Str("anchor")));
        Assert.True(Math.Abs(compact.Number("horizontal") - before.Number("horizontal")) <= 1, "Density switch lost horizontal position");
        Assert.Equal((before.Int("queryBuilds"), before.Int("draftSaves"), before.Int("fullSaves"), before.Int("undo")),
            (compact.Int("queryBuilds"), compact.Int("draftSaves"), compact.Int("fullSaves"), compact.Int("undo")));
        var restored = app.Query("data-density:comfortable");
        Assert.Equal((before.Str("anchor"), 40.0), (restored.Str("anchor"), restored.Number("actualRowHeight")));

        // A condition not yet applied does not change the results.
        var unapplied = app.Query("data-unapplied:pending condition");
        Assert.True(unapplied.Bool("unapplied"), "The typed condition is not marked as unapplied");
        Assert.Equal((restored.Int("queryBuilds"), rows), (unapplied.Int("queryBuilds"), unapplied.Int("total")));
        app.Query("""data-query:{"search":"","filter":0,"sort":null}""");

        // A batch preview freezes its scope; cancelling changes nothing and unlocks the controls.
        var beforeDialog = app.Query("data-state");
        Assert.True(app.Query("data-batch-dialog").Bool("locked"), "Batch preview did not freeze the operation scope");
        app.Screenshot("batch-dialog.png");
        var canceled = app.Query("dialog-cancel");
        Assert.True(canceled.Bool("unlocked"), "Cancel left controls locked");
        Assert.Equal((beforeDialog.Int("undo"), beforeDialog.Int("fullSaves")), (canceled["state"]!.Int("undo"), canceled["state"]!.Int("fullSaves")));

        // Language and theme change neither the density nor the query, at any window size.
        app.Query("data-page:1");
        app.Query("data-scroll:0,0");
        var matrix = new JsonArray();
        foreach (var size in new[] { "1200,800", "700,650" })
        {
            app.Query("data-window:" + size);
            foreach (var language in new[] { "zh-CN", "en-US" })
            {
                app.Query("language:" + language);
                foreach (var theme in new[] { "dark", "light" })
                {
                    app.Query("data-theme:" + theme);
                    foreach (var density in new[] { "comfortable", "compact" })
                    {
                        var state = app.Query("data-density:" + density);
                        Assert.Equal((density, 200, false), (state.Str("density"), state.Int("rows"), state.Bool("unapplied")));
                        var name = $"{size.Replace(',', 'x')}-{language}-{theme}-{density}.png";
                        app.Screenshot(name);
                        matrix.Add(new JsonObject { ["size"] = size, ["language"] = language, ["theme"] = theme, ["density"] = density, ["state"] = state, ["screenshot"] = name });
                    }
                }
            }
        }

        // The review groups the workspace's resources and never shows a single-resource picker; a stale one refreshes.
        app.Query("data-window:1200,800");
        app.Query("language:zh-CN");
        app.Query("data-theme:dark");
        var review = app.Query("review-fixture");
        Assert.False(review.Bool("resourcePickerVisible"), "The review showed a single-resource picker");
        Assert.True(review["resources"]!.AsArray().Count >= 3, "The review omitted resource groups");
        Assert.Equal(review.Int("total"), review.Int("ready"));
        Assert.True(review.Int("rows") > review.Int("total"), "The review has no group rows");
        Assert.False(string.IsNullOrEmpty(review.Str("renderedText")), "The review rendered no text");
        app.Screenshot("workspace-review.png");
        var stale = app.Query("review-stale");
        Assert.True(stale.Bool("refreshed"), "Stale workspace review was accepted");

        File.WriteAllText(Path.Combine(app.OutputDirectory, "summary.json"), new JsonObject
        {
            ["rows"] = rows, ["initial"] = initial.DeepClone(), ["before"] = before, ["compact"] = compact, ["restored"] = restored, ["unapplied"] = unapplied,
            ["cancel"] = canceled, ["review"] = review, ["stale"] = stale, ["matrix"] = matrix, ["dataDirectory"] = app.DataDirectory
        }.ToJsonString());

        app.Close();
    }

    [WindowFact]
    public void The_review_renders_its_groups_in_both_languages_and_refuses_to_stay_stale()
    {
        using var app = Start("data-review", d => DataUiFixture.Seed(d, 20000));
        app.Context = () => app.Query("review-state").ToJsonString();

        var initial = app.Query("data-open")["state"]!;
        Assert.Equal(0, EnabledBatchActions(initial));
        Assert.Equal(1, EnabledBatchActions(app.Query("data-select-first"), "delete"));
        app.Query("data-clear");
        app.Screenshot("initial-view.png");

        app.Query("data-window:1200,800");
        app.Query("language:zh-CN");
        var review = app.Query("review-fixture");
        Assert.Equal(review.Int("total"), review.Int("ready"));
        Assert.True(review["resources"]!.AsArray().Count >= 3, "The review omitted resource groups");
        Assert.False(string.IsNullOrEmpty(review.Str("renderedText")), "The review rendered no text");
        app.Screenshot("workspace-review.png");

        app.Query("language:en-US");
        app.Screenshot("workspace-review-en.png");
        Assert.Equal("Airports", app.Query("review-state").Str("renderedText"));

        app.Query("language:zh-CN");
        Assert.True(app.Query("review-stale").Bool("refreshed"), "Stale review was accepted");

        app.Close();
    }
}
