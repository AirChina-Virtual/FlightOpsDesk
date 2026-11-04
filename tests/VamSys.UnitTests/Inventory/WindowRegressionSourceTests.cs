namespace VamSys.UnitTests.Inventory;

// Two fixed window problems that no test can see happen: one is a colour, the other a queue that only shows at close. The
// source is the evidence that the fix is still there. How the colours look stays a manual check (R8).
public class WindowRegressionSourceTests
{
    static string DataTableView => SourceScan.Read(Path.Combine(SourceScan.Src, "VamSys.App", "DataTableView.cs"));

    // A row style not based on the default one lost the theme colours of its check box (black on dark, 2026-10-20).
    [Fact]
    public void The_row_style_builds_on_the_default_list_item_style()
    {
        Assert.Contains("BasedOn = (Style)Application.Current.Resources[\"DefaultListViewItemStyle\"]", DataTableView, StringComparison.Ordinal);
    }

    // Toggling the table's IsEnabled on every query queued events that closing then took minutes to drain (2026-10-19).
    [Fact]
    public void Locking_the_table_never_toggles_its_enabled_state()
    {
        Assert.Contains("Table.IsHitTestVisible", DataTableView, StringComparison.Ordinal);
        Assert.DoesNotContain("Table.IsEnabled", DataTableView, StringComparison.Ordinal);
    }
}
