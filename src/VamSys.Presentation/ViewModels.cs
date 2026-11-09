using CommunityToolkit.Mvvm.ComponentModel;
using VamSys.Core;
using CommunityToolkit.Mvvm.Input;

namespace VamSys.Presentation;

public sealed partial class CellViewModel : ObservableObject
{
    string value;
    readonly Action<string> commit;
    public string Field { get; }
    public double Width => Field == "_delete" ? 150 : Field is "ID" or "ICAO/IATA" ? 110 : Field is "Fleet ID" or "Fleet IDs" or "Parameter IDs" ? 260 : 180;
    // Table density: set when the page's cell models are created, and rebuilt with them.
    public double Height { get; init; } = 32;
    public bool Compact { get; init; }
    bool readOnly;
    public bool IsReadOnly { get => readOnly; set { if (SetProperty(ref readOnly, value)) { OnPropertyChanged(nameof(IsEnabled)); OnPropertyChanged(nameof(TextOpacity)); RefreshState(); } } }
    public bool IsEnabled => !IsReadOnly;
    public double TextOpacity => IsReadOnly ? 0.6 : 1;
    public string EditorVisibility => Field is "_delete" or "Fleet ID" or "Fleet IDs" or "Parameter IDs" ? "Collapsed" : "Visible";
    public string FleetVisibility => Field is "Fleet ID" or "Fleet IDs" or "Parameter IDs" ? "Visible" : "Collapsed";
    public string StatusVisibility => Field == "_delete" ? "Visible" : "Collapsed";
    public Func<string> DisplayText { get; init; } = () => "";
    public Func<bool> ShowServerNull { get; init; } = () => false;
    public Func<string> ServerNullLabel { get; init; } = () => "";
    public string VisibleText => Value.Length == 0 && ShowServerNull() ? ServerNullLabel()
        : Type == FieldType.Bool ? Value switch { "TRUE" => "☑ TRUE", "FALSE" => "☐ FALSE", _ => Value } : Value;
    // How the value is entered: the column's type from the field table, and the values it takes when it has a fixed set.
    public FieldType Type { get; }
    public IReadOnlyList<string> Choices { get; init; } = [];
    // Values offered while typing (airports for an airport column, tags already in use for tags), best first.
    public Func<string, IReadOnlyList<CellSuggestion>>? Suggest { get; init; }
    public bool Required { get; init; }
    // Texts of the editors' pickers: a weekday's name, and a resource text by key.
    public Func<string, string> DayName { get; init; } = day => day;
    public Func<string, string> Label { get; init; } = key => key;
    // What the checks found in this cell, the value the server has when it differs, and why a read-only cell is.
    MessageDescriptor? issue;
    string? original;
    public Func<MessageDescriptor, string> Format { get; init; } = m => m.ToString() ?? "";
    public Func<string> ReadOnlyReason { get; init; } = () => "";
    public Func<string, string> OriginalLabel { get; init; } = value => value;
    public bool HasIssue => issue != null;
    public bool IsChanged => original != null;
    // The server's value of a changed cell, null when the cell is unchanged or the record is new.
    public string? ServerValue => original;
    // 0 plain, 1 changed, 2 has a problem: the cell's look.
    public int Look => issue != null ? 2 : original != null ? 1 : 0;
    public string? Hint => issue is { } found ? Format(found) : original != null ? OriginalLabel(original) : IsReadOnly && Field != "_delete" ? NullIfEmpty(ReadOnlyReason()) : null;
    static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
    string? lastHint;
    int lastLook;
    public void ShowState(MessageDescriptor? found, string? before)
    {
        issue = found;
        original = before;
        RefreshState();
    }
    void RefreshState()
    {
        var look = Look;
        if (look != lastLook)
        { lastLook = look; OnPropertyChanged(nameof(Look)); }
        var hint = Hint;
        if (hint != lastHint)
        { lastHint = hint; OnPropertyChanged(nameof(Hint)); }
    }
    // An edit session ended (reverted when Escape took it back); multi-cell paste and fill down act from this cell.
    public Action<bool>? EditEnded { get; init; }
    public Func<string, Task>? Paste { get; init; }
    public Func<Task>? FillDown { get; init; }
    public Action? OpenDetails { get; init; }
    public Func<DataRow, bool> ReadOnlyForRow { get; init; } = Schemas.Deleted;
    string? lastDisplay, lastFleetName;
    string lastVisible;
    public string DisplayValue => DisplayText();
    public Func<string> FleetName { get; init; } = () => "";
    public string FleetAutomationName => FleetName();
    public void RefreshLanguage()
    {
        RefreshVisibleText();
        RefreshState();
        if (Field is "_delete" or "Fleet ID" or "Fleet IDs" or "Parameter IDs")
        {
            var text = DisplayText();
            if (lastDisplay != text)
            { lastDisplay = text; OnPropertyChanged(nameof(DisplayValue)); }
        }
        if (Field is "Fleet ID" or "Fleet IDs" or "Parameter IDs")
        {
            var name = FleetName();
            if (lastFleetName != name)
            { lastFleetName = name; OnPropertyChanged(nameof(FleetAutomationName)); }
        }
    }
    void RefreshVisibleText()
    { var visible = VisibleText; if (lastVisible != visible) { lastVisible = visible; OnPropertyChanged(nameof(VisibleText)); } }
    public void SyncFromSource(string text, bool isReadOnly)
    { SetProperty(ref value, text, nameof(Value)); IsReadOnly = isReadOnly; RefreshLanguage(); }
    public IAsyncRelayCommand? ChooseFleet { get; init; }
    public Func<bool> CanEdit { get; init; } = () => true;
    public string Value { get => value; set { if (IsReadOnly || !CanEdit() || this.value == value) return; commit(value); SetProperty(ref this.value, value); RefreshVisibleText(); } }
    public CellViewModel(string field, string value, Action<string> commit)
    {
        Field = field; this.value = value; this.commit = commit;
        Type = FieldSpecs.Column(field)?.Type ?? FieldType.Text;
        lastVisible = VisibleText;
    }
}
// A value offered while typing in a cell: what is entered, and what is shown beside it.
public sealed record CellSuggestion(string Value, string Label, string? Shown = null)
{
    public override string ToString() => Shown ?? (Label.Length == 0 ? Value : Value + "  ·  " + Label);
}
public sealed partial class RowViewModel(DataRow row, List<CellViewModel> cells)
{
    public DataRow Row { get; private set; } = row;
    public List<CellViewModel> Cells { get; } = cells;
    public object? CellTemplates { get; init; }
    // Checks the row and marks its cells; set by the grid that owns the row.
    public Action<RowViewModel>? Check { get; set; }
    // The row object was replaced by an edit; the cells already show its values.
    public void ReplaceRow(DataRow row) { Row = row; Check?.Invoke(this); }
    public void Rebind(DataRow row)
    {
        Row = row;
        foreach (var cell in Cells)
            cell.SyncFromSource(row.Get(cell.Field), cell.ReadOnlyForRow(row));
        Check?.Invoke(this);
    }
    public override string ToString() => nameof(RowViewModel) + " " + Row.LocalId;
}
public sealed record WorkspaceChoice(Guid Id, string Name);

public sealed class FleetOptionViewModel : ObservableObject
{
    bool selected;
    readonly Action<FleetOptionViewModel> changed;
    public FleetOption Option { get; }
    readonly ILocalizationService localizer;
    public string Label => Option.Describe(localizer);
    bool allowRemoval;
    public bool AllowRemoval { get => allowRemoval; set { if (SetProperty(ref allowRemoval, value)) OnPropertyChanged(nameof(CanSelect)); } }
    public bool CanSelect => Option.Available || Selected || AllowRemoval;
    public bool Selected {
        get => selected;
        set { if (SetProperty(ref selected, value)) { OnPropertyChanged(nameof(CanSelect)); changed(this); } }
    }
    public FleetOptionViewModel(FleetOption option, bool selected, Action<FleetOptionViewModel> changed, ILocalizationService localizer)
    { Option = option; this.selected = selected; this.changed = changed; this.localizer = localizer; }
}
