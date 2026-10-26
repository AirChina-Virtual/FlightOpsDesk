using System.Text;
using VamSys.Core;

namespace VamSys.UnitTests.Application;

public sealed class ImportExportServiceTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "flightops-importexport-" + Guid.NewGuid().ToString("N"));
    readonly FakeStorage storage = new();
    readonly FakeOnline online = new();
    readonly WorkspaceSession session;
    readonly ImportExportService service;

    public ImportExportServiceTests()
    {
        Directory.CreateDirectory(directory);
        storage.Seed();
        session = new(storage, new InteractionGate(), new FakeTimer());
        session.Open("default");
        service = new(session, online);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    ResourceData Airports => session.Workspace.Resources[ResourceKind.Airports];
    static DataRow Row(string code, string name) => new() { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = code, ["Name"] = name, ["_delete"] = "FALSE" } };
    static Dictionary<string, string> Identity(CsvTable table) => table.Headers.ToDictionary(h => h, h => h);
    string WriteCsv(string text) { var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".csv"); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }

    sealed class Prompts : IExportPrompts
    {
        public List<IReadOnlyList<Issue>> Issues = [];
        public int ConfirmedDeletes = -1;
        public bool Confirm = true;
        public string? Folder;
        public List<string> Order = [];
        public Task ShowIssuesAsync(ResourceKind kind, IReadOnlyList<Issue> issues) { Order.Add("issues"); Issues.Add(issues); return Task.CompletedTask; }
        public Task<bool> ConfirmDeletesAsync(int count) { Order.Add("confirm"); ConfirmedDeletes = count; return Task.FromResult(Confirm); }
        public Task<string?> ChooseFolderAsync() { Order.Add("folder"); return Task.FromResult(Folder); }
    }

    [Fact]
    public async Task A_patch_import_adds_the_rows_as_drafts_without_touching_the_snapshot_and_saves_once()
    {
        var table = await service.ParseFileAsync(WriteCsv("ICAO/IATA,Name\nEGLL,London\nEGKK,Gatwick\n"));
        var summary = await service.ImportAsync(ResourceKind.Airports, table, Identity(table), baseline: false);
        Assert.Equal(2, summary.Rows);
        Assert.Equal(2, Airports.Draft.Count);
        Assert.Empty(Airports.Snapshot);
        Assert.Equal(1, storage.Calls.Count(c => c == "journal:All"));
        Assert.Equal(2, storage.Read(session.Workspace.Id).Resources[ResourceKind.Airports].Draft.Count);
    }

    [Fact]
    public async Task A_baseline_import_replaces_the_snapshot_and_is_the_default_only_when_there_is_none()
    {
        Assert.True(service.BaselineAvailable);
        Assert.True(service.BaselineByDefault(ResourceKind.Airports));
        var table = await service.ParseFileAsync(WriteCsv("ICAO/IATA,Name\nEGLL,London\n"));
        await service.ImportAsync(ResourceKind.Airports, table, Identity(table), baseline: true);
        Assert.Single(Airports.Snapshot);
        Assert.False(service.BaselineByDefault(ResourceKind.Airports));
        Assert.True(service.BaselineNeedsWarning(ResourceKind.Airports, true));
        Assert.False(service.BaselineNeedsWarning(ResourceKind.Airports, false));
        Assert.False(service.BaselineNeedsWarning(ResourceKind.Fleets, true));
        session.Workspace.Mode = RunMode.Online;
        Assert.False(service.BaselineAvailable);
        Assert.False(service.BaselineByDefault(ResourceKind.Fleets));
    }

    [Fact]
    public async Task Duplicate_keys_in_the_file_are_refused_before_anything_changes()
    {
        var table = await service.ParseFileAsync(WriteCsv("ICAO/IATA,Name\nEGLL,London\nEGLL,Again\n"));
        var error = await Assert.ThrowsAsync<FormatException>(() => service.ImportAsync(ResourceKind.Airports, table, Identity(table), false));
        Assert.Equal("Text_3BCC8B750F", MessageErrors.Describe(error).Code);
        Assert.Empty(Airports.Draft);
        Assert.DoesNotContain("journal:All", storage.Calls);
    }

    [Fact]
    public async Task A_failed_save_after_an_import_surfaces_and_the_database_keeps_the_old_rows()
    {
        var table = await service.ParseFileAsync(WriteCsv("ICAO/IATA,Name\nEGLL,London\n"));
        storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(ResourceKind.Airports, table, Identity(table), false));
        Assert.Empty(storage.Read(session.Workspace.Id).Resources[ResourceKind.Airports].Draft);
        Assert.False(session.Edits.CanSaveRows(session.Workspace));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_fails_without_changing_the_workspace()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.ParseFileAsync(Path.Combine(directory, "missing.csv")));
        Assert.Empty(Airports.Draft);
    }

    [Fact]
    public async Task Validation_reports_the_issues_of_one_resource()
    {
        Airports.Draft.Add(new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "no code", ["_delete"] = "FALSE" } });
        Assert.NotEmpty(await service.ValidateAsync(ResourceKind.Airports));
        Assert.Empty(await service.ValidateAsync(ResourceKind.Fleets));
    }

    [Fact]
    public void The_online_check_collects_unsendable_changes_and_open_conflicts_without_sending()
    {
        session.Workspace.Mode = RunMode.Online;
        var api = online.For(session.Workspace);
        api.PreviewError = new InvalidOperationException("unsupported");
        Airports.Draft.Add(Row("EGLL", "London"));
        Airports.Conflicts.Add(new(Airports.Draft[0].LocalId, "Name", "a", "b", "c"));
        var messages = service.ValidateOnline(ResourceKind.Airports);
        Assert.Equal(2, messages.Count);
        Assert.Equal("ApiConflict", messages[1].Code);
        Assert.Equal(["preview"], api.Calls);

        api.PreviewError = null;
        Airports.Conflicts.Clear();
        Assert.Empty(service.ValidateOnline(ResourceKind.Airports));
    }

    [Fact]
    public async Task Data_with_issues_is_shown_to_the_person_and_nothing_is_exported()
    {
        Airports.Draft.Add(new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "no code", ["_delete"] = "FALSE" } });
        var prompts = new Prompts { Folder = directory };
        var outcome = await service.ExportAsync(ResourceKind.Airports, prompts);
        Assert.False(outcome.NothingChanged); Assert.Equal(0, outcome.Files);
        Assert.Equal(["issues"], prompts.Order);
        Assert.Empty(session.Workspace.Jobs);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task Nothing_changed_exports_nothing_and_says_so()
    {
        var prompts = new Prompts { Folder = directory };
        var outcome = await service.ExportAsync(ResourceKind.Airports, prompts);
        Assert.True(outcome.NothingChanged);
        Assert.Empty(prompts.Order);
        Assert.Empty(session.Workspace.Jobs);
    }

    [Fact]
    public async Task An_export_writes_the_files_records_an_awaiting_import_task_and_saves_only_the_task()
    {
        Airports.Draft.Add(Row("EGLL", "London"));
        var prompts = new Prompts { Folder = directory };
        var outcome = await service.ExportAsync(ResourceKind.Airports, prompts);
        Assert.False(outcome.NothingChanged);
        Assert.Equal(1, outcome.Files);
        var file = Assert.Single(Directory.GetFiles(directory));
        Assert.StartsWith("Airports-", Path.GetFileName(file));
        Assert.Contains("EGLL", await File.ReadAllTextAsync(file));
        var job = Assert.Single(session.Workspace.Jobs);
        Assert.Equal(JobStatus.AwaitingImport, job.StatusCode);
        Assert.Equal(RunMode.Offline, job.Mode);
        Assert.Equal("journal:Jobs", storage.Calls.Last());
        Assert.Single(storage.Read(session.Workspace.Id).Jobs);
    }

    [Fact]
    public async Task Deleting_records_needs_a_confirmation_and_cancelling_it_exports_nothing()
    {
        var existing = new DataRow { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = "EGLL", ["Name"] = "London", ["_delete"] = "FALSE" }, Identity = new ResourceIdentity("1", "7") };
        Airports.Snapshot.Add(existing);
        Airports.Draft.Add(existing.With("_delete", "TRUE"));
        var prompts = new Prompts { Folder = directory, Confirm = false };
        var outcome = await service.ExportAsync(ResourceKind.Airports, prompts);
        Assert.False(outcome.NothingChanged); Assert.Equal(0, outcome.Files);
        Assert.Equal(1, prompts.ConfirmedDeletes);
        Assert.Equal(["confirm"], prompts.Order);
        Assert.Empty(Directory.GetFiles(directory));
        Assert.Empty(session.Workspace.Jobs);

        prompts.Confirm = true;
        Assert.Equal(1, (await service.ExportAsync(ResourceKind.Airports, prompts)).Files);
    }

    [Fact]
    public async Task Cancelling_the_folder_choice_exports_nothing()
    {
        Airports.Draft.Add(Row("EGLL", "London"));
        var outcome = await service.ExportAsync(ResourceKind.Airports, new Prompts { Folder = null });
        Assert.False(outcome.NothingChanged); Assert.Equal(0, outcome.Files);
        Assert.Empty(session.Workspace.Jobs);
    }

    [Fact]
    public async Task A_failed_save_after_an_export_surfaces_and_the_database_holds_no_task()
    {
        Airports.Draft.Add(Row("EGLL", "London"));
        storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(ResourceKind.Airports, new Prompts { Folder = directory }));
        Assert.Empty(storage.Read(session.Workspace.Id).Jobs);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_written_fails_before_the_task_is_recorded()
    {
        Airports.Draft.Add(Row("EGLL", "London"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.ExportAsync(ResourceKind.Airports, new Prompts { Folder = Path.Combine(directory, "missing") }));
        Assert.Empty(session.Workspace.Jobs);
    }
}
