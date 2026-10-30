using CommunityToolkit.Mvvm.ComponentModel;
using VamSys.Core;

namespace VamSys.Presentation;

// A batch editing rule for the selected rows: the column, the kind of change, what to find and the new value. Retiring
// routes changes no chosen column, so the column picker is off for it.
public sealed class BatchRuleDialogViewModel : DialogViewModel, System.ComponentModel.INotifyPropertyChanged
{
    // Labels of the rule kinds, in RuleKind order.
    static readonly string[] KindKeys = ["Text_A30D395090", "Text_5B7341C2D6", "Text_795CBFF909", "Text_6FE8FC1819", "Text_FAC7E021A1", "Text_E79453E164", "Text_6797F1B017"];
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    readonly IReadOnlyList<RuleKind> kinds;
    int kindIndex;

    public BatchRuleDialogViewModel(string targets, IReadOnlyList<string> fields, IReadOnlyList<RuleKind> kinds, ILocalizationService language)
    {
        Targets = targets;
        Fields = fields;
        this.kinds = kinds;
        Kinds = kinds.Select(k => language.Get(KindKeys[(int)k])).ToArray();
        FieldHeader = language.Get("Text_86F623E5CC");
        KindHeader = language.Get("Text_ED31FBB483");
        FindHeader = language.Get("Text_8FAB339859");
        ValueHeader = language.Get("Text_DD5ABC95B1");
    }

    public string Targets { get; }
    public string FieldHeader { get; }
    public string KindHeader { get; }
    public string FindHeader { get; }
    public string ValueHeader { get; }
    public IReadOnlyList<string> Fields { get; }
    public IReadOnlyList<string> Kinds { get; }
    public int FieldIndex { get; set; }
    public int KindIndex
    {
        get => kindIndex;
        set { if (kindIndex == value) return; kindIndex = value; PropertyChanged?.Invoke(this, new(nameof(KindIndex))); PropertyChanged?.Invoke(this, new(nameof(CanChooseField))); }
    }
    public bool CanChooseField => KindIndex >= 0 && KindIndex < kinds.Count && kinds[KindIndex] != RuleKind.Retire;
    public string Find { get; set; } = "";
    public string Value { get; set; } = "";

    // The rule as entered, or null when no kind or column is chosen.
    public EditRule? Rule => KindIndex >= 0 && KindIndex < kinds.Count && FieldIndex >= 0 && FieldIndex < Fields.Count
        ? new EditRule(kinds[KindIndex], Fields[FieldIndex], Value, Find) : null;
}

// One CSV column and the field it is imported into (by default the column's own name).
public sealed class ImportColumnViewModel(string header)
{
    public string Header { get; } = header;
    public string Target { get; set; } = header;
}

// The CSV import: how many rows were read, the column mapping, whether the file is the server baseline, and a sample.
public sealed class ImportMappingDialogViewModel : DialogViewModel
{
    public string Summary { get; init; } = "";
    public string Hint { get; init; } = "";
    public string BaselineLabel { get; init; } = "";
    public bool Baseline { get; set; }
    public bool CanBaseline { get; init; }
    public IReadOnlyList<ImportColumnViewModel> Columns { get; init; } = [];
    public string Sample { get; init; } = "";
    // Each column's target field, trimmed. A column name repeated in the file maps as its last box says.
    public Dictionary<string, string> Mapping
    {
        get
        {
            var mapping = new Dictionary<string, string>();
            foreach (var column in Columns)
                mapping[column.Header] = column.Target.Trim();
            return mapping;
        }
    }
}

// Choosing fleets for the selected aircraft or routes: replace, add or remove (routes only), a search over the fleets and
// one check box per fleet. An aircraft has one fleet, so choosing one there clears the others.
public sealed partial class FleetChoiceDialogViewModel : DialogViewModel, System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    readonly ILocalizationService language;
    readonly bool single;
    readonly List<FleetOptionViewModel> choices = [];
    IReadOnlyList<FleetOptionViewModel> shown;
    string search = "", summary = "";
    int modeIndex;

    public FleetChoiceDialogViewModel(string targets, IEnumerable<FleetOption> options, IEnumerable<string> initial, bool singleFleet, bool canChooseMode, ILocalizationService language)
    {
        this.language = language;
        single = singleFleet;
        Targets = targets;
        CanChooseMode = canChooseMode;
        var chosen = initial.ToHashSet(StringComparer.OrdinalIgnoreCase);
        choices.AddRange(options.Select(o => new FleetOptionViewModel(o, chosen.Contains(o.Token), Changed, language)));
        shown = choices;
        ModeHeader = language.Get("Text_ED31FBB483");
        Modes = [language.Get("Text_DD9F9DBA0C"), language.Get("Text_7A387A9608"), language.Get("Text_58A816EF01")];
        SearchPlaceholder = language.Get("Text_368B16B1B0");
        summary = Describe();
    }

    public string Targets { get; }
    public string ModeHeader { get; }
    public IReadOnlyList<string> Modes { get; }
    public bool CanChooseMode { get; }
    public string SearchPlaceholder { get; }
    // The dialog's content scrolls within this height (the window decides it from its own size).
    public double AvailableHeight { get; init; } = double.PositiveInfinity;
    public IReadOnlyList<FleetOptionViewModel> Choices => choices;
    // The fleets whose label contains the search text.
    public IReadOnlyList<FleetOptionViewModel> Shown => shown;
    public string Summary => summary;
    public int ModeIndex
    {
        get => modeIndex;
        set
        {
            if (modeIndex == value)
                return;
            modeIndex = value;
            // Removing a fleet may name one that is no longer available.
            foreach (var choice in choices)
                choice.AllowRemoval = value == (int)FleetAssignmentMode.Remove;
            PropertyChanged?.Invoke(this, new(nameof(ModeIndex)));
        }
    }
    public string Search
    {
        get => search;
        set
        {
            if (search == value)
                return;
            search = value;
            shown = choices.Where(c => c.Label.Contains(value, StringComparison.OrdinalIgnoreCase)).ToList();
            PropertyChanged?.Invoke(this, new(nameof(Search)));
            PropertyChanged?.Invoke(this, new(nameof(Shown)));
        }
    }
    public FleetAssignmentMode Mode => (FleetAssignmentMode)ModeIndex;
    public IEnumerable<string> SelectedTokens => choices.Where(c => c.Selected).Select(c => c.Option.Token);

    void Changed(FleetOptionViewModel choice)
    {
        if (single && choice.Selected)
            foreach (var other in choices.Where(c => c != choice && c.Selected))
                other.Selected = false;
        summary = Describe();
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
    }
    string Describe() => language.Get("Text_D18ED1A023") + string.Join("；", choices.Where(c => c.Selected).Select(c => c.Label));
}
