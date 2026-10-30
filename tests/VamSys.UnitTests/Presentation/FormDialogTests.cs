using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class FormDialogTests
{
    static LocalizationService English() { var language = new LocalizationService(); language.SetLanguage("en-US"); return language; }
    static FleetOption Fleet(string token, string name, bool available = true) => new(token, Messages.FromLegacy(name), "A320", Messages.FromLegacy(token), available, null);

    [Fact]
    public void A_batch_rule_is_built_from_the_chosen_kind_and_column_and_retiring_turns_the_column_picker_off()
    {
        var language = English();
        var form = new BatchRuleDialogViewModel("2 rows", ["Callsign", "Remarks"], [RuleKind.Set, RuleKind.Replace, RuleKind.Retire], language);
        Assert.Equal([language.Get("Text_A30D395090"), language.Get("Text_5B7341C2D6"), language.Get("Text_6797F1B017")], form.Kinds);
        Assert.True(form.CanChooseField);
        var changed = new List<string?>();
        form.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        form.KindIndex = 1;
        form.FieldIndex = 1;
        form.Find = "old";
        form.Value = "new";
        Assert.Equal(new EditRule(RuleKind.Replace, "Remarks", "new", "old"), form.Rule);

        form.KindIndex = 2;
        Assert.False(form.CanChooseField);
        Assert.Contains(nameof(BatchRuleDialogViewModel.CanChooseField), changed);

        form.KindIndex = -1;
        Assert.Null(form.Rule);
        Assert.False(form.CanChooseField);
    }

    [Fact]
    public void An_import_maps_each_column_to_its_trimmed_target_and_a_repeated_column_name_to_its_last_box()
    {
        var columns = new[] { new ImportColumnViewModel("Name"), new ImportColumnViewModel("Code"), new ImportColumnViewModel("Code") };
        columns[0].Target = "  Callsign ";
        columns[2].Target = "Route Code";
        var form = new ImportMappingDialogViewModel { Columns = columns };
        Assert.Equal(new Dictionary<string, string> { ["Name"] = "Callsign", ["Code"] = "Route Code" }, form.Mapping);
        Assert.Equal("Code", columns[1].Target);
    }

    [Fact]
    public void Choosing_a_fleet_for_aircraft_clears_the_others_and_the_summary_names_the_chosen_ones()
    {
        var language = English();
        var form = new FleetChoiceDialogViewModel("1 row", [Fleet("1", "Airbus A320"), Fleet("2", "Boeing 737")], ["1"], singleFleet: true, canChooseMode: false, language);
        Assert.Equal(["1"], form.SelectedTokens);
        Assert.EndsWith("Airbus A320 · A320 · 1", form.Summary, StringComparison.Ordinal);
        var changed = new List<string?>();
        form.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        form.Choices[1].Selected = true;
        Assert.Equal(["2"], form.SelectedTokens);
        Assert.False(form.Choices[0].Selected);
        Assert.Contains(nameof(FleetChoiceDialogViewModel.Summary), changed);
        Assert.Contains("Boeing 737", form.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Airbus", form.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Routes_keep_several_fleets_search_filters_the_list_and_removing_allows_unavailable_fleets()
    {
        var language = English();
        var gone = Fleet("3", "Retired type", available: false);
        var form = new FleetChoiceDialogViewModel("3 rows", [Fleet("1", "Airbus A320"), Fleet("2", "Boeing 737"), gone], [], singleFleet: false, canChooseMode: true, language);
        form.Choices[0].Selected = true;
        form.Choices[1].Selected = true;
        Assert.Equal(["1", "2"], form.SelectedTokens);

        var changed = new List<string?>();
        form.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        form.Search = "boeing";
        Assert.Equal([form.Choices[1]], form.Shown);
        Assert.Contains(nameof(FleetChoiceDialogViewModel.Shown), changed);
        form.Search = "";
        Assert.Equal(3, form.Shown.Count);

        Assert.False(form.Choices[2].CanSelect);
        form.ModeIndex = (int)FleetAssignmentMode.Remove;
        Assert.Equal(FleetAssignmentMode.Remove, form.Mode);
        Assert.True(form.Choices[2].CanSelect);
        form.ModeIndex = (int)FleetAssignmentMode.Add;
        Assert.False(form.Choices[2].CanSelect);
        Assert.Equal(3, form.Modes.Count);
    }
}
