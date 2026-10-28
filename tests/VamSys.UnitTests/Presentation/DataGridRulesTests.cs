using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class DataGridRulesTests
{
    static DataRow Row(params (string, string)[] fields) => new() { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };
    static readonly LocalizationService English = Language("en-US");
    static LocalizationService Language(string culture) { var l = new LocalizationService(); l.SetLanguage(culture); return l; }

    [Fact]
    public void A_deleted_row_is_read_only_in_every_field_and_mode()
    {
        var deleted = Row(("Name", "x"), ("_delete", "TRUE"));
        foreach (var mode in Enum.GetValues<RunMode>())
            Assert.True(DataGridRules.CellReadOnly(deleted, "Name", mode));
        Assert.True(DataGridRules.CellReadOnly(Row(("_delete", "true")), "Name", RunMode.Offline));
    }

    [Theory]
    [InlineData(RunMode.Online, true, "Parameter IDs", "", true)]
    [InlineData(RunMode.Online, true, "Callsign", "jumpseat", true)]
    [InlineData(RunMode.Online, true, "Flight Number", "jumpseat", true)]
    [InlineData(RunMode.Online, true, "Callsign", "scheduled", false)]
    [InlineData(RunMode.Online, true, "Name", "jumpseat", false)]
    [InlineData(RunMode.Online, false, "Parameter IDs", "", false)]
    [InlineData(RunMode.Demo, true, "Parameter IDs", "", false)]
    [InlineData(RunMode.Offline, true, "Callsign", "jumpseat", false)]
    public void Online_a_row_the_server_knows_keeps_its_parameter_and_jumpseat_identity_fields(RunMode mode, bool known, string field, string type, bool readOnly)
    {
        var row = Row((field, "v"), ("Type", type));
        if (known)
            row = row.WithIdentity(new("101", "7"));
        Assert.Equal(readOnly, DataGridRules.CellReadOnly(row, field, mode));
    }

    [Fact]
    public void The_status_column_names_deletion_conflict_new_draft_and_unchanged_rows_in_that_order()
    {
        var unchanged = Row(("Name", "a"));
        var changed = unchanged.With("Name", "b");
        var deleted = changed.With("_delete", "TRUE");
        var fresh = Row(("Name", "new"));
        var baseline = new Dictionary<Guid, DataRow> { [unchanged.LocalId] = unchanged };
        var none = new HashSet<Guid>();
        Assert.Equal(English.Get("Text_EE96DC1EBE"), DataGridRules.RowStatus(deleted, baseline, new HashSet<Guid> { deleted.LocalId }, English));
        Assert.Equal("Conflict to review", DataGridRules.RowStatus(changed, baseline, new HashSet<Guid> { changed.LocalId }, English));
        Assert.Equal(English.Get("Text_4EF7F62706"), DataGridRules.RowStatus(fresh, baseline, none, English));
        Assert.Equal("Unsubmitted draft", DataGridRules.RowStatus(changed, baseline, none, English));
        Assert.Equal(English.Get("Text_20D2382F10"), DataGridRules.RowStatus(unchanged, baseline, none, English));
        Assert.Equal("Pending deletion", DataGridRules.RowStatus(deleted, baseline, none, English));
    }

    [Fact]
    public void The_fleet_column_describes_known_fleets_names_unknown_tokens_and_has_a_placeholder_when_empty()
    {
        var known = new FleetOption("4", Messages.FromLegacy("Airbus A320"), "A320", Messages.FromLegacy("4"), true, null);
        var labels = new Dictionary<string, FleetOption>(StringComparer.OrdinalIgnoreCase) { ["4"] = known };
        Assert.Equal(known.Describe(English), DataGridRules.FleetLabel("4", labels, English));
        Assert.Equal(known.Describe(English) + "; " + English.Get("Text_C221144914") + "99", DataGridRules.FleetLabel("4, 99, 4", labels, English));
        Assert.Equal(English.Get("Text_F274578907"), DataGridRules.FleetLabel(" ", labels, English));
        Assert.DoesNotContain("[", DataGridRules.FleetLabel("", labels, Language("zh-CN")), StringComparison.Ordinal);
    }
}
