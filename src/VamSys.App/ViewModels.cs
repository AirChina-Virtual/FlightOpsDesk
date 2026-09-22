using CommunityToolkit.Mvvm.ComponentModel;
using VamSys.Core;
using CommunityToolkit.Mvvm.Input;

namespace VamSys.App;
public sealed class CellViewModel : ObservableObject
{
    string value;
    readonly Action<string> commit;
    public string Field { get; }
    public double Width => Field == "_delete" ? 150 : Field is "ID" or "ICAO/IATA" ? 110 : Field is "Fleet ID" or "Fleet IDs" ? 260 : 180;
    public bool IsReadOnly { get; init; }
    public bool IsEnabled => !IsReadOnly;
    public string EditorVisibility => Field is "_delete" or "Fleet ID" or "Fleet IDs" ? "Collapsed" : "Visible";
    public string FleetVisibility => Field is "Fleet ID" or "Fleet IDs" ? "Visible" : "Collapsed";
    public string StatusVisibility => Field == "_delete" ? "Visible" : "Collapsed";
    public Func<string> DisplayText { get; init; } = () => "";
    public string DisplayValue => DisplayText();
    public Func<string> FleetName { get; init; } = () => "";
    public string FleetAutomationName => FleetName();
    public void RefreshLanguage() { OnPropertyChanged(nameof(DisplayValue)); OnPropertyChanged(nameof(FleetAutomationName)); }
    public IAsyncRelayCommand? ChooseFleet { get; init; }
    public Func<bool> CanEdit {get;init;}=()=>true;
    public string Value { get => value; set { if (!CanEdit() || this.value == value) return; commit(value); SetProperty(ref this.value, value); } }
    public CellViewModel(string field, string value, Action<string> commit) { Field = field; this.value = value; this.commit = commit; }
}
public sealed record RowViewModel(DataRow Row, List<CellViewModel> Cells);
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
    public bool Selected
    {
        get => selected;
        set { if (SetProperty(ref selected, value)) { OnPropertyChanged(nameof(CanSelect)); changed(this); } }
    }
    public FleetOptionViewModel(FleetOption option, bool selected, Action<FleetOptionViewModel> changed, ILocalizationService localizer)
    { Option = option; this.selected = selected; this.changed = changed; this.localizer = localizer; }
}
