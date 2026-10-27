using VamSys.Core;
using static VamSys.UnitTests.Domain.TestRows;

namespace VamSys.UnitTests.Domain;

/// <summary>
/// F04: three-way merge of a fresh remote snapshot into the local draft. Per field the three values are
/// original (the previous snapshot), mine (the draft) and theirs (the new remote value).
/// Also the regression for A1 (two remote airports sharing one IATA code made the whole refresh fail).
/// </summary>
public class SnapshotMergerTests
{
    const ResourceKind Fleets = ResourceKind.Fleets;
    const ResourceKind Airports = ResourceKind.Airports;

    static DataRow Remote(string id, params (string Field, string Value)[] fields)
    {
        return Row([("ID", id), .. fields]).WithIdentity(new(id, "7"));
    }

    // A refreshed fleet whose draft may differ from its snapshot: original / mine as given, then the remote brings theirs.
    static (ResourceData Data, DataRow Local) Existing(string original, string mine)
    {
        var baseline = Remote("1", ("Name", original));
        var data = new ResourceData { Snapshot = [baseline.Dup()], Draft = [baseline.With("Name", mine)] };
        return (data, data.Draft[0]);
    }

    // Original / mine / theirs of one field → what the draft keeps, and whether a conflict is recorded.
    [Theory]
    [InlineData("a", "a", "a", "a", false)]
    [InlineData("a", "a", "b", "b", false)]
    [InlineData("a", "b", "a", "b", false)]
    [InlineData("a", "b", "b", "b", false)]
    [InlineData("a", "b", "c", "b", true)]
    [InlineData("a", "", "c", "", true)]
    [InlineData("a", "b", "", "b", true)]
    [InlineData("", "b", "c", "b", true)]
    [InlineData("", "", "c", "c", false)]
    public void A_field_follows_the_three_way_rule(string original, string mine, string theirs, string kept, bool conflict)
    {
        var (data, local) = Existing(original, mine);

        SnapshotMerger.Merge(Fleets, data, [Remote("1", ("Name", theirs))]);

        var row = data.Draft.Single();
        Assert.Equal(kept, row.Get("Name"));
        Assert.Equal(local.LocalId, row.LocalId);
        if (conflict)
            Assert.Equal([new MergeConflict(local.LocalId, "Name", original, mine, theirs)], data.Conflicts);
        else
            Assert.Empty(data.Conflicts);
        Assert.Equal(theirs, data.Snapshot.Single().Get("Name"));
    }

    [Fact]
    public void Each_field_is_decided_on_its_own()
    {
        var baseline = Remote("1", ("Name", "n"), ("Remarks", "r"), ("Type Code", "t"));
        var data = new ResourceData { Snapshot = [baseline.Dup()], Draft = [baseline.With("Name", "mine")] };

        SnapshotMerger.Merge(Fleets, data, [Remote("1", ("Name", "n"), ("Remarks", "theirs"), ("Type Code", "t"))]);

        var row = data.Draft.Single();
        Assert.Equal("mine", row.Get("Name"));
        Assert.Equal("theirs", row.Get("Remarks"));
        Assert.Empty(data.Conflicts);
    }

    [Fact]
    public void Columns_that_exist_only_in_the_draft_are_kept()
    {
        var baseline = Remote("1", ("Name", "n"), ("CSV only", "x"));
        var data = new ResourceData { Snapshot = [baseline.Dup()], Draft = [baseline.Dup()] };

        SnapshotMerger.Merge(Fleets, data, [Remote("1", ("Name", "n"))]);

        Assert.Equal("x", data.Draft.Single().Get("CSV only"));
        Assert.Empty(data.Conflicts);
    }

    [Fact]
    public void A_remote_row_with_a_new_id_is_added_as_it_is_and_local_new_rows_stay()
    {
        var added = Row(("Name", "local new"));
        var data = new ResourceData { Draft = [added] };

        SnapshotMerger.Merge(Fleets, data, [Remote("5", ("Name", "remote new"))]);

        Assert.Equal(["local new", "remote new"], data.Draft.Select(r => r.Get("Name")));
        Assert.Contains(data.Draft, r => r.LocalId == added.LocalId);
        Assert.Empty(data.Conflicts);
    }

    [Fact]
    public void A_row_deleted_remotely_is_not_recreated_when_the_draft_never_changed_it()
    {
        var (data, _) = Existing("n", "n");

        SnapshotMerger.Merge(Fleets, data, []);

        Assert.Empty(data.Draft);
        Assert.Empty(data.Snapshot);
        Assert.Empty(data.Conflicts);
    }

    [Fact]
    public void A_row_deleted_remotely_but_edited_locally_stays_with_a_conflict_on_the_whole_row()
    {
        var (data, local) = Existing("n", "edited");

        SnapshotMerger.Merge(Fleets, data, []);

        Assert.Equal("edited", data.Draft.Single().Get("Name"));
        Assert.Equal([new MergeConflict(local.LocalId, "*", "exists", "edited", null)], data.Conflicts);
    }

    [Fact]
    public void A_row_deleted_remotely_and_marked_for_deletion_locally_just_disappears()
    {
        var baseline = Remote("1", ("Name", "n"));
        var data = new ResourceData { Snapshot = [baseline.Dup()], Draft = [baseline.With("_delete", "TRUE")] };

        SnapshotMerger.Merge(Fleets, data, []);

        Assert.Empty(data.Draft);
        Assert.Empty(data.Conflicts);
    }

    [Theory]
    [InlineData("n", false)]
    [InlineData("changed remotely", true)]
    public void A_local_deletion_conflicts_only_when_the_remote_row_changed_meanwhile(string remoteName, bool conflict)
    {
        var baseline = Remote("1", ("Name", "n"));
        var data = new ResourceData { Snapshot = [baseline.Dup()], Draft = [baseline.With("_delete", "TRUE")] };

        SnapshotMerger.Merge(Fleets, data, [Remote("1", ("Name", remoteName))]);

        Assert.Equal("TRUE", data.Draft.Single().Get("_delete"));
        Assert.Equal(conflict ? [new MergeConflict(data.Draft[0].LocalId, "_delete", "FALSE", "TRUE", "FALSE")] : [], data.Conflicts);
    }

    [Fact]
    public void An_imported_candidate_matches_the_remote_row_by_key_and_differing_values_need_review()
    {
        var candidate = Row(("ID", "9"), ("Name", "mine"), ("Remarks", "same"));
        var data = new ResourceData { Draft = [candidate] };

        SnapshotMerger.Merge(Fleets, data, [Remote("9", ("Name", "theirs"), ("Remarks", "same"))]);

        var row = data.Draft.Single();
        Assert.Equal(candidate.LocalId, row.LocalId);
        Assert.Equal("mine", row.Get("Name"));
        Assert.Equal("9", row.Identity!.RemoteId);
        Assert.Equal([new MergeConflict(candidate.LocalId, "Name", null, "mine", "theirs")], data.Conflicts);
    }

    [Fact]
    public void Unresolved_conflicts_survive_another_refresh_for_rows_that_are_still_there()
    {
        var (data, local) = Existing("a", "b");
        SnapshotMerger.Merge(Fleets, data, [Remote("1", ("Name", "c"))]);
        Assert.Single(data.Conflicts);

        // The second refresh brings the same remote value as the snapshot now holds: no new disagreement, the old one stays.
        SnapshotMerger.Merge(Fleets, data, [Remote("1", ("Name", "c"))]);
        Assert.Equal([new MergeConflict(local.LocalId, "Name", "a", "b", "c")], data.Conflicts);

        data.Conflicts = [.. data.Conflicts, new MergeConflict(Guid.NewGuid(), "Name", "x", "y", "z")];
        SnapshotMerger.Merge(Fleets, data, [Remote("1", ("Name", "c"))]);
        Assert.DoesNotContain(data.Conflicts, c => c.Original == "x");
    }

    [Fact]
    public void A_refresh_replaces_the_snapshot_with_copies_stamps_the_time_and_clears_the_undo_history()
    {
        var (data, _) = Existing("a", "a");
        data.Change(data.Draft[0].LocalId, "Name", "typed");
        Assert.Single(data.Undo);
        var incoming = Remote("1", ("Name", "a"), ("New remote column", "v"));

        SnapshotMerger.Merge(Fleets, data, [incoming]);

        Assert.Empty(data.Undo);
        Assert.Empty(data.Redo);
        Assert.NotNull(data.SnapshotAt);
        Assert.NotSame(incoming, data.Snapshot.Single());
        Assert.Equal(data.Draft.Single().LocalId, data.Snapshot.Single().LocalId);
        Assert.Contains("New remote column", data.Columns);
        Assert.Equal("typed", data.Draft.Single().Get("Name"));
    }

    [Fact]
    public void Rows_are_matched_by_remote_id_even_when_the_key_column_differs()
    {
        var baseline = Remote("1", ("Name", "n"));
        var data = new ResourceData { Snapshot = [baseline.Dup()], Draft = [baseline.Dup()] };
        var renumbered = Remote("1", ("Name", "n")).With("ID", "000");

        SnapshotMerger.Merge(Fleets, data, [renumbered]);

        Assert.Single(data.Draft);
        Assert.Empty(data.Conflicts);
    }

    // ---- airports: aliases (A1) ----

    static DataRow RemoteAirport(string id, string icao, string iata, string name = "Hub")
    {
        return Row(("ICAO/IATA", icao), ("Name", name))
            .WithIdentity(new(id, "7"))
            .WithRawApiJson($$"""{"id":{{id}},"icao":"{{icao}}","iata":"{{iata}}","name":"{{name}}"}""");
    }

    [Fact]
    public void A1_two_remote_airports_sharing_an_iata_code_do_not_fail_the_refresh_and_the_code_matches_no_local_row()
    {
        var local = Row(("ICAO/IATA", "LHR"), ("Name", "mine"));
        var data = new ResourceData { Draft = [local] };
        var london = RemoteAirport("101", "EGLL", "LHR");
        var duplicate = RemoteAirport("103", "EGXX", "LHR");

        var error = Record.Exception(() => SnapshotMerger.Merge(Airports, data, [london, duplicate]));

        Assert.Null(error);
        Assert.Equal(3, data.Draft.Count);
        Assert.Contains(data.Draft, r => r.LocalId == local.LocalId && r.Identity is null && r.Get("Name") == "mine");
        Assert.Equal(["101", "103"], data.Draft.Where(r => r.Identity is not null).Select(r => r.Identity!.RemoteId).Order());
        Assert.Empty(data.Conflicts);
    }

    [Fact]
    public void An_unambiguous_alias_matches_a_local_airport_without_identity_and_keeps_the_remote_code()
    {
        var local = Row(("ICAO/IATA", "lhr"), ("Name", "Hub"));
        var data = new ResourceData { Draft = [local] };

        SnapshotMerger.Merge(Airports, data, [RemoteAirport("101", "EGLL", "LHR"), RemoteAirport("102", "EGKK", "LGW")]);

        Assert.Equal(2, data.Draft.Count);
        var matched = data.Draft.Single(r => r.LocalId == local.LocalId);
        Assert.Equal("101", matched.Identity!.RemoteId);
        Assert.Equal("EGLL", matched.Get("ICAO/IATA"));
        Assert.Empty(data.Conflicts);
    }

    // ---- C11: assertions of the C3 scenarios that were only matched by test name ----------------------------------

    [Fact]
    public void Twenty_thousand_rows_merge_without_losing_identity_or_either_side_s_edit()
    {
        var rows = Enumerable.Range(1, 20000).Select(i => Remote(i.ToString(), ("Callsign", "ACA" + i), ("Remarks", "old"))).ToList();
        var data = new ResourceData { Snapshot = rows.Select(r => r.Dup()).ToList(), Draft = rows.Select(r => r.Dup()).ToList() };
        data.Draft[100] = data.Draft[100].With("Remarks", "local");
        var incoming = rows.Select(r => r.Dup()).ToList();
        incoming[200] = incoming[200].With("Remarks", "remote");

        SnapshotMerger.Merge(ResourceKind.Routes, data, incoming);

        Assert.Equal(20000, data.Draft.Count);
        Assert.Equal("local", data.Draft[100].Get("Remarks"));
        Assert.Equal("remote", data.Draft[200].Get("Remarks"));
        Assert.Empty(data.Conflicts);
        Assert.Equal("20000", data.Draft[19999].Identity!.RemoteId);
    }

    [Fact]
    public void A_row_without_identity_is_matched_by_its_id_column_and_each_side_keeps_its_own_field()
    {
        var baseline = Row(("ID", "1"), ("Name", "Original"), ("Type Code", "B738"));
        var draft = baseline.With("Name", "Mine");
        var data = new ResourceData { Snapshot = [baseline], Draft = [draft] };

        SnapshotMerger.Merge(Fleets, data, [Remote("1", ("Name", "Original"), ("Type Code", "B739"))]);

        var row = data.Draft.Single();
        Assert.Equal(draft.LocalId, row.LocalId);
        Assert.Equal("Mine", row.Get("Name"));
        Assert.Equal("B739", row.Get("Type Code"));
        Assert.Empty(data.Conflicts);
    }

    [Fact]
    public void A_remote_value_that_changed_after_a_first_merge_conflicts_with_the_local_edit_once()
    {
        var baseline = Row(("ID", "1"), ("Name", "Original"), ("Type Code", "B738"));
        var data = new ResourceData { Snapshot = [baseline], Draft = [baseline.With("Name", "Mine")] };
        SnapshotMerger.Merge(Fleets, data, [Remote("1", ("Name", "Original"), ("Type Code", "B739"))]);

        var theirs = Remote("1", ("Name", "Theirs"), ("Type Code", "B739"));
        SnapshotMerger.Merge(Fleets, data, [theirs]);
        Assert.Equal([new MergeConflict(data.Draft[0].LocalId, "Name", "Original", "Mine", "Theirs")], data.Conflicts);

        SnapshotMerger.Merge(Fleets, data, [theirs.Dup()]);
        Assert.Single(data.Conflicts);
    }

    [Fact]
    public void A_local_airport_without_identity_binds_by_its_icao_code_even_when_the_snapshot_holds_it_too()
    {
        var local = Row(("ICAO/IATA", "EGLL"), ("Name", "London"));
        var data = new ResourceData { Snapshot = [local], Draft = [local.Dup()] };

        SnapshotMerger.Merge(Airports, data, [RemoteAirport("101", "EGLL", "LHR", "London")]);

        Assert.Single(data.Draft);
        Assert.Equal("101", data.Draft[0].Identity!.RemoteId);
    }
}
