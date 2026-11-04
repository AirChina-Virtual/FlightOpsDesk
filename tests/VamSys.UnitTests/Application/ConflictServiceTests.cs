using VamSys.Core;

namespace VamSys.UnitTests.Application;

public class ConflictServiceTests
{
    sealed class Fixture
    {
        public readonly FakeStorage Storage = new();
        public readonly WorkspaceSession Session;
        public readonly ConflictService Service;
        public readonly DataRow Local, Other;
        public Fixture()
        {
            Storage.Seed();
            Session = new(Storage, new InteractionGate(), new FakeTimer());
            Session.Open("default");
            Local = Row("ZBAA", "mine");
            Other = Row("ZSPD", "theirs");
            var data = Session.Workspace.Resources[ResourceKind.Airports];
            data.Draft.AddRange([Local, Other]);
            data.Conflicts.Add(new(Local.LocalId, "Name", "orig", "mine", "server"));
            data.Conflicts.Add(new(Other.LocalId, "Name", "orig", "theirs", "server2"));
            Storage.Stored[Session.Workspace.Id] = Session.Workspace.Serialize();
            Service = new(Session);
        }
        public ResourceData Data => Session.Workspace.Resources[ResourceKind.Airports];
        public int Saves => Storage.Calls.Count(c => c == "journal:All");
    }

    static DataRow Row(string code, string name) => new() { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = code, ["Name"] = name } };

    [Fact]
    public async Task Each_choice_is_applied_in_memory_and_everything_is_saved_once_at_the_end()
    {
        var f = new Fixture();
        var prompts = new List<ConflictPrompt>();
        var answers = new Queue<ConflictChoice>([ConflictChoice.UseRemote, ConflictChoice.KeepLocal]);
        await f.Service.ResolveAsync([ResourceKind.Airports], p => { prompts.Add(p); Assert.Equal(0, f.Saves); return Task.FromResult(answers.Dequeue()); });
        Assert.Equal(["server", "theirs"], f.Data.Draft.Select(r => r.Get("Name")));
        Assert.Empty(f.Data.Conflicts);
        Assert.Equal(1, f.Saves);
        Assert.All(prompts, p => Assert.True(p.CanKeepLocal));
        Assert.Equal(["Name", "Name"], prompts.Select(p => p.Conflict.Field));
        Assert.Equal("server", f.Storage.Read(f.Session.Workspace.Id).Resources[ResourceKind.Airports].Draft[0].Get("Name"));
    }

    [Fact]
    public async Task Stopping_keeps_what_was_settled_and_saves_it()
    {
        var f = new Fixture();
        var answers = new Queue<ConflictChoice>([ConflictChoice.UseRemote, ConflictChoice.Stop]);
        await f.Service.ResolveAsync([ResourceKind.Airports], _ => Task.FromResult(answers.Dequeue()));
        Assert.Single(f.Data.Conflicts);
        Assert.Equal("server", f.Data.Draft[0].Get("Name"));
        Assert.Equal(1, f.Saves);
    }

    [Fact]
    public async Task Stopping_at_once_changes_and_saves_nothing()
    {
        var f = new Fixture();
        var asked = 0;
        await f.Service.ResolveAsync([ResourceKind.Airports], _ => { asked++; return Task.FromResult(ConflictChoice.Stop); });
        Assert.Equal(1, asked);
        Assert.Equal(2, f.Data.Conflicts.Count);
        Assert.Equal(0, f.Saves);
    }

    [Fact]
    public async Task A_conflict_whose_row_is_gone_is_dropped_without_asking()
    {
        var f = new Fixture();
        f.Data.Draft.Remove(f.Other);
        var asked = 0;
        await f.Service.ResolveAsync([ResourceKind.Airports], _ => { asked++; return Task.FromResult(ConflictChoice.KeepLocal); });
        Assert.Equal(1, asked);
        Assert.Empty(f.Data.Conflicts);
        Assert.Equal(1, f.Saves);
    }

    [Fact]
    public async Task A_conflict_on_the_whole_record_cannot_keep_the_local_value_and_using_the_server_removes_the_row()
    {
        var f = new Fixture();
        f.Data.Conflicts.Clear();
        f.Data.Conflicts.Add(new(f.Local.LocalId, "*", null, null, null));
        ConflictPrompt? seen = null;
        await f.Service.ResolveAsync([ResourceKind.Airports], p => { seen = p; return Task.FromResult(ConflictChoice.UseRemote); });
        Assert.False(seen!.CanKeepLocal);
        Assert.DoesNotContain(f.Local, f.Data.Draft);
        Assert.Equal(1, f.Saves);
    }

    [Fact]
    public async Task A_failing_dialog_still_saves_what_was_settled_before_it_and_rethrows()
    {
        var f = new Fixture();
        var step = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ResolveAsync([ResourceKind.Airports],
            _ => ++step == 1 ? Task.FromResult(ConflictChoice.UseRemote) : throw new InvalidOperationException("dialog")));
        Assert.Equal(1, f.Saves);
        Assert.Single(f.Data.Conflicts);
    }

    [Fact]
    public async Task A_failed_save_surfaces_and_leaves_the_database_with_the_old_conflicts()
    {
        var f = new Fixture();
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Service.ResolveAsync([ResourceKind.Airports], _ => Task.FromResult(ConflictChoice.UseRemote)));
        Assert.Equal(2, f.Storage.Read(f.Session.Workspace.Id).Resources[ResourceKind.Airports].Conflicts.Count);
        Assert.True(f.Session.Edits.RequiresFullSave || !f.Session.Edits.CanSaveRows(f.Session.Workspace));
    }

    [Fact]
    public async Task Nothing_to_settle_asks_nothing_and_saves_nothing()
    {
        var f = new Fixture();
        f.Data.Conflicts.Clear();
        await f.Service.ResolveAsync(Enum.GetValues<ResourceKind>(), _ => throw new InvalidOperationException("asked"));
        Assert.Equal(0, f.Saves);
    }
}
