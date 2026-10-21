using CommunityToolkit.Mvvm.ComponentModel;
using VamSys.Core;
using CommunityToolkit.Mvvm.Input;

namespace VamSys.App;

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
    public bool IsReadOnly { get => readOnly; set { if (SetProperty(ref readOnly, value)) { OnPropertyChanged(nameof(IsEnabled)); OnPropertyChanged(nameof(TextOpacity)); } } }
    public bool IsEnabled => !IsReadOnly;
    public double TextOpacity => IsReadOnly ? 0.6 : 1;
    public string EditorVisibility => Field is "_delete" or "Fleet ID" or "Fleet IDs" or "Parameter IDs" ? "Collapsed" : "Visible";
    public string FleetVisibility => Field is "Fleet ID" or "Fleet IDs" or "Parameter IDs" ? "Visible" : "Collapsed";
    public string StatusVisibility => Field == "_delete" ? "Visible" : "Collapsed";
    public Func<string> DisplayText { get; init; } = () => "";
    public Func<bool> ShowServerNull { get; init; } = () => false;
    public Func<string> ServerNullLabel { get; init; } = () => "";
    public string VisibleText => Value.Length == 0 && ShowServerNull() ? ServerNullLabel() : Value;
    public Func<DataRow, bool> ReadOnlyForRow { get; init; } = Schemas.Deleted;
    string? lastDisplay, lastFleetName;
    string lastVisible;
    public string DisplayValue => DisplayText();
    public Func<string> FleetName { get; init; } = () => "";
    public string FleetAutomationName => FleetName();
    public void RefreshLanguage()
    {
        RefreshVisibleText();
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
    public CellViewModel(string field, string value, Action<string> commit) { Field = field; this.value = value; lastVisible = value; this.commit = commit; }
}
public sealed partial class RowViewModel(DataRow row, List<CellViewModel> cells)
{
    public DataRow Row { get; private set; } = row;
    public List<CellViewModel> Cells { get; } = cells;
    public object? CellTemplates { get; init; }
    public void Rebind(DataRow row)
    {
        Row = row;
        foreach (var cell in Cells)
            cell.SyncFromSource(row.Get(cell.Field), cell.ReadOnlyForRow(row));
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
