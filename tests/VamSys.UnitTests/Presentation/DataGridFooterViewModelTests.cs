using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class DataGridFooterViewModelTests
{
    static DataTableViewModel Model(int rows, int page = 1)
    {
        var model = new DataTableViewModel();
        model.Apply(new DataQueryResult(Enumerable.Range(0, rows).Select(_ => Guid.NewGuid()).ToArray(), 0), "", 0, null);
        model.SetPage(page);
        return model;
    }

    static (DataGridFooterViewModel Footer, LocalizationService Language, List<string?> Changed) Footer()
    {
        var language = new LocalizationService();
        language.SetLanguage("en-US");
        var footer = new DataGridFooterViewModel(language);
        var changed = new List<string?>();
        footer.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        return (footer, language, changed);
    }

    static void Apply(DataGridFooterViewModel footer, DataTableViewModel model, bool editable = true, bool querying = false, string summary = "summary", string selected = "selected")
        => footer.Apply(DataGridAvailability.For(model, editable, Math.Min(model.PageSize, model.Total)), model, querying, summary, selected);

    [Fact]
    public void Applying_a_state_shows_its_availability_page_and_summaries()
    {
        var (footer, _, _) = Footer();
        var model = Model(450, 2);
        Apply(footer, model, querying: true, summary: "Page 2 of 3", selected: "0 selected");
        Assert.Equal((true, true, true, true, true, true), (footer.CanFirst, footer.CanPrevious, footer.CanNext, footer.CanLast, footer.CanGo, footer.CanEdit));
        Assert.True(footer.QueryInProgress);
        Assert.Equal("2 / 3", footer.PageText);
        Assert.Equal(("Page 2 of 3", "0 selected"), (footer.PageSummary, footer.SelectionSummary));
        Assert.Equal(200, footer.PageSize);
        Assert.Equal("2", footer.PageJumpText);

        Apply(footer, Model(450, 1));
        Assert.Equal((false, false, true, true), (footer.CanFirst, footer.CanPrevious, footer.CanNext, footer.CanLast));

        Apply(footer, Model(450, 3), editable: false);
        Assert.Equal((false, false, false, false, false, false), (footer.CanFirst, footer.CanPrevious, footer.CanNext, footer.CanLast, footer.CanGo, footer.CanEdit));
        Assert.False(footer.CanSelectAll);
        Assert.False(footer.QueryInProgress);
    }

    [Fact]
    public void The_page_jump_keeps_typed_text_until_the_page_changes_or_another_state_is_shown()
    {
        var (footer, _, _) = Footer();
        var model = Model(450, 1);
        Apply(footer, model);
        footer.PageJumpText = "7";
        Apply(footer, model);
        Assert.Equal("7", footer.PageJumpText);

        model.SetPage(2);
        Apply(footer, model);
        Assert.Equal("2", footer.PageJumpText);

        footer.PageJumpText = "9";
        footer.ShowPage(2);
        Assert.Equal("2", footer.PageJumpText);
    }

    [Fact]
    public void Labels_follow_the_language_and_an_unchanged_state_raises_nothing()
    {
        var (footer, language, changed) = Footer();
        var model = Model(10);
        Apply(footer, model);
        Assert.Equal(language.Get("TableFirst"), footer.FirstLabel);
        Assert.Equal(language.Get("TableSelectAll"), footer.SelectAllLabel);
        Assert.Equal(language.Get("BatchOnlySelected"), footer.SelectionHint);
        var english = footer.NextLabel;

        changed.Clear();
        Apply(footer, model);
        Assert.Empty(changed);

        language.SetLanguage("zh-CN");
        Apply(footer, model);
        Assert.NotEqual(english, footer.NextLabel);
        Assert.Contains(nameof(DataGridFooterViewModel.NextLabel), changed);
        Assert.DoesNotContain(nameof(DataGridFooterViewModel.PageText), changed);
    }

    [Fact]
    public void The_page_size_choices_match_the_pager_and_follow_the_shown_state()
    {
        var (footer, _, _) = Footer();
        Assert.Equal([100, 200, 500], footer.PageSizeChoices);
        var model = Model(1000);
        model.SetPageSize(500);
        Apply(footer, model);
        Assert.Equal(500, footer.PageSize);
    }
}
