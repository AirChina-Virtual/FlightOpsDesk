using System.Text.Json;
using VamSys.Core;

namespace VamSys.UnitTests;

/// <summary>A5: draft rows are replaced, never edited in place. In Debug builds a row that sits in a ResourceData list is frozen.</summary>
public class RowReplacementTests
{
    static readonly ResourceKind Kind = ResourceKind.Fleets;
    static readonly string Key = Schemas.All[Kind].Key;

    static DataRow Row(string key, string name = "n")
    {
        var row = new DataRow();
        row.Fields[Key] = key;
        row.Fields["Name"] = name;
        row.Fields["_delete"] = "FALSE";
        return row;
    }

    // A draft equal to its snapshot, like a freshly refreshed resource.
    static ResourceData Resource(params DataRow[] rows) => new() { Snapshot = rows.Select(r => r.Copy()).ToList(), Draft = [.. rows] };

    static void ExpectGuard(Action write)
    {
        var error = Record.Exception(write);
        if (DataRow.GuardEnabled)
            Assert.IsType<InvalidOperationException>(error);
        else
            Assert.Null(error);
    }

    [Fact]
    public void Guard_is_compiled_only_into_debug_builds()
    {
#if DEBUG
        Assert.True(DataRow.GuardEnabled);
#else
        Assert.False(DataRow.GuardEnabled);
#endif
    }

    [Fact]
    public void Rows_placed_in_a_list_reject_property_writes()
    {
        var data = Resource(Row("1"));
        var row = data.Draft[0];

        Assert.Equal(DataRow.GuardEnabled, row.HasBeenFrozen());
        ExpectGuard(() => row.Fields = new());
        ExpectGuard(() => row.Identity = new("1", "c"));
        ExpectGuard(() => row.RawApiJson = "{}");
        ExpectGuard(() => row.LocalId = Guid.NewGuid());
    }

    [Fact]
    public void An_indexer_write_to_a_frozen_row_is_caught_by_the_next_edit()
    {
        var data = Resource(Row("1"));
        var row = data.Draft[0];
        row.Fields["Name"] = "sneaky";

        ExpectGuard(() => data.Edit([row.LocalId], () => { }));
    }

    [Fact]
    public void An_in_place_write_inside_an_edit_is_rejected()
    {
        var data = Resource(Row("1"));
        var row = data.Draft[0];

        ExpectGuard(() => data.Edit([row.LocalId], () => row.Fields["Name"] = "changed"));
    }

    [Fact]
    public void Copies_are_editable_and_replace_the_row_through_the_edit_path()
    {
        var data = Resource(Row("1"));
        var original = data.Draft[0];
        var copy = original.Copy();
        Assert.False(copy.HasBeenFrozen());
        copy.Fields["Name"] = "changed";

        data.Edit([original.LocalId], () => data.ReplaceDraftRows([copy]));

        Assert.Same(copy, data.Draft[0]);
        Assert.Equal("n", original.Get("Name"));
        Assert.Equal("changed", data.Draft[0].Get("Name"));
        Assert.Equal(DataRow.GuardEnabled, copy.HasBeenFrozen());
        Assert.Single(data.Undo);
        data.UndoEdit();
        Assert.Equal("n", data.Draft[0].Get("Name"));
        data.RedoEdit();
        Assert.Equal("changed", data.Draft[0].Get("Name"));
    }

    [Fact]
    public void Rows_added_inside_an_edit_are_frozen_afterwards()
    {
        var data = Resource();
        var added = Row("2");

        data.Edit([added.LocalId], () => data.Draft.Add(added), structural: true);

        Assert.Equal(DataRow.GuardEnabled, added.HasBeenFrozen());
    }

    [Fact]
    public void Shallow_copies_keep_frozen_rows_and_verify_them()
    {
        var data = Resource(Row("1"));
        var copy = data.ShallowCopy();
        Assert.Same(data.Draft[0], copy.Draft[0]);

        data.Draft[0].Fields["Name"] = "sneaky";
        ExpectGuard(() => data.ShallowCopy());
    }

    [Fact]
    public void The_frozen_state_is_not_serialized()
    {
        var plain = Row("1");
        var frozen = Resource(plain.Copy()).Draft[0];

        Assert.Equal(JsonSerializer.Serialize(plain.Copy()), JsonSerializer.Serialize(frozen));
        Assert.DoesNotContain("rozen", JsonSerializer.Serialize(frozen), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Csv_merge_import_replaces_matched_rows()
    {
        var data = Resource(Row("1", "old"), Row("2", "keep"));
        var before = data.Draft.ToArray();
        var incoming = Row("1", "new");
        incoming.Fields["Extra"] = "x";
        var added = Row("3");

        CsvAdapter.Import(Kind, data, [incoming, added], baseline: false);

        Assert.Equal(3, data.Draft.Count);
        Assert.NotSame(before[0], data.Draft[0]);
        Assert.Equal(before[0].LocalId, data.Draft[0].LocalId);
        Assert.Equal("new", data.Draft[0].Get("Name"));
        Assert.Equal("x", data.Draft[0].Get("Extra"));
        Assert.Equal("old", before[0].Get("Name"));
        Assert.False(before[0].Fields.ContainsKey("Extra"));
        Assert.Same(before[1], data.Draft[1]);
        Assert.Same(added, data.Draft[2]);
        data.UndoEdit();
        Assert.Equal(2, data.Draft.Count);
        Assert.Equal("old", data.Draft[0].Get("Name"));
    }

    [Fact]
    public void Delete_marking_and_restore_replace_rows()
    {
        var workspace = new Workspace();
        var data = workspace.Resources[Kind] = Resource(Row("1"), Row("2"));
        var original = data.Draft[0];
        var service = new DeletionService();

        service.Apply(workspace, service.Preview(workspace, Kind, [original.LocalId]));

        Assert.NotSame(original, data.Draft[0]);
        Assert.Equal("FALSE", original.Get("_delete"));
        Assert.Equal("TRUE", data.Draft[0].Get("_delete"));
        Assert.Equal(original.LocalId, data.Draft[0].LocalId);

        var marked = data.Draft[0];
        service.Restore(data, [marked.LocalId]);

        Assert.NotSame(marked, data.Draft[0]);
        Assert.Equal("TRUE", marked.Get("_delete"));
        Assert.Equal("FALSE", data.Draft[0].Get("_delete"));
        Assert.Equal(2, data.Undo.Count);
    }

    [Fact]
    public void Deleting_new_rows_and_marking_existing_rows_in_one_step_keeps_positions()
    {
        var workspace = new Workspace();
        var data = workspace.Resources[Kind] = Resource(Row("1"), Row("2"));
        var added = Row("3");
        data.Edit([added.LocalId], () => data.Draft.Add(added), structural: true);
        var service = new DeletionService();

        service.Apply(workspace, service.Preview(workspace, Kind, [data.Draft[0].LocalId, added.LocalId]));

        Assert.Equal(2, data.Draft.Count);
        Assert.Equal("TRUE", data.Draft[0].Get("_delete"));
        Assert.Equal("FALSE", data.Draft[1].Get("_delete"));
        Assert.Equal(0, data.FindDraftPosition(data.Draft[0].LocalId));
        data.UndoEdit();
        Assert.Equal(3, data.Draft.Count);
        Assert.Equal("FALSE", data.Draft[0].Get("_delete"));
    }
}
