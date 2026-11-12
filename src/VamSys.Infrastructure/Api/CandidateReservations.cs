using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// The write-side guard of the duplicate check: while a write's outcome is uncertain its identities stay reserved in the
// session, so no other item of the run can create "the same thing again" under another name or code.
internal sealed class CandidateReservations(ApiContext ctx, DuplicateFinder finder)
{
    // Reserves the identities an item may have created or changed. Legacy archives may lack raw identities or contain
    // unresolved local references: creation fails closed for them (opaque) without preventing read-only recovery.
    public void Reserve(ChangeItem item, CandidateQuerySession session)
    {
        session.Bind(ctx.Workspace.Id, ctx.Workspace.AirlineId);
        var keys = new HashSet<CandidateSignature>(CandidateSignature.Equality);
        var opaque = false;
        try
        {
            if (item.Before != null)
                keys.UnionWith(finder.RemoteSignatures(item.Resource, item.Before));
            if (item.Kind != ChangeKind.Delete)
                keys.Add(finder.DraftSignature(item.Resource, item.After));
        }
        catch (Exception e) when (e is InvalidOperationException or System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        { opaque = true; }
        session.Reserve(item, keys, opaque || keys.Count == 0);
    }

    public void Seed(BatchJob job, CandidateQuerySession session)
    {
        session.Bind(ctx.Workspace.Id, ctx.Workspace.AirlineId);
        // Only uncertain writes survive a session. Confirmed candidates are read fresh on resume. Since 3.1 a new task may
        // run while an older one still has unknown items, so their identities are reserved too.
        foreach (var item in ctx.Workspace.Jobs.Where(j => j.Mode == RunMode.Online && !ReferenceEquals(j, job)).Prepend(job).SelectMany(j => j.Items)
            .Where(i => i.State is not (ItemState.Succeeded or ItemState.ManuallyConfirmed)
            && (i.State is ItemState.Unknown or ItemState.Running || i.CreateCompleted || i.WriteAccepted)))
            Reserve(item, session);
        ChooseLookups(job, session);
    }

    // E8: a filtered lookup costs one GET per distinct new fleet name and two per new airport code (filter[icao] and
    // filter[iata]); the whole list costs one GET per 100 rows (the server's page cap, 5.1). Filtered only when cheaper.
    // 5.1 showed both filters miss nothing the duplicate rule would find: ICAO/IATA match exactly without case, the
    // fleet name by substring without case (a superset the index below narrows to the rule's own matches).
    void ChooseLookups(BatchJob job, CandidateQuerySession session)
    {
        foreach (var kind in new[] { ResourceKind.Airports, ResourceKind.Fleets })
        {
            var names = job.Items.Where(i => i.Resource == kind && i.Kind == ChangeKind.Create && i.State is not (ItemState.Succeeded or ItemState.ManuallyConfirmed))
                .Select(i => i.After.Get(kind == ResourceKind.Airports ? "ICAO/IATA" : "Name")).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var pages = Math.Max(1, (ctx.Workspace.Resources[kind].Snapshot.Count + 99) / 100);
            if (names > 0 && names * (kind == ResourceKind.Airports ? 2 : 1) < pages)
                session.FilteredLookup.Add(kind);
        }
    }

    // The reason a create must wait (another item's outcome is unknown and could be the same record), or null.
    public string? BlockReason(ChangeItem item, CandidateQuerySession session)
    {
        session.Bind(ctx.Workspace.Id, ctx.Workspace.AirlineId);
        return session.Blocked(finder.DraftSignature(item.Resource, item.After));
    }

    // A write was verified: the confirmed version replaces any cached copy of the record, and the reservation ends.
    public void Confirm(ChangeItem item, DataRow? row, CandidateQuerySession session)
    {
        session.Bind(ctx.Workspace.Id, ctx.Workspace.AirlineId);
        var keys = row == null ? [] : finder.RemoteSignatures(item.Resource, row);
        var id = row?.Identity?.RemoteId ?? item.RemoteId ?? item.Before?.Identity?.RemoteId;
        if (id == null)
            throw Block("ApiRefreshFirst");
        if (session.Indexes.TryGetValue(item.Resource, out var index))
        { if (row == null) index.Remove(id); else index.Put(row, keys); }
        // E8 lookups too: each finds only its own signature, so the confirmed row may go into all of them.
        foreach (var (signature, lookup) in session.Lookups)
            if (signature.Kind == item.Resource)
            { if (row == null) lookup.Remove(id); else lookup.Put(row, keys); }
        if (!session.Confirmed.TryGetValue(item.Resource, out var confirmed))
            session.Confirmed[item.Resource] = confirmed = new();
        if (row == null)
            confirmed.Remove(id);
        else
            confirmed.Put(row, keys);
        if (item.Resource is ResourceKind.Routes or ResourceKind.Routings)
        {
            // The confirmed version replaces any cached copy, including one under the old airport pair.
            foreach (var (pair, rows) in session.Pairs)
                if (pair.Kind == item.Resource)
                    rows.RemoveAll(r => r.Identity!.RemoteId == id);
            if (row != null && keys.Length > 0 && session.Pairs.TryGetValue((item.Resource, keys[0].Departure, keys[0].Arrival), out var current))
                current.Add(row);
        }
        session.Release(item);
    }
}
