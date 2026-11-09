using System.Collections.Immutable;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class DataSortTests
{
    [Fact]
    public void A_sort_round_trips_and_a_header_click_cycles_ascending_descending_and_none()
    {
        Assert.Equal(("ID", false), DataSort.Parse(DataSort.Format("ID", false)));
        Assert.Equal(("ID", true), DataSort.Parse(DataSort.Format("ID", true)));
        Assert.Equal((null, false), DataSort.Parse(null));
        Assert.Null(DataSort.Format(null, true));

        var first = DataSort.Next(null, "ID");
        Assert.Equal(("ID", false), DataSort.Parse(first));
        var second = DataSort.Next(first, "ID");
        Assert.Equal(("ID", true), DataSort.Parse(second));
        Assert.Null(DataSort.Next(second, "ID"));
        Assert.Equal(("Name", false), DataSort.Parse(DataSort.Next(second, "Name")));
    }

    [Fact]
    public void Descending_reverses_the_order_and_keeps_equal_values_in_draft_order()
    {
        var rows = new[] { "b", "a", "c", "a" }.Select(v => new DataQueryRow(Guid.NewGuid(), [], v, false, false)).ToList();
        var ascending = DataTableViewModel.Query(rows, "", 0, true, CancellationToken.None);
        var descending = DataTableViewModel.Query(rows, "", 0, true, CancellationToken.None, descending: true);
        Assert.Equal([rows[1].LocalId, rows[3].LocalId, rows[0].LocalId, rows[2].LocalId], ascending.RowIds);
        Assert.Equal([rows[2].LocalId, rows[0].LocalId, rows[1].LocalId, rows[3].LocalId], descending.RowIds);
    }
}
