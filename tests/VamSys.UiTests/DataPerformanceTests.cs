using System.Globalization;
using System.Text.Json.Nodes;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// The data page with many rows: selection across pages, the reused editor, sorting, searching the whole data, page sizes,
/// the agreed time limits, and undo and redo that save only the row and never query again. Writes measurements.json and
/// benchmark.json, and screenshots for a person to look at, to the output directory. Replaces
/// scripts/test-data-performance-ui.ps1.
/// </summary>
public class DataPerformanceTests
{
    [WindowTheory]
    [InlineData(20000)]
    [InlineData(50000)]
    public void The_data_page_stays_correct_and_within_its_limits(int rows)
    {
        using var app = Start($"data-performance-{rows}", d => DataUiFixture.Seed(d, rows));
        app.Context = () => app.Query("data-state").ToJsonString();
        string Text(int number) => number.ToString(CultureInfo.InvariantCulture);
        JsonNode Search(string search, string sort = "") =>
            app.Query("data-query:" + new JsonObject { ["search"] = search, ["filter"] = 0, ["sort"] = sort }.ToJsonString());
        void Screenshot(string name) => app.Query("data-screenshot:" + Path.Combine(app.OutputDirectory, name));

        var opened = app.Query("data-open");
        var state = opened["state"]!;
        Assert.Equal((rows, 200, 200, true), (state.Int("total"), state.Int("rows"), state.Int("pageSize"), state.Bool("hasEditors")));
        Screenshot("initial-page.png");

        // Selection reaches across pages, and the reused editor edits the page it is on.
        app.Query("data-select-first");
        app.Query("data-page:2");
        var selected = app.Query("data-select-first");
        Assert.Equal(2, selected.Int("selected"));
        Assert.Equal(["0", "200"], selected["selectedRows"]!.AsArray().Select(r => r!.ToString()).Order(StringComparer.Ordinal));
        var rebound = app.Query("data-edit-rebound");
        Assert.Equal((true, "200", "0"), (rebound.Bool("correct"), rebound.Str("current"), rebound.Str("previous")));

        // Sorting keeps the selection; a search covers all the data.
        Assert.Equal(2, Search("", "ID")["state"]!.Int("selected"));
        var search = Search("row " + Text(rows - 1));
        var found = search["state"]!;
        Assert.Equal((1, Text(rows - 1), 0, 1), (found.Int("total"), found.Str("first"), found.Int("selected"), found.Int("page")));
        var empty = Search("no-such-record-20261015")["state"]!;
        Assert.Equal((0, 1), (empty.Int("rows"), empty.Int("pages")));
        Search("");

        // Page sizes keep the first shown row, and selecting everything creates no more models than the page.
        app.Query("data-page:3");
        var small = app.Query("data-size:100");
        Assert.Equal((5, "400"), (small.Int("page"), small.Str("first")));
        var large = app.Query("data-size:500");
        Assert.Equal((500, 1), (large.Int("rows"), large.Int("page")));
        app.Query("data-size:200");
        var all = app.Query("data-select-all");
        Assert.Equal((rows, 200), (all.Int("selected"), all.Int("rows")));
        app.Query("data-clear");
        Assert.True(app.Query("data-cancel-test").Bool("retained"), "Canceled query replaced results");

        var metrics = app.Query("data-benchmark");
        File.WriteAllText(Path.Combine(app.OutputDirectory, "measurements.json"), new JsonObject
        {
            ["opened"] = opened.DeepClone(), ["search"] = search.DeepClone(), ["benchmark"] = metrics.DeepClone(), ["dataDirectory"] = app.DataDirectory
        }.ToJsonString());
        Assert.Equal((metrics.Int("buildsBefore"), metrics.Int("queriesBefore")), (metrics.Int("buildsAfter"), metrics.Int("queriesAfter")));
        var modelled = metrics["state"]!;
        Assert.True(modelled.Int("rows") <= 200, "Row models exceeded the page");
        Assert.Equal(modelled.Int("rows") * modelled.Int("columns"), modelled.Int("cells"));
        Assert.True(metrics["paging"]!.Number("p95") <= 150, $"Paging P95 {metrics["paging"]!.Number("p95")} ms");
        Assert.True(metrics["language"]!.Number("p95") <= 150, $"Language P95 {metrics["language"]!.Number("p95")} ms");
        Assert.True(metrics["edit"]!.Number("p95") <= 50, $"Edit P95 {metrics["edit"]!.Number("p95")} ms");
        Assert.True(opened.Number("milliseconds") <= 1000, $"Opening took {opened.Number("milliseconds")} ms");
        Assert.True(search.Number("milliseconds") <= 1000, $"Searching took {search.Number("milliseconds")} ms");

        var last = app.Query("data-page:" + Text(rows / 200));
        Assert.Equal(Text(rows - 1), last.Str("last"));
        Screenshot("last-page.png");
        app.Query("language:zh-CN");
        app.Query("data-page:1");
        Screenshot("first-page-zh.png");

        // Undo and redo of a replaced row save the row, not the workspace, and rebind the page instead of querying again.
        var samples = Enumerable.Range(0, 25).Select(_ => app.Query("data-undo-save")).Skip(5).ToList();
        Assert.All(samples, s => Assert.Equal((0, 2, 0, true), (s.Int("fullCommits"), s.Int("draftCommits"), s.Int("queries"), s.Bool("rowsMatch"))));
        double[] Ordered(string key) => [.. samples.Select(s => s.Number(key)).Order()];
        double[] undo = Ordered("totalMilliseconds"), redo = Ordered("redoMilliseconds");

        File.WriteAllText(Path.Combine(app.OutputDirectory, "benchmark.json"), new JsonObject
        {
            ["rows"] = rows, ["opened"] = opened.DeepClone(), ["search"] = search.DeepClone(), ["benchmark"] = metrics.DeepClone(), ["last"] = last, ["workingSetBytes"] = app.WorkingSetBytes,
            ["dataDirectory"] = app.DataDirectory, ["reboundEditor"] = rebound,
            ["undoWithSave"] = new JsonObject { ["samples"] = new JsonArray([.. samples]), ["totalMedian"] = undo[10], ["totalP95"] = undo[18], ["redoMedian"] = redo[10], ["redoP95"] = redo[18] }
        }.ToJsonString());

        app.Close();
    }
}
