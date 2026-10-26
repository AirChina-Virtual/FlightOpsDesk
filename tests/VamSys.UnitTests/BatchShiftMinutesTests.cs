using VamSys.Core;

namespace VamSys.UnitTests;

/// <summary>A4: the batch "shift minutes" rule validates its input first and skips unusable rows one by one.</summary>
public class BatchShiftMinutesTests
{
    const string Departure = "Departure Time (HH:MM)";
    const string Arrival = "Arrival Time (HH:MM)";

    static DataRow Row(string field, string value)
    {
        return new DataRow { Fields = new Dictionary<string, string> { [field] = value } };
    }

    static BatchEditPreview Shift(string minutes, string field, params DataRow[] rows)
        => BatchEditPreview.Rule(BatchSelection.Capture(rows), new EditRule(RuleKind.ShiftMinutes, field, minutes));

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1.5")]
    [InlineData("1440")]
    [InlineData("-1440")]
    [InlineData("99999999999")]
    public void Invalid_minutes_give_a_validation_message_and_no_changes(string minutes)
    {
        var rows = new[] { Row(Departure, "08:00"), Row(Departure, "09:00") };

        BatchEditPreview? preview = null;
        var error = Record.Exception(() => preview = Shift(minutes, Departure, rows));

        Assert.Null(error);
        Assert.Empty(preview!.Changes);
        Assert.Empty(preview.RuleSkips);
        Assert.Equal("BatchShiftInvalidMinutes", preview.Validation?.Code);
        Assert.Equal(["08:00", "09:00"], rows.Select(r => r.Get(Departure)));
    }

    [Fact]
    public void A_row_with_an_empty_or_malformed_time_is_skipped_and_the_others_are_shifted()
    {
        var good1 = Row(Departure, "08:00");
        var empty = Row(Departure, "");
        var missing = new DataRow();
        var bad = Row(Departure, "8am");
        var good2 = Row(Departure, "09:30");

        BatchEditPreview? preview = null;
        var error = Record.Exception(() => preview = Shift("30", Departure, good1, empty, missing, bad, good2));

        Assert.Null(error);
        Assert.Null(preview!.Validation);
        Assert.Equal(["08:30", "10:00"], preview.Changes.Select(r => r.Get(Departure)));
        Assert.Equal(3, preview.RuleSkips.Count);
        Assert.All(preview.RuleSkips, s => Assert.Equal("BatchShiftSkippedInvalidTime", s.ReasonCode));
        Assert.Equal([empty.LocalId, missing.LocalId, bad.LocalId], preview.RuleSkips.Select(s => s.LocalId));
        Assert.Equal(0, preview.Unchanged);
        Assert.Equal("BatchShiftSkippedInvalidTime", Assert.Single(preview.SkipReasons).Code);
        Assert.Equal(3m, preview.SkipReasons[0].Arguments["count"].Number);
    }

    [Theory]
    [InlineData("23:30", "29", "23:59")]
    [InlineData("00:10", "-10", "00:00")]
    public void A_shift_that_stays_inside_the_day_is_applied(string time, string minutes, string expected)
    {
        var preview = Shift(minutes, Arrival, Row(Arrival, time));

        Assert.Equal(expected, Assert.Single(preview.Changes).Get(Arrival));
        Assert.Empty(preview.RuleSkips);
    }

    [Theory]
    [InlineData("23:30", "30")]   // lands exactly on 24:00
    [InlineData("23:30", "45")]
    [InlineData("00:10", "-11")]
    [InlineData("00:00", "-1")]
    [InlineData("12:00", "720")]
    [InlineData("12:00", "-721")]
    public void A_shift_that_crosses_midnight_skips_the_row_and_leaves_it_untouched(string time, string minutes)
    {
        var row = Row(Departure, time);
        var preview = Shift(minutes, Departure, row);

        Assert.Equal(time, row.Get(Departure));
        var skip = Assert.Single(preview.RuleSkips);
        Assert.Equal(row.LocalId, skip.LocalId);
        Assert.Equal("BatchShiftSkippedCrossesMidnight", skip.ReasonCode);
        Assert.Empty(preview.Changes);
        Assert.Equal(0, preview.Unchanged);
        Assert.Equal("BatchShiftSkippedCrossesMidnight", Assert.Single(preview.SkipReasons).Code);
    }

    [Fact]
    public void Skipped_midnight_rows_do_not_stop_the_other_rows_from_shifting()
    {
        var late = Row(Departure, "23:50");
        var early = Row(Departure, "09:00");

        var preview = Shift("20", Departure, late, early);

        Assert.Equal("09:20", Assert.Single(preview.Changes).Get(Departure));
        Assert.Equal(late.LocalId, Assert.Single(preview.RuleSkips).LocalId);
        Assert.Equal(1, preview.TotalSkipped);
    }

    [Fact]
    public void A_normal_shift_is_unchanged_and_keeps_the_seconds_and_format()
    {
        var plain = Row(Departure, "08:30");
        var withSeconds = Row(Departure, "08:30:15");
        var back = Row(Departure, "10:05:59");

        var preview = Shift("-65", Departure, plain, withSeconds, back);

        Assert.Equal(["07:25", "07:25:15", "09:00:59"], preview.Changes.Select(r => r.Get(Departure)));
        Assert.Empty(preview.RuleSkips);
        Assert.Null(preview.Validation);
        Assert.Empty(preview.SkipReasons);
        Assert.Equal(0, preview.Unchanged);
    }

    [Fact]
    public void A_zero_shift_counts_valid_rows_as_unchanged_and_skipped_rows_separately()
    {
        var preview = Shift("0", Departure, Row(Departure, "08:30"), Row(Departure, "nope"));

        Assert.Empty(preview.Changes);
        Assert.Single(preview.RuleSkips);
        Assert.Equal(1, preview.Unchanged);
    }
}
