using System.Text.Json.Nodes;
using System.Windows.Automation;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// The data table footer as a user or screen reader reaches it: paging buttons and their availability, the page jump (Go
/// button, Enter key, invalid input), the page size picker, the selection menu and the automation names after a language
/// change. Replaces scripts/test-data-footer-ui.ps1.
/// </summary>
public class DataFooterTests
{
    static readonly string[] FooterIds = ["DataFirstPage", "DataPreviousPage", "DataNextPage", "DataLastPage", "DataPageJump", "DataGoPage", "DataPageSize"];

    [WindowFact]
    public void Footer_pages_jumps_resizes_selects_and_follows_the_language()
    {
        using var app = Start("data-footer", directory => DataUiFixture.Seed(directory, 20000));
        app.Context = () => app.Query("data-state").ToJsonString();
        JsonNode State() => app.Query("data-state");
        JsonNode Page(int page, string what) => app.Wait(() => State() is var s && s.Int("page") == page ? s : null, $"{what} (page {page})");
        JsonNode Where(Func<JsonNode, bool> holds, string what) => app.Wait(() => State() is var s && holds(s) ? s : null, what);
        void PickPageSize(int size)
        {
            var box = app.One("DataPageSize");
            box.Expand();
            // The open list is a popup; its item is found from the window root.
            app.First(size.ToString(System.Globalization.CultureInfo.InvariantCulture), ControlType.ListItem).Select();
            box.Collapse();
        }

        app.Query("language:en-US");
        var opened = app.Query("data-open")["state"]!;
        Assert.Equal((20000, 100, 1), (opened.Int("total"), opened.Int("pages"), opened.Int("page")));
        foreach (var id in FooterIds)
            app.One(id);
        // Not querying: the cancel button is collapsed and not in the automation tree.
        Assert.Empty(app.Find("DataQueryCancel"));

        // Paging buttons and their availability at both ends.
        Assert.Equal((false, false, true, true), (app.Enabled("DataFirstPage"), app.Enabled("DataPreviousPage"), app.Enabled("DataNextPage"), app.Enabled("DataLastPage")));
        app.One("DataNextPage").Invoke();
        var s = Page(2, "Next");
        Assert.Equal(("200", "2"), (s.Str("first"), app.One("DataPageJump").Text()));
        app.One("DataLastPage").Invoke();
        Assert.Equal("19999", Page(100, "Last").Str("last"));
        app.Until(() => !app.Enabled("DataNextPage") && !app.Enabled("DataLastPage") && app.Enabled("DataFirstPage"), "last page availability");
        app.One("DataPreviousPage").Invoke();
        Page(99, "Previous");
        app.One("DataFirstPage").Invoke();
        Page(1, "First");

        // Page jump: the Go button, the Enter key, and invalid input that keeps the page and the typed text.
        app.One("DataPageJump").SetText("7");
        app.One("DataGoPage").Invoke();
        Page(7, "Go");
        app.Foreground();
        var jump = app.One("DataPageJump");
        jump.SetText("12");
        jump.SetFocus();
        Thread.Sleep(200);
        Press(Keys.Enter);
        Page(12, "Enter in the page jump");
        app.One("DataPageJump").SetText("999");
        app.One("DataGoPage").Invoke();
        app.Until(() => app.Name("StatusLabel") == "Enter a valid page number.", "invalid page status");
        s = State();
        Assert.Equal((12, "999"), (s.Int("page"), s.Str("pageInput")));

        // Page size keeps the first shown row on screen.
        app.One("DataPageJump").SetText("3");
        app.One("DataGoPage").Invoke();
        Page(3, "Go to 3");
        PickPageSize(100);
        s = Where(x => x.Int("pageSize") == 100, "page size 100");
        Assert.Equal((5, "400", 100), (s.Int("page"), s.Str("first"), s.Int("rows")));
        Assert.Equal("5", app.One("DataPageJump").Text());
        PickPageSize(200);
        s = Where(x => x.Int("pageSize") == 200, "page size 200");
        Assert.Equal((3, 200), (s.Int("page"), s.Int("rows")));

        // Selecting: the header box selects the page, then "select all" offers every result; clearing is offered only
        // while something is selected.
        Assert.Empty(app.Find("DataSelectAll"));
        app.One("DataSelectPage").Toggle();
        Where(x => x.Int("selected") == 200, "select page");
        app.Wait(() => app.Find("DataSelectAll").FirstOrDefault(), "select all is offered").Invoke();
        Where(x => x.Int("selected") == 20000, "select all");
        app.One("DataClearSelection").Invoke();
        Where(x => x.Int("selected") == 0, "clear selection");
        app.Until(() => app.Find("DataClearSelection").Length == 0, "clearing is gone with nothing selected");

        // Automation names follow the language.
        var pageSize = app.Name("DataPageSize");
        Assert.Equal(("Next", "Go", "Go"), (app.Name("DataNextPage"), app.Name("DataGoPage"), app.Name("DataPageJump")));
        app.Query("language:zh-CN");
        app.Until(() => app.Name("DataNextPage") == "下一页" && app.Name("DataGoPage") == "跳转" && app.Name("DataPageJump") == "跳转" && app.Name("DataPageSize") != pageSize,
            "Chinese automation names");
        app.One("DataSelectPage").Toggle();
        app.Until(() => app.Find("DataSelectAll").FirstOrDefault()?.Current.Name.StartsWith("选择全部", StringComparison.Ordinal) == true, "Chinese select-all label");
        app.One("DataClearSelection").Invoke();

        app.Close();
    }
}
