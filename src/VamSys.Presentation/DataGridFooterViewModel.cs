using CommunityToolkit.Mvvm.ComponentModel;
using VamSys.Core;

namespace VamSys.Presentation;

// The data table footer: paging, page size and selection. The window applies the shown state after every change
// (and after a language change); each property notifies only when its value changes.
public sealed class DataGridFooterViewModel(ILocalizationService language) : ObservableObject
{
    public static readonly int[] PageSizes = [100, 200, 500];

    bool canFirst, canPrevious, canNext, canLast, canGo, canEdit, canSelectPage, canSelectAll, canClearSelection, queryInProgress;
    bool isEmpty;
    string pageText = "", pageSummary = "", selectionSummary = "", pageJumpText = "", emptyText = "";
    object? pageSize;
    int? shownPage;

    public int[] PageSizeChoices => PageSizes;
    public bool CanFirst { get => canFirst; private set => SetProperty(ref canFirst, value); }
    public bool CanPrevious { get => canPrevious; private set => SetProperty(ref canPrevious, value); }
    public bool CanNext { get => canNext; private set => SetProperty(ref canNext, value); }
    public bool CanLast { get => canLast; private set => SetProperty(ref canLast, value); }
    public bool CanGo { get => canGo; private set => SetProperty(ref canGo, value); }
    // The page jump, page size and selection menu.
    public bool CanEdit { get => canEdit; private set => SetProperty(ref canEdit, value); }
    public bool CanSelectPage { get => canSelectPage; private set => SetProperty(ref canSelectPage, value); }
    public bool CanSelectAll { get => canSelectAll; private set => SetProperty(ref canSelectAll, value); }
    public bool CanClearSelection { get => canClearSelection; private set => SetProperty(ref canClearSelection, value); }
    public bool QueryInProgress { get => queryInProgress; private set => SetProperty(ref queryInProgress, value); }
    // The finished query found nothing: the table says so instead of staying blank.
    public bool IsEmpty { get => isEmpty; private set => SetProperty(ref isEmpty, value); }
    public string EmptyText { get => emptyText; private set => SetProperty(ref emptyText, value); }
    // "page / pages"; the full summary is its tooltip and automation name.
    public string PageText { get => pageText; private set => SetProperty(ref pageText, value); }
    public string PageSummary { get => pageSummary; private set => SetProperty(ref pageSummary, value); }
    public string SelectionSummary { get => selectionSummary; private set => SetProperty(ref selectionSummary, value); }
    // The shown page size, boxed for the picker's selected item.
    public object? PageSize { get => pageSize; private set => SetProperty(ref pageSize, value); }
    // What the page jump box holds. It follows the page when the page changes and otherwise keeps what was typed.
    public string PageJumpText { get => pageJumpText; set => SetProperty(ref pageJumpText, value); }

    string first = "", previous = "", next = "", last = "", go = "", size = "", selection = "", selectPage = "", selectAll = "", clear = "", cancel = "", onlySelected = "";
    public string FirstLabel { get => first; private set => SetProperty(ref first, value); }
    public string PreviousLabel { get => previous; private set => SetProperty(ref previous, value); }
    public string NextLabel { get => next; private set => SetProperty(ref next, value); }
    public string LastLabel { get => last; private set => SetProperty(ref last, value); }
    public string GoLabel { get => go; private set => SetProperty(ref go, value); }
    public string PageSizeLabel { get => size; private set => SetProperty(ref size, value); }
    public string SelectionLabel { get => selection; private set => SetProperty(ref selection, value); }
    public string SelectPageLabel { get => selectPage; private set => SetProperty(ref selectPage, value); }
    public string SelectAllLabel { get => selectAll; private set => SetProperty(ref selectAll, value); }
    public string ClearSelectionLabel { get => clear; private set => SetProperty(ref clear, value); }
    public string CancelQueryLabel { get => cancel; private set => SetProperty(ref cancel, value); }
    public string SelectionHint { get => onlySelected; private set => SetProperty(ref onlySelected, value); }

    public void Apply(DataGridAvailability can, DataTableViewModel model, bool querying, string summary, string selected)
    {
        CanFirst = can.First;
        CanPrevious = can.Previous;
        CanNext = can.Next;
        CanLast = can.Last;
        CanGo = can.Go;
        CanEdit = can.Edit;
        CanSelectPage = can.SelectPage;
        CanSelectAll = can.SelectAll;
        CanClearSelection = can.ClearSelection;
        QueryInProgress = querying;
        IsEmpty = !querying && !model.IsDirty && model.Total == 0;
        EmptyText = language.Get("TableEmpty");
        PageText = $"{model.Page} / {model.PageCount}";
        PageSummary = summary;
        SelectionSummary = selected;
        if (!Equals(pageSize, model.PageSize))
            PageSize = model.PageSize;
        if (shownPage != model.Page)
        { shownPage = model.Page; PageJumpText = model.Page.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        FirstLabel = language.Get("TableFirst");
        PreviousLabel = language.Get("TablePrevious");
        NextLabel = language.Get("TableNext");
        LastLabel = language.Get("TableLast");
        GoLabel = language.Get("TableGo");
        PageSizeLabel = language.Get("TablePageSize");
        SelectionLabel = language.Get("TableSelection");
        SelectPageLabel = language.Get("TableSelectPage");
        SelectAllLabel = language.Get("TableSelectAll");
        ClearSelectionLabel = language.Get("TableClearSelection");
        CancelQueryLabel = language.Get("TableCancelQuery");
        SelectionHint = language.Get("BatchOnlySelected");
    }

    // Another state is shown: the jump box shows its page even if the number is the same.
    public void ShowPage(int page)
    { shownPage = page; PageJumpText = page.ToString(System.Globalization.CultureInfo.InvariantCulture); }
}
