using System.Text;

namespace VamSys.Core;

// What the export asks the person; the window answers with dialogs and pickers.
public interface IExportPrompts
{
    Task ShowIssuesAsync(ResourceKind kind, IReadOnlyList<Issue> issues);
    Task<bool> ConfirmDeletesAsync(int count);
    // The folder to write the files to; null when the person cancels.
    Task<string?> ChooseFolderAsync();
}

// What the window reports after an export: that nothing had changed, or how many files were written. Neither: nothing to
// say (the person cancelled, or the data had issues the person was shown).
public sealed record ExportOutcome(bool NothingChanged, int Files);

public sealed record ImportSummary(int Rows, int Issues);

// CSV import and export, and the checks both rely on.
public sealed class ImportExportService(WorkspaceSession session, IOnlineGateway online)
{
    readonly CsvAdapter csv = new();
    readonly ChangePlanner planner = new();

    // Reads a file off the UI thread.
    public Task<CsvTable> ParseFileAsync(string path)
        => Task.Run(async () => csv.Parse(await File.ReadAllTextAsync(path, Encoding.UTF8)));

    // A baseline import replaces the snapshot, so it is never offered online, and is the default only for a resource
    // that has no snapshot yet.
    public bool BaselineAvailable => session.Workspace.Mode != RunMode.Online;
    public bool BaselineByDefault(ResourceKind kind) => BaselineAvailable && session.Workspace.Resources[kind].SnapshotAt is null;
    // Replacing the snapshot under existing drafts needs a second confirmation.
    public bool BaselineNeedsWarning(ResourceKind kind, bool baseline) => baseline && session.Workspace.Resources[kind].Draft.Count > 0;

    // Maps the file's columns, imports the rows as drafts (or as the baseline) and saves. Duplicate keys in the file
    // are refused before anything changes.
    public async Task<ImportSummary> ImportAsync(ResourceKind kind, CsvTable table, IReadOnlyDictionary<string, string> mapping, bool baseline)
    {
        var rows = csv.Map(table, mapping);
        var duplicates = rows.Where(r => Schemas.Key(kind, r) != "").GroupBy(r => Schemas.Key(kind, r)).Any(g => g.Count() > 1);
        if (duplicates)
        {
            var message = Messages.Define("Text_3BCC8B750F");
            throw MessageErrors.Attach(new FormatException(message), message);
        }
        var workspace = session.Workspace;
        CsvAdapter.Import(kind, workspace.Resources[kind], rows, baseline);
        await session.SaveAsync();
        var issues = await ValidateAsync(kind);
        return new(rows.Count, issues.Count);
    }

    // The checks of one resource, off the UI thread.
    public Task<List<Issue>> ValidateAsync(ResourceKind kind)
    {
        var workspace = session.Workspace;
        return Task.Run(() => Schemas.Validate(workspace, kind));
    }

    // The online check: every planned change of the resource must be sendable, and open conflicts must be settled.
    // Nothing is sent.
    public IReadOnlyList<MessageDescriptor> ValidateOnline(ResourceKind kind)
    {
        var workspace = session.Workspace;
        var messages = new List<MessageDescriptor>();
        foreach (var item in planner.PlanWorkspace(workspace).Where(c => c.Resource == kind))
            try
            { online.Session(workspace).PreviewPlan(item); }
            catch (Exception e) { messages.Add(MessageErrors.Describe(e)); }
        if (workspace.Resources[kind].Conflicts.Count > 0)
            messages.Add(Messages.Define("ApiConflict"));
        return messages;
    }

    // Exports the changed records of one resource as CSV files and records an "awaiting import" task for them.
    public async Task<ExportOutcome> ExportAsync(ResourceKind kind, IExportPrompts prompts)
    {
        var workspace = session.Workspace;
        var issues = await ValidateAsync(kind);
        if (issues.Count > 0)
        {
            await prompts.ShowIssuesAsync(kind, issues);
            return new(false, 0);
        }
        var changes = planner.Plan(kind, workspace.Resources[kind]);
        if (changes.Count == 0)
            return new(true, 0);
        var deletes = changes.Count(i => i.Kind == ChangeKind.Delete);
        if (deletes > 0 && !await prompts.ConfirmDeletesAsync(deletes))
            return new(false, 0);
        var folder = await prompts.ChooseFolderAsync();
        if (folder is null)
            return new(false, 0);
        var parts = await Task.Run(() => csv.Export(kind, changes));
        var prefix = $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        for (int i = 0; i < parts.Count; i++)
            await File.WriteAllBytesAsync(Path.Combine(folder, $"{prefix}-{i + 1}.csv"), parts[i]);
        workspace.Jobs.Add(new BatchJob { Mode = RunMode.Offline, Items = changes, Description = Messages.Define("Text_D964132351", ("arg0", parts.Count), ("arg1", folder)), StatusCode = JobStatus.AwaitingImport, Status = Messages.Define("Text_D964132351", ("arg0", parts.Count), ("arg1", folder)) });
        await session.SaveJobsAsync();
        return new(false, parts.Count);
    }
}
