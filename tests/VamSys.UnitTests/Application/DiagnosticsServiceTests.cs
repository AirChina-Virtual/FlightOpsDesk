using System.Security.Cryptography;
using System.Text;
using VamSys.Core;

namespace VamSys.UnitTests.Application;

public sealed class DiagnosticsServiceTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "flightops-diagnostics-" + Guid.NewGuid().ToString("N"));
    readonly FakeStorage storage = new();
    readonly WorkspaceSession session;
    readonly BatchJob stored = new() { Mode = RunMode.Online };
    readonly List<BatchJob> serialized = [];

    public DiagnosticsServiceTests()
    {
        Directory.CreateDirectory(directory);
        var w = storage.Seed();
        w.Jobs.Add(stored);
        storage.Stored[w.Id] = w.Serialize();
        session = new(storage, new InteractionGate(), new FakeTimer());
        session.Open("default");
    }

    DiagnosticsService Service() => new(session, job => { serialized.Add(job); return "{\"job\":\"" + job.Id + "\"}"; });
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public async Task The_stored_task_is_written_with_a_hash_file_that_matches_it()
    {
        var path = Path.Combine(directory, "evidence.json");
        var result = await Service().ExportAsync(stored.Id, () => Task.FromResult<string?>(path));
        Assert.Equal(path, result);
        // The stored copy is exported, not the instance in memory.
        Assert.NotSame(stored, Assert.Single(serialized));
        Assert.Equal(stored.Id, serialized[0].Id);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.Equal(hash + "  evidence.json" + Environment.NewLine, await File.ReadAllTextAsync(path + ".sha256"));
    }

    [Fact]
    public async Task Cancelling_the_file_choice_writes_nothing()
    {
        var result = await Service().ExportAsync(stored.Id, () => Task.FromResult<string?>(null));
        Assert.Null(result);
        Assert.Empty(serialized);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task A_task_the_database_does_not_hold_is_refused_before_asking_for_a_file()
    {
        var asked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ExportAsync(Guid.NewGuid(), () => { asked = true; return Task.FromResult<string?>("x"); }));
        Assert.False(asked);
    }

    [Fact]
    public async Task A_file_that_cannot_be_written_fails_without_a_hash_file_or_a_change_to_the_workspace()
    {
        var path = Path.Combine(directory, "missing", "evidence.json");
        var before = session.Workspace.Serialize();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Service().ExportAsync(stored.Id, () => Task.FromResult<string?>(path)));
        Assert.False(File.Exists(path + ".sha256"));
        Assert.Equal(before, session.Workspace.Serialize());
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("journal") || c == "rows" || c == "saveAll");
    }
}
