using VamSys.Core;

namespace VamSys.UnitTests.Storage;

// The journal turns "what the database holds" and "what memory holds" into the smallest list of row operations. These
// tests need no database: a model of the stored rows applies the operations the way the storage layer does (including
// the unique position per list) and must end up equal to the list in memory.
public class ChangeJournalTests
{
    const string Header = "header";

    static DataRow Row(string name, string value = "") => new() { Fields = new Dictionary<string, string> { ["Name"] = name, ["Value"] = value } };

    static Workspace Synced(int rows, ResourceKind kind = ResourceKind.Fleets)
    {
        var w = new Workspace();
        var data = w.Resources[kind];
        for (var n = 0; n < rows; n++)
            data.Draft.Add(Row("row" + n));
        data.Snapshot = [.. data.Draft.Select(r => r.Dup())];
        Sync(w);
        return w;
    }

    static void Sync(Workspace w, long revision = 1)
    {
        w.StorageRevision = revision;
        w.Journal().Adopt(w.Journal().Capture(w, Header), revision);
    }

    static JournalPlan Plan(Workspace w, JournalScope scope = JournalScope.All) => w.Journal().Plan(w, Header, w.StorageRevision, scope)!;

    static ResourceChange Only(JournalPlan plan) => Assert.Single(plan.Resources);

    [Fact]
    public void A_workspace_that_was_never_synchronised_has_no_plan_and_needs_a_full_save()
    {
        var w = new Workspace();
        Assert.Null(w.Journal().Plan(w, Header, 0, JournalScope.All));
        Assert.False(w.Journal().IsTracking(0));
    }

    [Fact]
    public void A_journal_is_only_trusted_at_the_revision_it_was_synchronised_with()
    {
        var w = Synced(3);
        Assert.True(w.Journal().IsTracking(1));
        Assert.Null(w.Journal().Plan(w, Header, 2, JournalScope.All));
        w.Journal().RequireFullSave();
        Assert.Null(w.Journal().Plan(w, Header, 1, JournalScope.All));
    }

    [Fact]
    public void Nothing_changed_gives_an_empty_plan()
    {
        var w = Synced(50);
        var plan = Plan(w);
        Assert.True(plan.IsEmpty);
        Assert.False(plan.HeaderChanged);
        Assert.Empty(plan.Resources);
        Assert.True(plan.Jobs.IsEmpty);
    }

    [Fact]
    public void A_replaced_row_is_one_write_at_its_position_and_nothing_else_changes()
    {
        var w = Synced(10);
        var data = w.Resources[ResourceKind.Fleets];
        var target = data.Draft[4];
        var edited = target.With("Name", "edited");
        data.Edit([target.LocalId], () => data.ReplaceDraftRows([edited]));
        var change = Only(Plan(w));
        Assert.True(change.Snapshot.IsEmpty);
        Assert.False(change.MetadataChanged);
        Assert.Equal(ChangeMode.Delta, change.Draft.Mode);
        var placed = Assert.Single(change.Draft.Replaced);
        Assert.Same(edited, placed.Row);
        Assert.Equal(4, placed.Ordinal);
        Assert.Empty(change.Draft.Added);
        Assert.Empty(change.Draft.Deleted);
        Assert.Empty(change.Draft.Moved);
        Assert.Equal(1, change.Draft.RowWrites);
    }

    [Fact]
    public void A_replacement_with_the_same_content_writes_nothing()
    {
        var w = Synced(10);
        var data = w.Resources[ResourceKind.Fleets];
        data.Draft[3] = data.Draft[3].Dup();
        Assert.True(Plan(w).IsEmpty);
        // The copy became the new baseline, so a later real change is still found against it.
        data.Draft[3] = data.Draft[3].With("Name", "x");
        Assert.Single(Only(Plan(w)).Draft.Replaced);
    }

    [Fact]
    public void Appended_rows_are_inserts_at_the_end_without_renumbering()
    {
        var w = Synced(5);
        var data = w.Resources[ResourceKind.Fleets];
        var added = new[] { Row("a"), Row("b") };
        data.Edit(added.Select(r => r.LocalId), () => data.Draft.AddRange(added), structural: true);
        var change = Only(Plan(w)).Draft;
        Assert.Equal(ChangeMode.Delta, change.Mode);
        Assert.Equal([5, 6], change.Added.Select(p => p.Ordinal));
        Assert.Empty(change.Moved);
        Assert.Empty(change.Deleted);
    }

    [Fact]
    public void Removing_the_last_rows_needs_no_renumbering()
    {
        var w = Synced(8);
        var data = w.Resources[ResourceKind.Fleets];
        var gone = data.Draft.Skip(6).Select(r => r.LocalId).ToArray();
        data.Draft.RemoveRange(6, 2);
        var change = Only(Plan(w)).Draft;
        Assert.Equal(gone, change.Deleted);
        Assert.Empty(change.Moved);
    }

    [Fact]
    public void Removing_a_row_in_the_middle_renumbers_the_rows_behind_it()
    {
        var w = Synced(8);
        var data = w.Resources[ResourceKind.Fleets];
        var gone = data.Draft[2].LocalId;
        data.Draft.RemoveAt(2);
        var change = Only(Plan(w)).Draft;
        Assert.Equal(ChangeMode.Delta, change.Mode);
        Assert.Equal([gone], change.Deleted);
        Assert.Equal(Enumerable.Range(2, 5), change.Moved.Select(m => m.Ordinal));
    }

    [Fact]
    public void A_change_that_would_renumber_most_of_a_long_list_rewrites_the_list_instead()
    {
        var w = Synced(2000);
        var data = w.Resources[ResourceKind.Fleets];
        data.Draft.RemoveAt(0);
        var change = Only(Plan(w)).Draft;
        Assert.Equal(ChangeMode.Rewrite, change.Mode);
        Assert.Equal(1999, change.All.Count);
        Assert.Equal(1999, change.RowWrites);
        // The other list of the resource is untouched.
        Assert.True(Only(Plan(w)).Snapshot.IsEmpty);
    }

    [Fact]
    public void Swapping_two_rows_moves_exactly_those_two()
    {
        var w = Synced(6);
        var data = w.Resources[ResourceKind.Fleets];
        (data.Draft[1], data.Draft[4]) = (data.Draft[4], data.Draft[1]);
        var change = Only(Plan(w)).Draft;
        Assert.Equal(2, change.Moved.Count);
        Assert.Empty(change.Replaced);
        Assert.Equal(new[] { 1, 4 }, change.Moved.Select(m => m.Ordinal).Order());
    }

    [Fact]
    public void Columns_conflicts_and_snapshot_time_are_compared_by_value_even_when_edited_in_place()
    {
        var w = Synced(3);
        var data = w.Resources[ResourceKind.Fleets];
        data.Columns.Add("Extra");
        var change = Only(Plan(w));
        Assert.True(change.MetadataChanged);
        Assert.Equal(["Extra"], change.Columns);
        Assert.True(change.Draft.IsEmpty && change.Snapshot.IsEmpty);

        var again = Synced(3);
        again.Resources[ResourceKind.Fleets].Conflicts.Add(new(again.Resources[ResourceKind.Fleets].Draft[0].LocalId, "Name", "a", "b", "c"));
        Assert.True(Only(Plan(again)).MetadataChanged);

        var timed = Synced(3);
        timed.Resources[ResourceKind.Fleets].SnapshotAt = DateTimeOffset.UnixEpoch;
        Assert.True(Only(Plan(timed)).MetadataChanged);
    }

    [Fact]
    public void Snapshot_and_draft_lists_are_tracked_separately()
    {
        var w = Synced(4);
        var data = w.Resources[ResourceKind.Fleets];
        data.Snapshot.Add(Row("remote"));
        var change = Only(Plan(w));
        Assert.True(change.Draft.IsEmpty);
        Assert.Equal([4], change.Snapshot.Added.Select(p => p.Ordinal));
    }

    [Fact]
    public void The_header_is_compared_by_its_key()
    {
        var w = Synced(1);
        Assert.False(w.Journal().Plan(w, Header, 1, JournalScope.All)!.HeaderChanged);
        Assert.True(w.Journal().Plan(w, "other", 1, JournalScope.All)!.HeaderChanged);
        Assert.False(w.Journal().Plan(w, "other", 1, JournalScope.Jobs)!.HeaderChanged);
    }

    [Fact]
    public void Accepting_a_plan_moves_the_baseline_and_dropping_it_changes_nothing()
    {
        var w = Synced(5);
        var data = w.Resources[ResourceKind.Fleets];
        data.Draft.Add(Row("new"));
        var first = Plan(w);
        // Not accepted (the save failed): the same change is still pending.
        Assert.Equal(first.Resources.Single().Draft.Added.Count, Only(Plan(w)).Draft.Added.Count);
        w.Journal().Accept(first, 2);
        w.StorageRevision = 2;
        Assert.True(Plan(w).IsEmpty);
        // A second change on top of the accepted one is relative to it.
        data.Draft.Add(Row("newer"));
        Assert.Equal([6], Only(Plan(w)).Draft.Added.Select(p => p.Ordinal));
    }

    [Fact]
    public void Changes_made_while_a_save_runs_stay_pending_after_it_is_accepted()
    {
        var w = Synced(5);
        var data = w.Resources[ResourceKind.Fleets];
        data.Draft.Add(Row("first"));
        var plan = Plan(w);
        data.Draft.Add(Row("during the save"));
        w.Journal().Accept(plan, 2);
        w.StorageRevision = 2;
        Assert.Equal([6], Only(Plan(w)).Draft.Added.Select(p => p.Ordinal));
    }

    [Fact]
    public void Marking_a_resource_rewrites_its_rows_once_and_a_mark_made_during_a_save_survives_it()
    {
        var w = Synced(4);
        w.Journal().MarkRowsDirty(ResourceKind.Fleets);
        var plan = Plan(w);
        var change = Only(plan);
        Assert.Equal(ChangeMode.Rewrite, change.Draft.Mode);
        Assert.Equal(ChangeMode.Rewrite, change.Snapshot.Mode);
        Assert.True(change.MetadataChanged);
        w.Journal().MarkRowsDirty(ResourceKind.Fleets);
        w.Journal().Accept(plan, 2);
        w.StorageRevision = 2;
        Assert.Equal(ChangeMode.Rewrite, Only(Plan(w)).Draft.Mode);
        var second = Plan(w);
        w.Journal().Accept(second, 3);
        w.StorageRevision = 3;
        Assert.True(Plan(w).IsEmpty);
    }

    [Fact]
    public void A_scoped_plan_leaves_the_other_parts_pending()
    {
        var w = Synced(3);
        var data = w.Resources[ResourceKind.Fleets];
        data.Draft.Add(Row("pending"));
        w.Jobs.Add(new BatchJob());
        var jobsOnly = w.Journal().Plan(w, Header, 1, JournalScope.Jobs)!;
        Assert.Empty(jobsOnly.Resources);
        Assert.Single(jobsOnly.Jobs.Inserted);
        w.Journal().Accept(jobsOnly, 2);
        w.StorageRevision = 2;
        var rest = Plan(w);
        Assert.True(rest.Jobs.IsEmpty);
        Assert.Single(Only(rest).Draft.Added);
    }

    [Fact]
    public void A_new_job_is_inserted_and_an_existing_one_is_rewritten_only_when_touched()
    {
        var w = new Workspace();
        var existing = new BatchJob { Items = [new ChangeItem()] };
        w.Jobs.Add(existing);
        Sync(w);
        Assert.True(Plan(w).Jobs.IsEmpty);

        existing.Items[0].State = ItemState.Running; // in place, no mark: the state fingerprint finds it
        var found = Plan(w).Jobs;
        Assert.Same(existing, Assert.Single(found.Rewritten).Job);
        w.Journal().Accept(Plan(w), 2);
        w.StorageRevision = 2;
        Assert.True(Plan(w).Jobs.IsEmpty);
        w.Journal().TouchJob(existing); // the fast path
        var touched = Plan(w).Jobs;
        Assert.Equal(ChangeMode.Delta, touched.Mode);
        Assert.Same(existing, Assert.Single(touched.Rewritten).Job);

        var added = new BatchJob();
        w.Jobs.Add(added);
        var both = Plan(w).Jobs;
        Assert.Same(added, Assert.Single(both.Inserted).Job);
        Assert.Equal(1, both.Inserted[0].Ordinal);
        Assert.Single(both.Rewritten);
    }

    [Fact]
    public void Removing_or_reordering_jobs_rewrites_all_of_them()
    {
        var w = new Workspace();
        w.Jobs.AddRange([new BatchJob(), new BatchJob(), new BatchJob()]);
        Sync(w);
        w.Jobs.RemoveAt(1);
        var change = Plan(w).Jobs;
        Assert.Equal(ChangeMode.Rewrite, change.Mode);
        Assert.Equal(2, change.All.Count);

        var shuffled = new Workspace();
        shuffled.Jobs.AddRange([new BatchJob(), new BatchJob()]);
        Sync(shuffled);
        (shuffled.Jobs[0], shuffled.Jobs[1]) = (shuffled.Jobs[1], shuffled.Jobs[0]);
        Assert.Equal(ChangeMode.Rewrite, Plan(shuffled).Jobs.Mode);
    }

    [Fact]
    public void Rows_written_in_place_by_a_row_save_move_the_baseline_without_a_plan()
    {
        var w = Synced(10);
        var data = w.Resources[ResourceKind.Fleets];
        var edited = data.Draft[2].With("Name", "cell");
        data.Edit([edited.LocalId], () => data.ReplaceDraftRows([edited]));
        w.Journal().AcceptDraftRows(ResourceKind.Fleets, [edited]);
        w.Journal().AfterExternalWrite(1, 2);
        w.StorageRevision = 2;
        Assert.True(Plan(w).IsEmpty);
    }

    [Fact]
    public void A_row_save_for_a_row_the_journal_does_not_know_makes_it_distrust_itself()
    {
        var w = Synced(3);
        w.Journal().AcceptDraftRows(ResourceKind.Fleets, [Row("stranger")]);
        Assert.False(w.Journal().IsTracking(1));
    }

    [Fact]
    public void A_staged_copy_gets_its_own_journal_and_can_be_adopted()
    {
        var w = Synced(3);
        var staged = w.StageResources();
        Assert.False(staged.Journal().IsTracking(1));
        Sync(staged, 2);
        w.Journal().AdoptFrom(staged.Journal());
        Assert.True(w.Journal().IsTracking(2));
    }

    // ---- model check ------------------------------------------------------------------------------------------------

    // The stored rows of one list as the storage layer keeps them: unique position, JSON (here: the field values).
    sealed class StoredList
    {
        public readonly Dictionary<Guid, (int Position, string Content)> Rows = [];

        public static string Content(DataRow row) => string.Join("|", row.Fields.Select(p => p.Key + "=" + p.Value));

        public void Rewrite(IReadOnlyList<DataRow> rows)
        {
            Rows.Clear();
            for (var n = 0; n < rows.Count; n++)
                Rows.Add(rows[n].LocalId, (n, Content(rows[n])));
        }

        public void Apply(RowListChange change)
        {
            if (change.Mode == ChangeMode.Unchanged)
                return;
            if (change.Mode == ChangeMode.Rewrite)
            { Rewrite(change.All); return; }
            foreach (var id in change.Deleted)
                Assert.True(Rows.Remove(id), "delete of a row the database does not hold");
            // Two phases like the storage layer: park on negative positions, then final ones; positions stay unique.
            foreach (var m in change.Moved)
                Rows[m.Id] = (-1 - m.Ordinal, Rows[m.Id].Content);
            foreach (var m in change.Moved)
                Rows[m.Id] = (m.Ordinal, Rows[m.Id].Content);
            foreach (var p in change.Replaced)
                Rows[p.Row.LocalId] = (Rows[p.Row.LocalId].Position, Content(p.Row));
            foreach (var p in change.Added)
                Rows.Add(p.Row.LocalId, (p.Ordinal, Content(p.Row)));
            Assert.Equal(Rows.Count, Rows.Values.Select(v => v.Position).Distinct().Count());
        }

        public void AssertEquals(IReadOnlyList<DataRow> rows)
        {
            Assert.Equal(rows.Count, Rows.Count);
            for (var n = 0; n < rows.Count; n++)
                Assert.Equal((n, Content(rows[n])), Rows[rows[n].LocalId]);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(20260117)]
    public void Random_list_edits_applied_as_planned_always_reproduce_the_list(int seed)
    {
        var rng = new Random(seed);
        var w = Synced(rng.Next(0, 40));
        var data = w.Resources[ResourceKind.Fleets];
        var snapshotDb = new StoredList();
        var draftDb = new StoredList();
        snapshotDb.Rewrite(data.Snapshot);
        draftDb.Rewrite(data.Draft);
        long revision = 1;
        var steps = 0;
        for (var step = 0; step < 1500; step++, steps++)
        {
            var list = rng.Next(4) == 0 ? data.Snapshot : data.Draft;
            var count = list.Count;
            switch (rng.Next(9))
            {
                case 0 when count > 0: // replace
                    var at = rng.Next(count);
                    list[at] = list[at].With("Value", "v" + step);
                    break;
                case 1: // same-content replacement
                    if (count > 0)
                    { var same = rng.Next(count); list[same] = list[same].Dup(); }
                    break;
                case 2: // append
                    for (var n = rng.Next(1, 4); n > 0; n--)
                        list.Add(Row("n" + step + "-" + n));
                    break;
                case 3 when count > 0: // remove anywhere
                    list.RemoveAt(rng.Next(count));
                    break;
                case 4 when count > 1: // swap
                    var a = rng.Next(count);
                    var b = rng.Next(count);
                    (list[a], list[b]) = (list[b], list[a]);
                    break;
                case 5 when count > 2: // insert in the middle
                    list.Insert(rng.Next(count), Row("i" + step));
                    break;
                case 6: // remove from the end
                    if (count > 0)
                    { var tail = Math.Min(count, rng.Next(1, 4)); list.RemoveRange(count - tail, tail); }
                    break;
                case 7 when count > 3: // reverse a stretch
                    var from = rng.Next(count - 2);
                    list.Reverse(from, rng.Next(2, count - from + 1));
                    break;
                default: // replace the whole list with copies
                    if (rng.Next(6) == 0)
                    {
                        var copies = list.Select(r => r.Dup()).ToList();
                        if (list == data.Draft) data.Draft = copies; else data.Snapshot = copies;
                    }
                    break;
            }
            var plan = Plan(w);
            var change = plan.Resources.SingleOrDefault();
            if (change != null)
            {
                snapshotDb.Apply(change.Snapshot);
                draftDb.Apply(change.Draft);
            }
            snapshotDb.AssertEquals(data.Snapshot);
            draftDb.AssertEquals(data.Draft);
            w.Journal().Accept(plan, ++revision);
            w.StorageRevision = revision;
        }
        Assert.True(Plan(w).IsEmpty);
    }
}
