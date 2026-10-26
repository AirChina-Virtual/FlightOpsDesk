using VamSys.Core;

namespace VamSys.Infrastructure;

// Reads every resource into a scratch session and, only once everything was read and saved, makes it the workspace's
// snapshot. A failure on any page, or while saving, leaves the live workspace and its identity index untouched.
internal sealed class SnapshotRefresher(ApiContext ctx)
{
    public async Task RefreshAsync(Func<Workspace, Task> persist, CancellationToken ct, Action<ResourceKind, int>? progress = null)
    {
        var workspace = ctx.Workspace;
        // Reads must not mutate the live identity index, even if a later page or save fails.
        var readWorkspace = new Workspace { AirlineId = workspace.AirlineId };
        var reader = new ApiContext(ctx.Transport, readWorkspace);
        var incoming = new Dictionary<ResourceKind, List<DataRow>>();
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var rows = new List<DataRow>();
            // Fleets precede aircraft in the enumeration, so their pages are not requested twice.
            var source = kind == ResourceKind.Aircraft ? reader.Reader.ReadAircraftAsync(incoming[ResourceKind.Fleets].ToAsyncEnumerable(), ct) : reader.Reader.ReadAllAsync(kind, ct);
            await foreach (var row in source)
            { rows.Add(row); if (rows.Count % 100 == 0) progress?.Invoke(kind, rows.Count); }
            incoming[kind] = rows;
        }
        ct.ThrowIfCancellationRequested();
        // Merging replaces each staged resource's collections, so the live workspace stays untouched until persisted.
        var staged = workspace.StageResources();
        staged.AirlineId ??= readWorkspace.AirlineId;
        foreach (var (kind, rows) in incoming)
        {
            SnapshotMerger.Merge(kind, staged.Resources[kind], rows);
            if (!staged.VerifiedOperations.Contains(kind + ":Read"))
                staged.VerifiedOperations.Add(kind + ":Read");
        }
        await persist(staged);
        workspace.StorageRevision = staged.StorageRevision;
        // No await between the durable snapshot and its in-memory index replacement.
        workspace.Resources = staged.Resources;
        workspace.VerifiedOperations = staged.VerifiedOperations;
        workspace.AirlineId = staged.AirlineId;
        // The staged copy was just saved in full and now is the workspace: the journal of the copy describes the database.
        workspace.Journal().AdoptFrom(staged.Journal());
        ctx.Identity.Adopt(reader.Identity);
        ctx.SessionAirlineId = reader.SessionAirlineId ?? ctx.SessionAirlineId;
    }
}
