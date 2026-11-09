using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    string tableDensity = "comfortable";
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
        // The resource tabs take their own width; the snapshot text is cut to what is left.
        SnapshotLabel.MaxWidth = Math.Max(120, BodyGrid.ActualWidth - (merged ? 180 : 40) - ResourcePicker.ActualWidth);
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
        tableDensity = target;
        // The column headers stay above the list and take no part in its scrolling.
        var targetOffset = DataGridScroll.AnchoredOffset(anchorIndex, anchor?.Top ?? 0, 0, DataRowHeight, state.Vertical);
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

    (Guid Id, double Top)? FirstVisibleDataRow() => tableView?.FirstVisibleRow(visible);

    void RefreshQueryNotice()
    {
        if (queryNotice is null || shownState is null)
            return;
        var changed = searchBox?.Text != shownState.AppliedSearch || filterBox?.SelectedIndex != shownState.AppliedFilter || (sortBox?.SelectedItem as string ?? "") != (shownState.AppliedSort ?? "");
        queryNotice.Text = changed ? L("TableUnapplied") : "";
        queryNotice.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
    }
}
