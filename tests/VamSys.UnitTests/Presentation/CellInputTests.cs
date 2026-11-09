using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class CellInputTests
{
    [Theory]
    [InlineData("930", "09:30")]
    [InlineData("0930", "09:30")]
    [InlineData("9:5", "09:05")]
    [InlineData("9", "09:00")]
    [InlineData("23:59", "23:59")]
    [InlineData("25:99", "25:99")]
    [InlineData("soon", "soon")]
    [InlineData("", "")]
    public void Times_are_put_into_hours_and_minutes(string typed, string stored)
        => Assert.Equal(stored, CellInput.Normalize(FieldType.Time, typed));

    [Theory]
    [InlineData("2026-3-1", "2026-03-01 00:00:00")]
    [InlineData("2026/03/01", "2026-03-01 00:00:00")]
    [InlineData("20260301", "2026-03-01 00:00:00")]
    [InlineData("2026-3-1 8:30", "2026-03-01 08:30:00")]
    [InlineData("2026-03-01 08:30:00", "2026-03-01 08:30:00")]
    [InlineData("2026-03-01 08:30:00.250", "2026-03-01 08:30:00.250")]
    [InlineData("next week", "next week")]
    public void Dates_are_put_into_the_stored_format_and_a_stored_value_is_kept(string typed, string stored)
        => Assert.Equal(stored, CellInput.Normalize(FieldType.Date, typed));

    [Theory]
    [InlineData("mon,FRI", "monday,friday")]
    [InlineData("sunday, monday,monday", "monday,sunday")]
    [InlineData("t", "t")]
    [InlineData("tu,th", "tuesday,thursday")]
    [InlineData("funday", "funday")]
    public void Day_lists_are_full_day_names_in_week_order(string typed, string stored)
        => Assert.Equal(stored, CellInput.Normalize(FieldType.DayList, typed));

    [Fact]
    public void Yes_and_no_become_TRUE_and_FALSE_and_other_types_are_left_alone()
    {
        Assert.Equal("TRUE", CellInput.Normalize(FieldType.Bool, "yes"));
        Assert.Equal("FALSE", CellInput.Normalize(FieldType.Bool, "0"));
        Assert.Equal(" 930 ", CellInput.Normalize(FieldType.Text, " 930 "));
    }

    [Fact]
    public void A_picked_date_keeps_the_time_of_day()
    {
        Assert.Equal("2026-05-02 08:30:00", CellInput.WithDate("2026-01-01 08:30:00", new DateOnly(2026, 5, 2)));
        Assert.Equal("2026-05-02 00:00:00", CellInput.WithDate("", new DateOnly(2026, 5, 2)));
        Assert.Equal(new DateOnly(2026, 1, 1), CellInput.DateOf("2026-01-01 08:30:00"));
        Assert.Null(CellInput.DateOf("soon"));
    }

    [Fact]
    public void Clipboard_text_is_read_as_rows_of_tab_separated_cells()
    {
        var table = CellInput.Table("a\tb\r\nc\td\r\n");
        Assert.Equal([["a", "b"], ["c", "d"]], table);
        Assert.Equal([["single"]], CellInput.Table("single"));
        Assert.Equal([[""], ["x"]], CellInput.Table("\nx"));
    }
}
