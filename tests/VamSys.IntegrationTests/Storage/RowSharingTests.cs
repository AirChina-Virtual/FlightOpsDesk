using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// A load holds an unchanged record once: a draft row whose content is exactly its snapshot row's is the snapshot row
/// object itself, and field names are one string instance each. Sharing must be invisible: edits reach only the draft,
/// journal saves, full saves, undo/redo and the change plan give the results an unshared workspace gives.
/// </summary>
public class RowSharingTests
{
    const string Custom = "Imported Column";

    // Routes: rows 0-9 unchanged, 3 edited, 5 with its fields in another order, 6 with an identity only in the draft,
    // 8 marked for deletion, row 9 removed from the draft, one new draft row. Fleets: unchanged rows in another order.
    static Workspace Seeded()
    {
        var w = new Workspace { Name = "Sharing", AirlineId = "7", Mode = RunMode.Online };
        var routes = w.Resources[ResourceKind.Routes];
        for (var n = 0; n < 10; n++)
        {
            var row = new DataRow { Fields = new Dictionary<string, string> { ["ID"] = n.ToString(), ["Flight Number"] = "AC" + n, ["Callsign"] = "ACA" + n, [new string(Custom.AsSpan())] = "x" + n } };
            routes.Snapshot.Add(row);
            routes.Draft.Add(row.Dup());
        }
        routes.Draft[3] = routes.Draft[3].With("Callsign", "EDITED");
        var reordered = new DataRow.Builder(routes.Draft[5].LocalId);
        foreach (var key in routes.Draft[5].Fields.Keys.Reverse())
            reordered[key] = routes.Draft[5].Get(key);
        routes.Draft[5] = reordered.ToRow();
        routes.Draft[6] = routes.Draft[6].WithIdentity(new("606", "7"));
        routes.Draft[8] = routes.Draft[8].With("_delete", "TRUE");
        routes.Draft.RemoveAt(9);
        routes.Draft.Add(new DataRow { Fields = new Dictionary<string, string> { ["Flight Number"] = "NEW", ["Callsign"] = "NEW1" } });
        routes.Columns = ["ID", "Flight Number", "Callsign", Custom];
        var fleets = w.Resources[ResourceKind.Fleets];
        for (var n = 0; n < 4; n++)
        {
            var row = new DataRow { Fields = new Dictionary<string, string> { ["ID"] = (n + 1).ToString(), ["Name"] = "Fleet " + n } };
            fleets.Snapshot.Add(row);
            fleets.Draft.Insert(0, row.Dup());
        }
        return w;
    }

    static string Plan(Workspace w) => string.Join("\n", new ChangePlanner().PlanWorkspace(w)
        .Select(i => $"{i.Resource} {i.Kind} {i.After.LocalId} {JsonSerializer.Serialize(i.After)} {JsonSerializer.Serialize(i.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))}"));

    [Fact]
    public void A_load_shares_unchanged_draft_rows_and_field_names_and_keeps_every_other_row_apart()
    {
        using var dir = TempDataDirectory.Create("vamsys-sharing-");
        var store = new WorkspaceStore(dir.Path);
        var original = Seeded();
        store.Save(original);
        var w = store.Load(original.Id);

        var routes = w.Resources[ResourceKind.Routes];
        for (var n = 0; n < 9; n++)
        {
            var shared = n is not (3 or 5 or 6 or 8);
            Assert.Equal(shared, ReferenceEquals(routes.Snapshot[n], routes.Draft[n]));
            Assert.Equal(JsonSerializer.Serialize(original.Resources[ResourceKind.Routes].Draft[n]), JsonSerializer.Serialize(routes.Draft[n]));
        }
        Assert.DoesNotContain(routes.Draft[9], routes.Snapshot);
        var fleets = w.Resources[ResourceKind.Fleets];
        Assert.All(fleets.Draft, row => Assert.Same(fleets.Snapshot.Single(s => s.LocalId == row.LocalId), row));
        Assert.Equal(fleets.Draft[3].LocalId, fleets.FindDraftRow(fleets.Draft[3].LocalId)!.LocalId);

        // Known names are the field table's own strings; an imported column is one instance within the load.
        var callsign = FieldSpecs.Get(ResourceKind.Routes).Find("Callsign")!.Csv;
        var unshared = new List<DataRow> { routes.Snapshot[3], routes.Draft[3], routes.Snapshot[5], routes.Draft[5], routes.Draft[8] };
        foreach (var row in unshared)
        {
            Assert.Same(callsign, row.Fields.Keys.Single(k => k == "Callsign"));
            Assert.Same(routes.Snapshot[0].Fields.Keys.Single(k => k == Custom), row.Fields.Keys.Single(k => k == Custom));
        }
        Assert.Equal(Plan(original), Plan(w));
    }

    [Fact]
    public void Unknown_field_names_are_shared_only_inside_a_read()
    {
        DataRow Make() => new() { Fields = new Dictionary<string, string> { [new string(Custom.AsSpan())] = "1", [new string("Name".AsSpan())] = "n" } };
        var a = Make();
        var b = Make();
        Assert.NotSame(a.Fields.Keys.First(), b.Fields.Keys.First());
        Assert.Same(a.Fields.Keys.Last(), b.Fields.Keys.Last());
        using (FieldNames.BeginScope())
        {
            var c = Make();
            var d = Make();
            Assert.Same(c.Fields.Keys.First(), d.Fields.Keys.First());
        }
        Assert.NotSame(Make().Fields.Keys.First(), Make().Fields.Keys.First());
    }

    // What a full save of an independent, unshared copy of `w` (rows rebuilt from JSON) writes to an empty database.
    static string FullSaveOfCopy(Workspace w)
    {
        using var dir = TempDataDirectory.Create("vamsys-sharing-full-");
        var copy = Workspace.Deserialize(w.Serialize());
        copy.StorageRevision = 0;
        foreach (var data in copy.Resources.Values)
            data.Draft = [.. data.Draft.Select(r => r.Dup())];
        new WorkspaceStore(dir.Path).Save(copy);
        return StoredState.Dump(dir.Path);
    }

    [Fact]
    public void Edits_undo_and_redo_of_a_shared_row_reach_only_the_draft_and_saves_equal_full_saves()
    {
        using var dir = TempDataDirectory.Create("vamsys-sharing-");
        var store = new WorkspaceStore(dir.Path);
        var original = Seeded();
        store.Save(original);
        var w = store.Load(original.Id);
        store.SaveChanges(w);
        Assert.True(store.LastSave!.WroteNothing);
        Assert.Equal(FullSaveOfCopy(w), StoredState.Dump(dir.Path));

        var routes = w.Resources[ResourceKind.Routes];
        var snapshotRow = routes.Snapshot[1];
        Assert.Same(snapshotRow, routes.Draft[1]);
        routes.Edit([snapshotRow.LocalId], () => routes.ReplaceDraftRows([snapshotRow.With("Callsign", "CHANGED")]));
        Assert.Same(snapshotRow, routes.Snapshot[1]);
        Assert.Equal("ACA1", routes.Snapshot[1].Get("Callsign"));
        Assert.Equal("CHANGED", routes.Draft[1].Get("Callsign"));
        Assert.Contains(new ChangePlanner().PlanWorkspace(w), i => i.After.LocalId == snapshotRow.LocalId && i.Kind == ChangeKind.Update);

        void Save(string step, int rowWrites)
        {
            store.SaveChanges(w);
            Assert.Equal("Journal", store.LastSave!.Kind);
            Assert.Equal(rowWrites, store.LastSave.RowWrites);
            Assert.True(FullSaveOfCopy(w) == StoredState.Dump(dir.Path), step);
            var reloaded = new WorkspaceStore(dir.Path).Load(w.Id);
            Assert.Equal(Plan(w), Plan(reloaded));
            Assert.Equal(w.Serialize(), reloaded.Serialize());
        }
        Save("edit", 1);
        routes.UndoEdit();
        Assert.True(DataRow.SameContent(routes.Snapshot[1], routes.Draft[1]));
        Save("undo", 1);
        routes.RedoEdit();
        Save("redo", 1);
        // A row whose draft is shared and whose snapshot is replaced (as a refresh does) keeps the old draft row.
        var refreshed = routes.Snapshot[2].With("Callsign", "REMOTE");
        var draftRow = routes.Draft[2];
        routes.Snapshot[2] = refreshed;
        Assert.Equal("ACA2", routes.Draft[2].Get("Callsign"));
        Assert.Same(draftRow, routes.Draft[2]);
        Save("snapshot replaced", 1);
    }

    [Fact]
    public void A_workspace_read_from_json_shares_unchanged_rows_too()
    {
        var original = Seeded();
        var w = Workspace.Deserialize(original.Serialize());
        var routes = w.Resources[ResourceKind.Routes];
        Assert.Same(routes.Snapshot[0], routes.Draft[0]);
        Assert.NotSame(routes.Snapshot[3], routes.Draft[3]);
        Assert.Equal(original.Serialize(), w.Serialize());
        Assert.Equal(Plan(original), Plan(w));
    }
}
