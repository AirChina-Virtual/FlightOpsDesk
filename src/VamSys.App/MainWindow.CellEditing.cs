using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;

namespace VamSys.App;

// Editing beyond one cell: a block pasted from a spreadsheet, a value filled down the selected records, the record
// details pane, and copies of records as new ones.
public sealed partial class MainWindow
{
    // A pasted block covers the cells to the right of and below the cell it is pasted on, within the shown page.
    // Read-only cells keep their values; values are put into the column's format as typed values are. One undo step.
    Task PasteCells(RowViewModel start, string field, string text) => LocalOperation(async () =>
    {
        if (!DataViewCurrent())
            return;
        var table = CellInput.Table(text);
        var columns = DisplayColumns();
        var left = columns.IndexOf(field);
        var top = visible.IndexOf(start);
        if (left < 0 || top < 0)
            return;
        var changed = new Dictionary<Guid, DataRow>();
        int cells = 0, kept = 0;
        for (var r = 0; r < table.Count; r++)
        {
            if (top + r >= visible.Count)
            { kept += table[r].Length; continue; }
            var row = Data.FindDraftRow(visible[top + r].Row.LocalId);
            if (row is null)
            { kept += table[r].Length; continue; }
            var replacement = row;
            for (var c = 0; c < table[r].Length; c++)
            {
                var column = left + c < columns.Count ? columns[left + c] : null;
                if (column is null or "_delete" || DataGridRules.CellReadOnly(row, column, workspace.Mode))
                { kept++; continue; }
                var value = CellInput.Normalize(FieldSpecs.Column(column)?.Type ?? FieldType.Text, table[r][c]);
                if (replacement.Get(column) == value)
                    continue;
                replacement = replacement.With(column, value);
                cells++;
            }
            if (!ReferenceEquals(replacement, row))
                changed[row.LocalId] = replacement;
        }
        if (changed.Count == 0)
        { Status(() => L("PasteNothing", ("kept", kept))); return; }
        if (cells > 1 && !await ConfirmMessage(L("PasteTitle"), L("PasteConfirm", ("cells", cells), ("rows", changed.Count), ("kept", kept)), L("PasteApply")))
            return;
        await ReplaceShownRows(changed.Values.ToList());
        Status(() => L("PasteDone", ("cells", cells), ("rows", changed.Count), ("kept", kept)));
    });

    // The cell's value into the same column of the other selected records that can take it.
    Task FillDown(RowViewModel from, string field) => LocalOperation(async () =>
    {
        if (!DataViewCurrent() || Data.FindDraftRow(from.Row.LocalId) is not { } source)
            return;
        var value = source.Get(field);
        var targets = SelectedRows().Where(r => r.LocalId != source.LocalId && !DataGridRules.CellReadOnly(r, field, workspace.Mode) && r.Get(field) != value).ToList();
        if (targets.Count == 0)
        { Status(() => L("FillDownNothing")); return; }
        var label = ApiOperationPolicy.FieldLabel(field, localization);
        if (!await ConfirmMessage(L("FillDownTitle"), L("FillDownConfirm", ("field", label), ("value", value.Length == 0 ? L("CellEmptyValue") : value), ("rows", targets.Count)), L("FillDownApply")))
            return;
        await ReplaceShownRows(targets.Select(r => r.With(field, value)).ToList());
        Status(() => L("FillDownDone", ("field", label), ("rows", targets.Count)));
    });

    // Replaced rows keep their place in the applied query, as cell edits do; the shown ones are rebound.
    async Task ReplaceShownRows(IReadOnlyList<DataRow> replacements)
    {
        var source = Data;
        var ids = replacements.Select(r => r.LocalId).ToList();
        var changedBefore = CountChanged(source, ids);
        await batch.ReplaceRowsAsync(resource, replacements);
        if (DataViewCurrent())
            RefreshReplacedRows(source, ids, changedBefore);
        else
            InvalidateDataViews();
        RefreshReviewBadge();
    }

    Task DuplicateSelected() => LocalOperation(async () =>
    {
        var rows = SelectedRows().Where(r => !Schemas.Deleted(r)).ToList();
        if (rows.Count == 0)
        { Status(() => L("BatchNothing")); return; }
        var copies = await batch.DuplicateRowsAsync(resource, rows);
        Status(() => L("RowDuplicateDone", ("count", copies.Count)));
    });

    // --- Record details: every field of one record in a pane beside the table ---
    Border? detailsPane;
    TextBlock? detailsTitle;
    StackPanel? detailsFields;
    RowViewModel? detailsRow;

    Border BuildDetailsPane()
    {
        detailsTitle = new TextBlock { Style = UiTokens.CardTitleStyle, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = new SymbolIcon(Symbol.Cancel), Padding = UiTokens.IconButtonPadding };
        ToolTipService.SetToolTip(close, new ToolTip().Localize(localization, "Content", () => L("RowDetailsClose")));
        AutomationProperties.SetAutomationId(close, "RowDetailsClose");
        close.Click += (_, _) => CloseDetails();
        var header = new Grid { ColumnSpacing = UiTokens.SpacingMedium };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(close, 1);
        header.Children.Add(detailsTitle);
        header.Children.Add(close);
        detailsFields = new StackPanel { Spacing = UiTokens.SpacingTight, Padding = UiTokens.DetailsFieldsPadding };
        var scroll = new ScrollViewer { Content = detailsFields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // The scroller, not the border: a border has no automation element of its own.
        AutomationProperties.SetAutomationId(scroll, "RowDetails");
        var hint = Text(() => L("RowDetailsHint"));
        hint.Style = UiTokens.SecondaryTextStyle;
        var content = new Grid { RowSpacing = UiTokens.SpacingMedium };
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(hint, 1);
        Grid.SetRow(scroll, 2);
        content.Children.Add(header);
        content.Children.Add(hint);
        content.Children.Add(scroll);
        return detailsPane = new Border { Style = UiTokens.CardStyle, Width = UiTokens.DetailsPaneWidth, Child = content, Visibility = Visibility.Collapsed };
    }
    void OpenDetails(Guid id)
    {
        if (detailsPane is null || cellTemplates is null || !DataViewCurrent() || PageRows.Detached(id, AllColumns()) is not { } view)
            return;
        detailsRow = view;
        FillDetails();
        detailsPane.Visibility = Visibility.Visible;
    }
    void OpenSelectedDetails()
    {
        var focused = SelectedRows().FirstOrDefault();
        if (focused is null)
        { Status(() => L("BatchNothing")); return; }
        OpenDetails(focused.LocalId);
    }
    void CloseDetails()
    {
        detailsRow = null;
        detailsFields?.Children.Clear();
        if (detailsPane != null)
            detailsPane.Visibility = Visibility.Collapsed;
    }
    void FillDetails()
    {
        if (detailsRow is not { } view || detailsFields is null || cellTemplates is null)
            return;
        detailsFields.Children.Clear();
        foreach (var cell in view.Cells)
        {
            if (cell.Field == "_delete")
                continue;
            var label = new TextBlock { Text = ApiOperationPolicy.FieldLabel(cell.Field, localization) + (cell.Required ? " *" : ""), Style = UiTokens.SecondaryTextStyle, Margin = UiTokens.DetailsLabelMargin };
            var editor = (FrameworkElement)cellTemplates.SelectTemplate(cell).LoadContent();
            editor.DataContext = cell;
            // The field takes the pane's width. The template binds the column width and margin when it loads, so they
            // are cleared after that.
            editor.HorizontalAlignment = HorizontalAlignment.Left;
            var host = new Grid();
            host.Children.Add(editor);
            host.SizeChanged += (_, e) => { editor.Width = e.NewSize.Width; editor.Margin = new Thickness(0); };
            detailsFields.Children.Add(label);
            detailsFields.Children.Add(host);
        }
        RefreshDetailsTitle();
    }
    void RefreshDetailsTitle()
    {
        if (detailsRow is not { } view || detailsTitle is null)
            return;
        var key = Schemas.Key(resource, view.Row);
        detailsTitle.Text = (key.Length > 0 ? key : L("RowDetailsNew")) + " · " + DataGridRules.RowStatus(view.Row, tableBaseline, tableConflicts, localization);
    }
    // The pane and the table show the same draft row: an edit in one is shown in the other.
    void SyncEditedRow(Guid id)
    {
        if (Data.FindDraftRow(id) is not { } draft)
            return;
        foreach (var view in visible)
            if (view.Row.LocalId == id && !ReferenceEquals(view.Row, draft))
                view.Rebind(draft);
        if (detailsRow?.Row.LocalId == id)
        {
            if (!ReferenceEquals(detailsRow.Row, draft))
                detailsRow.Rebind(draft);
            RefreshDetailsTitle();
        }
    }
    // After the table was filled again or rows were replaced: the pane follows its record, or closes when the record is
    // not in this resource any more.
    void RefreshDetails()
    {
        if (detailsRow is not { } view)
            return;
        var id = view.Row.LocalId;
        if (!DataViewCurrent() || Data.FindDraftRow(id) is null)
        { CloseDetails(); return; }
        OpenDetails(id);
    }
}
