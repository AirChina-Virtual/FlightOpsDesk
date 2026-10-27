using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Storage;

/// <summary>Several workspaces in one real store do not see each other, and the language preference lives in the store.</summary>
public class WorkspaceIsolationTests
{
    static DataRow Row(params (string, string)[] fields) => new() { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };

    static Workspace Seeded(string name, string icao)
    {
        var w = new Workspace { Name = name };
        w.Resources[ResourceKind.Airports].Draft.Add(Row(("ICAO/IATA", icao), ("Name", name + " airport")));
        w.Resources[ResourceKind.Airports].Edit([], () => { });
        w.Jobs.Add(new BatchJob { Items = [new ChangeItem { Resource = ResourceKind.Airports, State = ItemState.Succeeded }] });
        return w;
    }

    [Fact]
    public void Changing_draft_undo_history_secret_and_tasks_of_one_workspace_leaves_the_other_byte_for_byte_unchanged()
    {
        using var dir = TempDataDirectory.Create("vamsys-isolation-");
        var store = new WorkspaceStore(dir.Path);
        var a = Seeded("A", "ZBAA");
        var b = Seeded("B", "ZSPD");
        store.Save(a);
        store.Save(b);
        var windows = OperatingSystem.IsWindows();
        if (windows)
        {
            store.SaveSecret(a.Id, "secret-a");
            store.SaveSecret(b.Id, "secret-b");
        }
        var bBefore = store.Load(b.Id).Serialize();
        var bUndoBefore = store.Load(b.Id).Resources[ResourceKind.Airports].Undo.Count;

        var loadedA = store.Load(a.Id);
        var airports = loadedA.Resources[ResourceKind.Airports];
        airports.Draft[0] = airports.Draft[0].With("Name", "changed in A");
        airports.Edit([], () => { });
        airports.Edit([], () => { });
        airports.UndoEdit();
        loadedA.Jobs.Add(new BatchJob { Items = [new ChangeItem { Resource = ResourceKind.Fleets }] });
        store.Save(loadedA);
        if (windows)
            store.SaveSecret(a.Id, "rotated-a");

        var reopened = new WorkspaceStore(dir.Path);
        var bAfter = reopened.Load(b.Id);
        Assert.Equal(bBefore, bAfter.Serialize());
        Assert.Equal(bUndoBefore, bAfter.Resources[ResourceKind.Airports].Undo.Count);
        Assert.Equal("B airport", bAfter.Resources[ResourceKind.Airports].Draft[0].Get("Name"));
        Assert.Single(bAfter.Jobs);

        var aAfter = reopened.Load(a.Id);
        Assert.Equal("changed in A", aAfter.Resources[ResourceKind.Airports].Draft[0].Get("Name"));
        Assert.Equal(2, aAfter.Jobs.Count);

        if (windows)
        {
            Assert.Equal("rotated-a", reopened.LoadSecret(a.Id));
            Assert.Equal("secret-b", reopened.LoadSecret(b.Id));
        }
    }

    [Fact]
    public void A_saved_undo_step_survives_reopening_and_a_workspace_without_secret_has_none()
    {
        using var dir = TempDataDirectory.Create("vamsys-undo-");
        var store = new WorkspaceStore(dir.Path);
        var a = new Workspace { Name = "A" };
        var b = new Workspace { Name = "B" };
        a.Resources[ResourceKind.Airports].Draft.Add(Row(("ICAO/IATA", "ZBAA")));
        a.Resources[ResourceKind.Airports].Edit([], () => { });
        store.Save(a);
        store.Save(b);

        var reopened = new WorkspaceStore(dir.Path);
        Assert.Single(reopened.Load(a.Id).Resources[ResourceKind.Airports].Undo);
        Assert.Empty(reopened.Load(b.Id).Resources[ResourceKind.Airports].Draft);
        if (OperatingSystem.IsWindows())
        {
            reopened.SaveSecret(a.Id, "secret");
            Assert.Equal("secret", reopened.LoadSecret(a.Id));
            Assert.Null(reopened.LoadSecret(b.Id));
        }
    }

    [Fact]
    public void The_language_defaults_to_Chinese_persists_across_stores_and_an_invalid_value_falls_back_to_Chinese()
    {
        using var dir = TempDataDirectory.Create("vamsys-language-");
        var store = new WorkspaceStore(dir.Path);
        Assert.Equal("zh-CN", store.LoadLanguage());

        store.SaveLanguage("en-US");
        Assert.Equal("en-US", new WorkspaceStore(dir.Path).LoadLanguage());

        store.SaveLanguage("invalid");
        Assert.Equal("zh-CN", store.LoadLanguage());
    }
}
