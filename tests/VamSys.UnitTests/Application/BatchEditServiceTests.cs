using VamSys.Core;

namespace VamSys.UnitTests.Application;

public class BatchEditServiceTests
{
    sealed class Fixture
    {
        public readonly FakeStorage Storage = new();
        public readonly WorkspaceSession Session;
        public readonly BatchEditService Service;
        public int Invalidations;
        public Fixture()
        {
            Storage.Seed();
            Session = new(Storage, new InteractionGate(), new FakeTimer());
            Session.Open("default");
            Session.DataInvalidated += () => Invalidations++;
            Service = new(Session);
        }
        public Workspace W => Session.Workspace;
        public ResourceData Fleets => W.Resources[ResourceKind.Fleets];
        public ResourceData Aircraft => W.Resources[ResourceKind.Aircraft];
        public ResourceData Routes => W.Resources[ResourceKind.Routes];
        public ResourceData Airports => W.Resources[ResourceKind.Airports];
        public int Saves => Storage.Calls.Count(c => c == "journal:All");
        // Writes memory to the fake database so a later failed save can be compared with it.
        public void Persist() => Storage.Stored[W.Id] = W.Serialize();
        public ResourceData Stored(ResourceKind kind) => Storage.Read(W.Id).Resources[kind];
    }

    static DataRow Row(params (string, string)[] fields)
    {
        var values = fields.ToDictionary(f => f.Item1, f => f.Item2);
        values.TryAdd("_delete", "FALSE");
        return new() { Fields = values };
    }

    // --- Delete and restore ---

    [Fact]
    public async Task Deleting_removes_new_drafts_marks_existing_rows_and_saves_once()
    {
        var f = new Fixture();
        var added = Row(("ICAO/IATA", "NEW"), ("Name", "new"));
        var existing = Row(("ICAO/IATA", "OLD"), ("Name", "old"), ("ID", "5"));
        f.Airports.Snapshot.Add(existing.Dup());
        f.Airports.Draft.AddRange([added, existing]);
        var proposal = f.Service.ProposeDelete(ResourceKind.Airports, [added, existing]);
        Assert.False(proposal.NothingToDo);
        Assert.Empty(proposal.Issues);
        Assert.Equal(2, proposal.Ids.Count);
        Assert.Single(proposal.Plan!.RemoveDrafts);
        Assert.Single(proposal.Plan.MarkExisting);
        await f.Service.ApplyDeleteAsync(proposal);
        Assert.Null(f.Airports.FindDraftRow(added.LocalId));
        Assert.True(Schemas.Deleted(f.Airports.FindDraftRow(existing.LocalId)!));
        Assert.Equal(1, f.Saves);
        Assert.Equal(1, f.Invalidations);
    }

    [Fact]
    public void Nothing_selected_or_nothing_to_delete_proposes_nothing_and_changes_nothing()
    {
        var f = new Fixture();
        Assert.True(f.Service.ProposeDelete(ResourceKind.Airports, []).NothingToDo);
        var marked = Row(("ICAO/IATA", "OLD"), ("ID", "5"), ("_delete", "TRUE"));
        f.Airports.Snapshot.Add(marked.Dup());
        f.Airports.Draft.Add(marked);
        var proposal = f.Service.ProposeDelete(ResourceKind.Airports, [marked]);
        Assert.True(proposal.NothingToDo);
        Assert.Equal(0, f.Saves);
        Assert.Empty(f.Airports.Undo);
    }

    [Fact]
    public void A_record_other_records_still_use_cannot_be_deleted()
    {
        var f = new Fixture();
        var fleet = Row(("ID", "1"), ("Name", "Alpha"), ("Type Code", "A320"));
        f.Fleets.Snapshot.Add(fleet.Dup());
        f.Fleets.Draft.Add(fleet);
        f.Aircraft.Draft.Add(Row(("ID", "9"), ("Fleet ID", "1")));
        var proposal = f.Service.ProposeDelete(ResourceKind.Fleets, [fleet]);
        Assert.False(proposal.NothingToDo);
        Assert.NotEmpty(proposal.Issues);
        Assert.Empty(proposal.Ids);
        Assert.False(Schemas.Deleted(f.Fleets.Draft[0]));
    }

    [Fact]
    public async Task A_failed_save_after_deleting_surfaces_and_the_database_keeps_the_rows()
    {
        var f = new Fixture();
        var added = Row(("ICAO/IATA", "NEW"));
        f.Airports.Draft.Add(added);
        f.Persist();
        var proposal = f.Service.ProposeDelete(ResourceKind.Airports, [added]);
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Service.ApplyDeleteAsync(proposal));
        Assert.Single(f.Stored(ResourceKind.Airports).Draft);
    }

    [Fact]
    public async Task Restoring_clears_the_deletion_marker_only_on_deleted_rows()
    {
        var f = new Fixture();
        var deleted = Row(("ICAO/IATA", "A"), ("ID", "1"), ("_delete", "TRUE"));
        var active = Row(("ICAO/IATA", "B"), ("ID", "2"));
        f.Airports.Snapshot.AddRange([deleted.Dup(), active.Dup()]);
        f.Airports.Draft.AddRange([deleted, active]);
        var proposal = f.Service.ProposeRestore([deleted, active]);
        Assert.Equal(1, proposal.Selection.Skipped);
        Assert.Single(proposal.Changes);
        await f.Service.ApplyRestoreAsync(ResourceKind.Airports, proposal);
        Assert.False(Schemas.Deleted(f.Airports.FindDraftRow(deleted.LocalId)!));
        Assert.Equal(1, f.Saves);
        Assert.Empty(f.Service.ProposeRestore([active]).Changes);
    }

    // --- Rules and retirement ---

    [Fact]
    public void Only_routes_can_be_retired_and_fleet_and_deletion_columns_are_not_editable_by_rule()
    {
        var f = new Fixture();
        Assert.Contains(RuleKind.Retire, f.Service.RuleKinds(ResourceKind.Routes));
        Assert.DoesNotContain(RuleKind.Retire, f.Service.RuleKinds(ResourceKind.Airports));
        Assert.Equal(["ID", "Name"], f.Service.RuleFields(["_delete", "ID", "Fleet ID", "Name", "Fleet IDs"]));
    }

    [Fact]
    public async Task A_rule_edit_previews_without_touching_rows_then_applies_and_saves_once()
    {
        var f = new Fixture();
        var a = Row(("ICAO/IATA", "A"), ("Name", "old"));
        var b = Row(("ICAO/IATA", "B"), ("Name", "same"));
        f.Airports.Draft.AddRange([a, b]);
        var selection = f.Service.Capture([a, b]);
        var preview = await f.Service.PreviewRuleAsync(selection, new EditRule(RuleKind.Set, "Name", "same", ""));
        Assert.Single(preview.Changes);
        Assert.Equal("old", f.Airports.FindDraftRow(a.LocalId)!.Get("Name"));
        Assert.Equal(0, f.Saves);
        await f.Service.ApplyEditAsync(ResourceKind.Airports, preview);
        Assert.Equal("same", f.Airports.FindDraftRow(a.LocalId)!.Get("Name"));
        Assert.Equal(1, f.Saves);
        Assert.Single(f.Airports.Undo);
    }

    [Fact]
    public async Task A_rule_that_changes_nothing_saves_nothing()
    {
        var f = new Fixture();
        var a = Row(("ICAO/IATA", "A"), ("Name", "same"));
        f.Airports.Draft.Add(a);
        var preview = await f.Service.PreviewRuleAsync(f.Service.Capture([a]), new EditRule(RuleKind.Set, "Name", "same", ""));
        Assert.Empty(preview.Changes);
        await f.Service.ApplyEditAsync(ResourceKind.Airports, preview);
        Assert.Empty(f.Airports.Undo);
    }

    [Fact]
    public async Task A_failed_save_after_a_rule_edit_surfaces_and_the_database_keeps_the_old_values()
    {
        var f = new Fixture();
        var a = Row(("ICAO/IATA", "A"), ("Name", "old"));
        f.Airports.Draft.Add(a);
        f.Persist();
        var preview = await f.Service.PreviewRuleAsync(f.Service.Capture([a]), new EditRule(RuleKind.Set, "Name", "new", ""));
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Service.ApplyEditAsync(ResourceKind.Airports, preview));
        Assert.Equal("old", f.Stored(ResourceKind.Airports).Draft[0].Get("Name"));
    }

    [Fact]
    public async Task Retirement_sets_an_end_date_two_days_back_on_routes_only()
    {
        var f = new Fixture();
        var route = Row(("ID", "1"), ("Callsign", "X1"), ("End Date", ""));
        f.Routes.Draft.Add(route);
        Assert.Null(f.Service.ProposeRetire(ResourceKind.Airports, [route]));
        var proposal = f.Service.ProposeRetire(ResourceKind.Routes, [route])!;
        var end = Assert.Single(proposal.Preview!.Changes).Get("End Date");
        Assert.Equal(19, end.Length);
        Assert.True(DateTime.Parse(end, System.Globalization.CultureInfo.InvariantCulture) < DateTime.UtcNow.AddDays(-1.9));
        await f.Service.ApplyEditAsync(ResourceKind.Routes, proposal.Preview);
        Assert.Equal(end, f.Routes.FindDraftRow(route.LocalId)!.Get("End Date"));
        Assert.Null(f.Service.ProposeRetire(ResourceKind.Routes, [])!.Preview);
    }

    // --- Fleets ---

    [Fact]
    public async Task Assigning_a_fleet_previews_the_new_value_then_applies_and_saves()
    {
        var f = new Fixture();
        f.Fleets.Draft.AddRange([Row(("ID", "1"), ("Name", "Alpha"), ("Type Code", "A320")), Row(("ID", "2"), ("Name", "Beta"), ("Type Code", "B738"))]);
        var aircraft = Row(("ID", "9"), ("Registration", "B-1"), ("Fleet ID", "1"));
        f.Aircraft.Draft.Add(aircraft);
        var start = f.Service.BeginFleetAssignment(ResourceKind.Aircraft, [aircraft])!;
        Assert.Equal("Fleet ID", start.Field);
        Assert.Equal(["1"], start.Initial);
        Assert.Equal(["1", "2"], start.Options.Select(o => o.Token).Order());
        var preview = f.Service.PreviewFleets(ResourceKind.Aircraft, start.Selection.Eligible, ["2"], FleetAssignmentMode.Replace);
        Assert.Empty(preview.Issues);
        Assert.Equal("1", f.Aircraft.Draft[0].Get("Fleet ID"));
        await f.Service.ApplyFleetsAsync(ResourceKind.Aircraft, preview);
        Assert.Equal("2", f.Aircraft.Draft[0].Get("Fleet ID"));
        Assert.Equal(1, f.Saves);
    }

    [Fact]
    public void An_aircraft_needs_exactly_one_fleet_and_deleted_rows_cannot_be_assigned()
    {
        var f = new Fixture();
        f.Fleets.Draft.Add(Row(("ID", "1"), ("Name", "Alpha"), ("Type Code", "A320")));
        var aircraft = Row(("ID", "9"), ("Fleet ID", "1"));
        f.Aircraft.Draft.Add(aircraft);
        var none = f.Service.PreviewFleets(ResourceKind.Aircraft, [aircraft], [], FleetAssignmentMode.Replace);
        Assert.NotEmpty(none.Issues);
        var gone = Row(("ID", "10"), ("_delete", "TRUE"));
        Assert.Null(f.Service.BeginFleetAssignment(ResourceKind.Aircraft, [gone]));
        Assert.Null(f.Service.BeginFleetAssignment(ResourceKind.Aircraft, []));
    }

    [Fact]
    public async Task A_failed_save_after_assigning_a_fleet_surfaces_and_the_database_keeps_the_old_fleet()
    {
        var f = new Fixture();
        f.Fleets.Draft.AddRange([Row(("ID", "1"), ("Name", "Alpha")), Row(("ID", "2"), ("Name", "Beta"))]);
        var aircraft = Row(("ID", "9"), ("Fleet ID", "1"));
        f.Aircraft.Draft.Add(aircraft);
        f.Persist();
        var preview = f.Service.PreviewFleets(ResourceKind.Aircraft, [aircraft], ["2"], FleetAssignmentMode.Replace);
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Service.ApplyFleetsAsync(ResourceKind.Aircraft, preview));
        Assert.Equal("1", f.Stored(ResourceKind.Aircraft).Draft[0].Get("Fleet ID"));
    }

    // --- Adding a row and history ---

    [Fact]
    public async Task Adding_a_row_creates_an_empty_active_row_with_the_resource_columns_and_one_undo_step()
    {
        var f = new Fixture();
        await f.Service.AddRowAsync(ResourceKind.Airports);
        var row = Assert.Single(f.Airports.Draft);
        Assert.Equal("FALSE", row.Get("_delete"));
        Assert.All(Schemas.All[ResourceKind.Airports].Columns, c => Assert.Contains(c, f.Airports.Columns));
        Assert.Single(f.Airports.Undo);
        Assert.Equal(1, f.Saves);
    }

    [Fact]
    public async Task A_failed_save_after_adding_a_row_surfaces_and_the_database_has_no_row()
    {
        var f = new Fixture();
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Service.AddRowAsync(ResourceKind.Airports));
        Assert.Empty(f.Stored(ResourceKind.Airports).Draft);
    }

    [Fact]
    public async Task Undoing_a_cell_edit_saves_only_the_rows_and_undoing_an_addition_takes_a_full_save()
    {
        var f = new Fixture();
        var row = Row(("ICAO/IATA", "A"), ("Name", "before"));
        f.Airports.Draft.Add(row);
        f.Airports.Edit([row.LocalId], () => f.Airports.ReplaceDraftRows([row.With("Name", "after")]));
        var move = f.Service.MoveHistory(ResourceKind.Airports, undo: true);
        Assert.True(move.Moved);
        Assert.NotNull(move.ReplacedRows);
        Assert.Equal("before", f.Airports.FindDraftRow(row.LocalId)!.Get("Name"));
        await f.Service.SaveMoveAsync(ResourceKind.Airports, move);
        Assert.Equal("rows", f.Storage.Calls.Last());

        await f.Service.AddRowAsync(ResourceKind.Airports);
        var calls = f.Storage.Calls.Count;
        var undoAdd = f.Service.MoveHistory(ResourceKind.Airports, undo: true);
        Assert.True(undoAdd.Moved);
        Assert.Null(undoAdd.ReplacedRows);
        await f.Service.SaveMoveAsync(ResourceKind.Airports, undoAdd);
        Assert.Equal("journal:All", f.Storage.Calls.Last());
        Assert.Equal(calls + 1, f.Storage.Calls.Count);
    }

    [Fact]
    public async Task Nothing_to_undo_moves_and_saves_nothing()
    {
        var f = new Fixture();
        var move = f.Service.MoveHistory(ResourceKind.Airports, undo: true);
        Assert.False(move.Moved);
        await f.Service.SaveMoveAsync(ResourceKind.Airports, move);
        Assert.DoesNotContain(f.Storage.Calls, c => c is "rows" or "journal:All");
    }
}
