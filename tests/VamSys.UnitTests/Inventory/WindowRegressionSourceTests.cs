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

    // A brush read in code keeps the theme it was read in: the column names vanished on a light window (2026-11-05).
    // The look stays a manual check; this keeps the colours where the theme can reach them.
    [Fact]
    public void Colours_set_from_code_go_through_styles_that_follow_the_theme()
    {
        var tokens = SourceScan.Read(Path.Combine(SourceScan.Src, "VamSys.App", "UiTokens.cs"));
        foreach (var brush in new[] { "SecondaryTextBrush", "StatusErrorBrush", "ModeOnlineBrush", "ModeDemoBrush" })
            Assert.DoesNotContain($"Get<Brush>(\"{brush}\")", tokens, StringComparison.Ordinal);
        Assert.Contains("ColumnHeaderTextStyle", DataTableView, StringComparison.Ordinal);
    }

    // Toggling the table's IsEnabled on every query queued events that closing then took minutes to drain (2026-10-19).
    [Fact]
    public void Locking_the_table_never_toggles_its_enabled_state()
    {
        Assert.Contains("Table.IsHitTestVisible", DataTableView, StringComparison.Ordinal);
        Assert.DoesNotContain("Table.IsEnabled", DataTableView, StringComparison.Ordinal);
    }
}
