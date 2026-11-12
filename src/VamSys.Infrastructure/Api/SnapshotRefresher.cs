using VamSys.Core;

namespace VamSys.Infrastructure;

// Reads every resource (or the chosen ones, E5) into a scratch session and, only once everything was read and saved, makes
// it the workspace's snapshot. A failure on any page, or while saving, leaves the live workspace and its identity index
// untouched. A partial refresh leaves the other resources' snapshots, drafts, conflicts, read marks and index entries as
// they were; each resource keeps its own snapshot time, which the data page shows.
// E6 (D9): when a refresh fails, the resources it read in full are kept for this session (this object lives as long as the
// connection) for five minutes from the first of those reads, and the next refresh reads only the rest. They are dropped
// once any write was attempted, when the airline changes, or when the refresh succeeds; nothing is saved until all is read.
internal sealed class SnapshotRefresher(ApiContext ctx)
{
    public static readonly TimeSpan KeepPartial = TimeSpan.FromMinutes(5);
    sealed record Partial(ApiContext Reader, Dictionary<ResourceKind, List<DataRow>> Rows, DateTimeOffset ReadAt, long Writes);
    Partial? partial;

    // The resources a later refresh would not read again (if it comes in time and no write happened before it).
    public IReadOnlyCollection<ResourceKind> KeptResources => partial?.Rows.Keys ?? (IReadOnlyCollection<ResourceKind>)[];

    // E5: what a resource's rows refer to and must be read with it: route and routing airports become codes through the
    // airports, and routes and aircraft name their fleets. (5.1 found the page size capped at 100, so E4 brings nothing.)
    public static IReadOnlySet<ResourceKind> WithReferences(IEnumerable<ResourceKind> resources)
    {
        var result = new HashSet<ResourceKind>();
        foreach (var kind in resources)
        {
            result.Add(kind);
            if (kind is ResourceKind.Routes or ResourceKind.Routings)
                result.Add(ResourceKind.Airports);
            if (kind is ResourceKind.Routes or ResourceKind.Aircraft)
                result.Add(ResourceKind.Fleets);
        }
        return result;
    }

    public async Task RefreshAsync(Func<Workspace, Task> persist, CancellationToken ct, Action<ResourceKind, int>? progress = null, IEnumerable<ResourceKind>? resources = null)
    {
        var chosen = resources is null ? null : WithReferences(resources);
        var workspace = ctx.Workspace;
        var now = ctx.Transport.UtcNow;
        var kept = Usable(partial, now, workspace.AirlineId);
        partial = null;
        // Reads must not mutate the live identity index, even if a later page or save fails.
        var readWorkspace = new Workspace { AirlineId = kept?.Reader.Workspace.AirlineId ?? workspace.AirlineId };
        var reader = new ApiContext(ctx.Transport, readWorkspace) { SessionAirlineId = kept?.Reader.SessionAirlineId };
        var incoming = new Dictionary<ResourceKind, List<DataRow>>();
        var needed = Enum.GetValues<ResourceKind>().Where(k => chosen is null || chosen.Contains(k)).ToList();
        if (kept != null)
        {
            // Aircraft are read fleet by fleet: they are kept only together with the fleets they were read through.
            var reuse = kept.Rows.Keys.Where(needed.Contains)
                .Where(k => k != ResourceKind.Aircraft || kept.Rows.ContainsKey(ResourceKind.Fleets) && needed.Contains(ResourceKind.Fleets)).ToHashSet();
            foreach (var kind in reuse)
            {
                incoming[kind] = kept.Rows[kind];
                progress?.Invoke(kind, incoming[kind].Count);
            }
            // Only the index entries of resources read in full; a page of the one that failed may be gone by now.
            reader.Identity.Adopt(kept.Reader.Identity, reuse);
        }
        var writesBefore = ctx.Transport.WritesAttempted;
        try
        {
            await ReadAndSaveAsync(persist, ct, progress, chosen, workspace, readWorkspace, reader, incoming, needed);
        }
        catch
        {
            if (incoming.Count > 0)
                partial = new Partial(reader, incoming, kept?.ReadAt ?? now, writesBefore);
            throw;
        }
    }

    Partial? Usable(Partial? p, DateTimeOffset now, string? airline)
        => p != null && now >= p.ReadAt && now - p.ReadAt <= KeepPartial && p.Writes == ctx.Transport.WritesAttempted
           && (airline == null || p.Reader.Workspace.AirlineId == null || p.Reader.Workspace.AirlineId == airline) ? p : null;

    async Task ReadAndSaveAsync(Func<Workspace, Task> persist, CancellationToken ct, Action<ResourceKind, int>? progress, IReadOnlySet<ResourceKind>? chosen,
        Workspace workspace, Workspace readWorkspace, ApiContext reader, Dictionary<ResourceKind, List<DataRow>> incoming, List<ResourceKind> needed)
    {
        foreach (var kind in needed.Where(k => !incoming.ContainsKey(k)))
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
        ctx.Identity.Adopt(reader.Identity, chosen);
        ctx.SessionAirlineId = reader.SessionAirlineId ?? ctx.SessionAirlineId;
    }
}
