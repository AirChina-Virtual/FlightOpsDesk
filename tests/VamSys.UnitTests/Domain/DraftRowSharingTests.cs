using VamSys.Core;
using static VamSys.UnitTests.Domain.TestRows;

namespace VamSys.UnitTests.Domain;

/// <summary>ResourceData.ShareUnchangedDraftRows: only rows whose content is exactly the snapshot row's become that object.</summary>
public class DraftRowSharingTests
{
    static DataRow Copy(DataRow row) => row.ToBuilder().ToRow();

    [Fact]
    public void Identical_rows_are_shared_wherever_they_sit_and_every_difference_keeps_its_own_row()
    {
        var data = new ResourceData();
        for (var n = 0; n < 6; n++)
            data.Snapshot.Add(Row(("ID", n.ToString()), ("Name", "n" + n)));
        var draft = data.Snapshot.Select(Copy).ToList();
        draft[1] = draft[1].With("Name", "changed");
        draft[2] = draft[2].WithRawApiJson("{}");
        draft[3] = draft[3].With("Extra", "");
        (draft[4], draft[5]) = (draft[5], draft[4]);
        var created = Row(("Name", "new"));
        draft.Add(created);
        data.Draft = draft;
        var position = data.FindDraftPosition(draft[4].LocalId);

        Assert.Equal(3, data.ShareUnchangedDraftRows());

        Assert.Same(data.Snapshot[0], data.Draft[0]);
        Assert.Same(data.Snapshot[5], data.Draft[4]);
        Assert.Same(data.Snapshot[4], data.Draft[5]);
        foreach (var n in new[] { 1, 2, 3 })
            Assert.NotSame(data.Snapshot[n], data.Draft[n]);
        Assert.Same(created, data.Draft[6]);
        Assert.Equal(position, data.FindDraftPosition(data.Draft[4].LocalId));
        Assert.Equal(3, data.ShareUnchangedDraftRows());
    }

    [Fact]
    public void Field_order_and_identity_count_as_content()
    {
        var snapshot = Row(("A", "1"), ("B", "2"));
        var reordered = new DataRow.Builder(snapshot.LocalId) { ["B"] = "2", ["A"] = "1" }.ToRow();
        var data = new ResourceData { Snapshot = [snapshot, Row(("A", "x"))], Draft = [reordered] };
        data.Draft.Add(data.Snapshot[1].WithIdentity(new("1", "c")));
        Assert.Equal(0, data.ShareUnchangedDraftRows());
        Assert.Same(reordered, data.Draft[0]);
    }

    [Fact]
    public void Editing_a_shared_row_replaces_it_in_the_draft_only_and_undo_restores_equal_content()
    {
        var data = Baseline(ResourceKind.Fleets, [("ID", "1"), ("Name", "A")], [("ID", "2"), ("Name", "B")]);
        data.Draft = data.Draft.Select(Copy).ToList();
        Assert.Equal(2, data.ShareUnchangedDraftRows());
        var shared = data.Draft[0];
        data.Change(shared.LocalId, "Name", "edited");
        Assert.Same(shared, data.Snapshot[0]);
        Assert.Equal("A", data.Snapshot[0].Get("Name"));
        Assert.Equal("edited", data.Draft[0].Get("Name"));
        data.UndoEdit();
        Assert.True(DataRow.SameContent(data.Snapshot[0], data.Draft[0]));
        Assert.Empty(new ChangePlanner().Plan(ResourceKind.Fleets, data));
        data.RedoEdit();
        Assert.Single(new ChangePlanner().Plan(ResourceKind.Fleets, data));
    }

    [Fact]
    public void Nothing_to_share_in_empty_lists()
    {
        Assert.Equal(0, new ResourceData().ShareUnchangedDraftRows());
        Assert.Equal(0, new ResourceData { Draft = [Row(("A", "1"))] }.ShareUnchangedDraftRows());
    }
}
