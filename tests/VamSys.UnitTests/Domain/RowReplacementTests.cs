using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.UnitTests;

/// <summary>
/// A5/R4: a DataRow is immutable after construction, enforced by the compiler in every configuration; draft rows are
/// replaced by edited copies, never edited in place (the change journal detects edits by row-object identity).
/// </summary>
public class RowReplacementTests
{
    static readonly ResourceKind Kind = ResourceKind.Fleets;
    static readonly string Key = Schemas.All[Kind].Key;

    static DataRow Row(string key, string name = "n") =>
        new DataRow.Builder { [Key] = key, ["Name"] = name, ["_delete"] = "FALSE" }.ToRow();

    // A draft equal to its snapshot, like a freshly refreshed resource.
    static ResourceData Resource(params DataRow[] rows) => new() { Snapshot = rows.Select(r => r.Dup()).ToList(), Draft = [.. rows] };

    [Fact]
    public void No_public_member_can_modify_a_row_after_construction()
    {
        foreach (var property in typeof(DataRow).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var setter = property.SetMethod;
            if (setter is null)
                continue;
            Assert.True(setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)), $"{property.Name} must be init-only");
        }
        var fields = typeof(DataRow).GetProperty(nameof(DataRow.Fields))!.PropertyType;
        Assert.Equal(typeof(IReadOnlyDictionary<string, string>), fields);
        var ignorable = new[] { "Get", "With", "Without", "WithIdentity", "WithRawApiJson", "WithLocalId", "ToBuilder", "SameContent", "ToString", "Equals", "GetHashCode", "GetType" };
        Assert.Empty(typeof(DataRow).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && !ignorable.Contains(m.Name)).Select(m => m.Name));
        Assert.Empty(typeof(DataRow).GetFields(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void The_fields_view_cannot_be_turned_back_into_a_mutable_dictionary()
    {
        var row = Row("1");
        Assert.False(row.Fields is IDictionary<string, string>);
        Assert.False(row.Fields is System.Collections.IDictionary);
        Assert.False(row.Fields is ICollection<KeyValuePair<string, string>>);
        Assert.Same(row.Fields, row.Fields);
    }

    [Fact]
    public void Initialization_copies_the_supplied_dictionary()
    {
        var source = new Dictionary<string, string> { ["a"] = "1" };
        var row = new DataRow { Fields = source };
        source["a"] = "changed";
        source["b"] = "2";

        Assert.Equal("1", row.Get("a"));
        Assert.Single(row.Fields);
    }

    [Fact]
    public void With_methods_return_edited_copies_and_leave_the_original_untouched()
    {
        var original = Row("1");
        var edited = original.With("Name", "x");
        var several = original.With(new Dictionary<string, string> { ["Name"] = "y", ["Extra"] = "e" });
        var identified = original.WithIdentity(new("9", "c"));
        var raw = original.WithRawApiJson("{}");
        var removed = original.Without("Name");
        var moved = original.WithLocalId(Guid.NewGuid());

        Assert.Equal("n", original.Get("Name"));
        Assert.Null(original.Identity);
        Assert.Null(original.RawApiJson);
        Assert.False(original.Fields.ContainsKey("Extra"));
        Assert.Equal("x", edited.Get("Name"));
        Assert.Equal("y", several.Get("Name"));
        Assert.Equal("e", several.Get("Extra"));
        Assert.Equal("9", identified.Identity!.RemoteId);
        Assert.Equal("{}", raw.RawApiJson);
        Assert.False(removed.Fields.ContainsKey("Name"));
        Assert.NotEqual(original.LocalId, moved.LocalId);
        foreach (var copy in new[] { edited, several, identified, raw, removed })
        {
            Assert.NotSame(original, copy);
            Assert.Equal(original.LocalId, copy.LocalId);
        }
        Assert.Equal(original.Fields.Keys, identified.Fields.Keys);
        Assert.Equal(original.Fields.Keys, edited.Fields.Keys);
    }

    [Fact]
    public void A_builder_hands_its_fields_to_exactly_one_row()
    {
        var builder = new DataRow.Builder { Identity = new("1", "c"), RawApiJson = "{}" };
        builder["a"] = "1";
        builder.Set("b", "2").Remove("a");
        var row = builder.ToRow();

        Assert.Equal(["b"], row.Fields.Keys);
        Assert.Equal("1", row.Identity!.RemoteId);
        Assert.Throws<InvalidOperationException>(() => builder.ToRow());
        Assert.Throws<InvalidOperationException>(() => builder["c"] = "3");
        var fromRow = row.ToBuilder();
        fromRow["b"] = "changed";
        Assert.Equal("2", row.Get("b"));
        Assert.Equal(row.LocalId, fromRow.ToRow().LocalId);
    }

    [Fact]
    public void Serialization_keeps_property_order_and_deserializes_into_immutable_rows()
    {
        var row = new DataRow.Builder(Guid.Parse("11111111-1111-1111-1111-111111111111")) { Identity = new("7", "c", "f"), RawApiJson = "{\"x\":1}" }
            .Set("z", "1").Set("a", "").ToRow();

        var json = JsonSerializer.Serialize(row);

        Assert.Equal("{\"LocalId\":\"11111111-1111-1111-1111-111111111111\",\"Identity\":{\"RemoteId\":\"7\",\"ConnectionId\":\"c\",\"ParentFleetId\":\"f\"},\"RawApiJson\":\"{\\u0022x\\u0022:1}\",\"Fields\":{\"z\":\"1\",\"a\":\"\"}}", json);
        var back = JsonSerializer.Deserialize<DataRow>(json)!;
        Assert.True(DataRow.SameContent(row, back));
        Assert.Equal(["z", "a"], back.Fields.Keys);
        Assert.Equal(json, JsonSerializer.Serialize(back));
    }

    [Fact]
    public void Copies_replace_the_row_through_the_edit_path()
    {
        var data = Resource(Row("1"));
        var original = data.Draft[0];
        var copy = original.With("Name", "changed");

        data.Edit([original.LocalId], () => data.ReplaceDraftRows([copy]));

        Assert.Same(copy, data.Draft[0]);
        Assert.Equal("n", original.Get("Name"));
        Assert.Equal("changed", data.Draft[0].Get("Name"));
        Assert.Single(data.Undo);
        data.UndoEdit();
        Assert.Equal("n", data.Draft[0].Get("Name"));
        data.RedoEdit();
        Assert.Equal("changed", data.Draft[0].Get("Name"));
    }

    [Fact]
    public void Shallow_copies_share_the_immutable_rows()
    {
        var data = Resource(Row("1"));
        var copy = data.ShallowCopy();

        Assert.Same(data.Draft[0], copy.Draft[0]);
        copy.Draft[0] = copy.Draft[0].With("Name", "other");
        Assert.Equal("n", data.Draft[0].Get("Name"));
    }

    [Fact]
    public void Csv_merge_import_replaces_matched_rows()
    {
        var data = Resource(Row("1", "old"), Row("2", "keep"));
        var before = data.Draft.ToArray();
        var incoming = Row("1", "new").With("Extra", "x");
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