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
        // Only uncertain writes survive a session. Confirmed candidates are read fresh on resume.
        foreach (var item in job.Items.Where(i => i.State is not (ItemState.Succeeded or ItemState.ManuallyConfirmed)
            && (i.State is ItemState.Unknown or ItemState.Running || i.CreateCompleted || i.WriteAccepted)))
            Reserve(item, session);
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
