using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    string tableDensity = "comfortable";
    ComboBox? densityPicker;
    TextBlock? queryNotice;
    double DataRowHeight => UiTokens.DataRowHeight(tableDensity == "compact");

    void UpdateDataHeaderLayout()
    {
        var data = page == "data";
        var merged = data && BodyGrid.ActualWidth >= 800;
        PageTitle.FontSize = data ? UiTokens.DataPageTitleFontSize : UiTokens.PageTitleFontSize;
        BodyGrid.Margin = data ? UiTokens.DataPageMargin : UiTokens.PageMargin;
        BodyGrid.RowSpacing = data ? UiTokens.SpacingMedium : UiTokens.SpacingPage;
        Grid.SetRow(ResourceBar, merged ? 0 : 1);
        Grid.SetColumn(ResourceBar, merged ? 1 : 0);
        Grid.SetColumnSpan(ResourceBar, merged ? 1 : 2);
        SnapshotLabel.MaxWidth = Math.Max(120, BodyGrid.ActualWidth - (merged ? 340 : 200));
    }

    ComboBox DensityPicker()
    {
        var picker = new ComboBox { Width = UiTokens.DensityPickerWidth };
        picker.Items.Add(new ComboBoxItem().Localize(localization, "Content", () => L("DensityComfortable")));
        picker.Items.Add(new ComboBoxItem().Localize(localization, "Content", () => L("DensityCompact")));
        picker.SelectedIndex = tableDensity == "compact" ? 1 : 0;
        AutomationProperties.SetAutomationId(picker, "TableDensity");
        AutomationProperties.SetName(picker, L("TableDensity"));
        picker.SelectionChanged += async (_, _) =>
        {
            if (picker.SelectedIndex < 0 || !ReferenceEquals(picker, densityPicker))
                return;
            var target = picker.SelectedIndex == 1 ? "compact" : "comfortable";
            if (target == tableDensity || !CanEditData())
                return;
            await UserAction(async () =>
            {
                try
                { await ChangeTableDensity(target); }
                catch { picker.SelectedIndex = tableDensity == "compact" ? 1 : 0; throw; }
            });
        };
        densityPicker = picker;
        return picker;
    }

    async Task ChangeTableDensity(string target)
    {
        if (!CanEditData() || target == tableDensity)
            return;
        settings.SaveTableDensity(target); // Preserve the current view if persistence fails.
        CapturePageState();
        var state = CurrentDataState;
        var anchor = FirstVisibleDataRow();
        var anchorIndex = anchor is null ? -1 : visible.FindIndex(r => r.Row.LocalId == anchor.Value.Id);
        var headerHeight = tableView?.HeaderHeight ?? 0;
        tableDensity = target;
        var targetOffset = anchorIndex < 0 ? state.Vertical : Math.Max(0, headerHeight + anchorIndex * DataRowHeight - anchor!.Value.Top);
        // Row heights live in the item container style: build the page again.
        shownDataPage = null;
        dataPageOwner = null;
        PageRows.Reset();
        Render();
        // Render captures the old table once more; install the new offset afterwards.
        state.Vertical = targetOffset;
        RootGrid.UpdateLayout();
        RestoreDataOffsets(state);
        await Task.CompletedTask;
    }

    (Guid Id, double Top)? FirstVisibleDataRow()
    {
        if (table is null || FindVisual<ScrollViewer>(table) is not { } scroll)
            return null;
        for (var n = 0; n < visible.Count; n++)
            if (table.ContainerFromIndex(n) is FrameworkElement item)
            {
                var y = item.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
                if (y + item.ActualHeight > 0 && y < scroll.ViewportHeight)
                    return (visible[n].Row.LocalId, y);
            }
        return null;
    }

    void RefreshQueryNotice()
    {
        if (queryNotice is null || shownState is null)
            return;
        var changed = searchBox?.Text != shownState.AppliedSearch || filterBox?.SelectedIndex != shownState.AppliedFilter || sortBox?.SelectedItem as string != shownState.AppliedSort;
        queryNotice.Text = changed ? L("TableUnapplied") : "";
        queryNotice.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
    }
}
