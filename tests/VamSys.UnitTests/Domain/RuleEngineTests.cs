using System.Globalization;
using VamSys.Core;
using static VamSys.UnitTests.Domain.TestRows;

namespace VamSys.UnitTests.Domain;

/// <summary>
/// F08: batch rules are previews on copies. The minute-shift rule has its own tests (BatchShiftMinutesTests, A4);
/// this class covers the other rules and the preview contract shared by all of them.
/// </summary>
public class RuleEngineTests
{
    [Fact]
    public void Preview_returns_copies_with_the_same_ids_and_never_changes_the_input_rows()
    {
        var row = Row(("Time", "23:45"), ("Date", "2026-12-31 12:00:00"), ("Name", "n"));
        var before = Snapshot([row]);

        var preview = RuleEngine.Preview([row], new(RuleKind.ShiftDays, "Date", "1"));

        Assert.Equal(before, Snapshot([row]));
        Assert.NotSame(row, preview[0]);
        Assert.Equal(row.LocalId, preview[0].LocalId);
        Assert.Equal("n", preview[0].Get("Name"));
        Assert.Equal("23:45", preview[0].Get("Time"));
    }

    [Theory]
    [InlineData(RuleKind.Set, "Remarks", "new", "", "old", "new")]
    [InlineData(RuleKind.Set, "Remarks", "", "", "old", "")]
    [InlineData(RuleKind.Replace, "Remarks", "B", "A", "AxA", "BxB")]
    [InlineData(RuleKind.Replace, "Remarks", "B", "a", "AxA", "AxA")]
    [InlineData(RuleKind.Replace, "Remarks", "", "x", "AxA", "AA")]
    [InlineData(RuleKind.AddTags, "Tags", "b,c", "", "a,b", "a,b,c")]
    [InlineData(RuleKind.AddTags, "Tags", " b , c ", "", "", "b,c")]
    [InlineData(RuleKind.AddTags, "Tags", "a", "", "a", "a")]
    [InlineData(RuleKind.RemoveTags, "Tags", "b", "", "a,b,c", "a,c")]
    [InlineData(RuleKind.RemoveTags, "Tags", "a, c", "", "a,b,c", "b")]
    [InlineData(RuleKind.RemoveTags, "Tags", "z", "", "a,b", "a,b")]
    [InlineData(RuleKind.RemoveTags, "Tags", "a,b", "", "a,b", "")]
    [InlineData(RuleKind.ShiftDays, "End Date", "1", "", "2026-12-31 12:00:00", "2027-01-01 12:00:00")]
    [InlineData(RuleKind.ShiftDays, "End Date", "-31", "", "2026-03-01 08:30:00", "2026-01-29 08:30:00")]
    [InlineData(RuleKind.ShiftDays, "End Date", "0", "", "2026-03-01 08:30:00", "2026-03-01 08:30:00")]
    public void Each_rule_computes_the_new_value_of_its_field(RuleKind kind, string field, string value, string search, string old, string expected)
    {
        var row = Row((field, old), ("Other", "keep"));

        var preview = RuleEngine.Preview([row], new(kind, field, value, search)).Single();

        Assert.Equal(expected, preview.Get(field));
        Assert.Equal("keep", preview.Get("Other"));
        Assert.Equal(old, row.Get(field));
    }

    [Fact]
    public void A_rule_applies_to_every_selected_row_and_keeps_their_order()
    {
        var rows = new[] { Row(("Tags", "a")), Row(("Tags", "b")), Row(("Tags", "")) };

        var preview = RuleEngine.Preview(rows, new(RuleKind.AddTags, "Tags", "x"));

        Assert.Equal(["a,x", "b,x", "x"], preview.Select(r => r.Get("Tags")));
        Assert.Equal(rows.Select(r => r.LocalId), preview.Select(r => r.LocalId));
    }

    [Fact]
    public void Replace_needs_a_search_text()
    {
        var error = Assert.Throws<ArgumentException>(() => RuleEngine.Preview([Row(("Remarks", "x"))], new(RuleKind.Replace, "Remarks", "y")));

        Assert.NotNull(MessageErrors.Describe(error).Code);
    }

    [Fact]
    public void An_unknown_rule_kind_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(() => RuleEngine.Preview([Row(("Remarks", "x"))], new((RuleKind)99, "Remarks", "y")));

        Assert.NotNull(MessageErrors.Describe(error).Code);
    }

    [Fact]
    public void Retire_sets_the_end_date_to_yesterday_in_utc_whatever_field_the_rule_names()
    {
        var row = Row(("End Date", "2030-01-01 00:00:00"), ("Name", "n"));

        var preview = RuleEngine.Preview([row], new(RuleKind.Retire, "Name", "")).Single();

        var end = DateTime.ParseExact(preview.Get("End Date"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.InRange(end, DateTime.UtcNow.AddDays(-1).AddMinutes(-2), DateTime.UtcNow.AddDays(-1).AddMinutes(2));
        Assert.Equal("n", preview.Get("Name"));
    }

    // Product gap found while writing these tests (not fixed here: src/ is out of scope for R2 tests). The minute-shift rule
    // got input validation and per-row skips in A4; the day-shift rule still lets the raw parser exceptions escape.
    [Theory(Skip = "Product gap: ShiftDays throws a raw FormatException / OverflowException for an empty or malformed date or a non-numeric day count instead of a localized validation message or a per-row skip (compare A4 for ShiftMinutes).")]
    [InlineData("", "1")]
    [InlineData("2026-12-31", "1")]
    [InlineData("2026-12-31 12:00:00", "x")]
    public void ShiftDays_reports_unusable_input_as_a_message_instead_of_a_raw_parser_exception(string date, string days)
    {
        var error = Record.Exception(() => RuleEngine.Preview([Row(("Date", date))], new(RuleKind.ShiftDays, "Date", days)));

        Assert.IsNotType<FormatException>(error);
        Assert.IsNotType<OverflowException>(error);
    }

    [Fact]
    public void Evaluate_reports_no_skips_and_no_validation_message_for_rules_other_than_minute_shift()
    {
        var outcome = RuleEngine.Evaluate([Row(("Remarks", "x"))], new(RuleKind.Set, "Remarks", "y"));

        Assert.Empty(outcome.Skips);
        Assert.Null(outcome.Validation);
        Assert.Equal("y", outcome.Rows.Single().Get("Remarks"));
    }

    [Fact]
    public void Minute_shift_rows_that_cannot_move_come_back_unchanged_and_are_listed_as_skips()
    {
        var inside = Row(("Time", "10:00"));
        var crossing = Row(("Time", "23:45"));

        var outcome = RuleEngine.Evaluate([inside, crossing], new(RuleKind.ShiftMinutes, "Time", "30"));

        Assert.Equal(["10:30", "23:45"], outcome.Rows.Select(r => r.Get("Time")));
        Assert.Equal([new RuleSkip(crossing.LocalId, "BatchShiftSkippedCrossesMidnight")], outcome.Skips);
    }
}
