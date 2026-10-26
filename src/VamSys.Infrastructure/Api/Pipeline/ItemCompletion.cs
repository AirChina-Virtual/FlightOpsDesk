using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

// A verified item becomes part of the workspace: the server's row replaces the snapshot row, the draft follows,
// the operation counts as verified and the duplicate caches learn the confirmed version.
internal sealed class ItemCompletion(JobRun run)
{
    readonly Dictionary<ResourceKind, (ResourceData Data, SnapshotPositions Positions)> positions = [];

    // One position index per resource, dropped if the workspace swapped the resource's data during the run.
    SnapshotPositions Positions(ResourceKind kind)
    {
        var data = run.Workspace.Resources[kind];
        if (!positions.TryGetValue(kind, out var found) || !ReferenceEquals(found.Data, data))
            positions[kind] = found = (data, new SnapshotPositions(data));
        return found.Positions;
    }

    public async Task CompleteAsync(ChangeItem item, DataRow? verified)
    {
        var api = run.Api;
        var workspace = run.Workspace;
        if (item.Resource == ResourceKind.Fleets)
            api.InvalidateParameters();
        if (!run.Sink.HasWriter)
        {
            var rebased = ApplyRebase(workspace, item, verified, Positions(item.Resource));
            api.ConfirmCandidate(item, rebased, run.Queries);
            if (verified == null && (item.RemoteId ?? item.Before?.Identity?.RemoteId) is string id)
                api.Forget(item.Resource, id);
            return;
        }
        // Stage the change on a one-row copy, write it as one atomic checkpoint, then let the live workspace follow.
        var original = workspace.Resources[item.Resource];
        var current = original.FindDraftRow(item.After.LocalId);
        var data = new ResourceData { Draft = current == null ? [] : [current], SnapshotAt = original.SnapshotAt };
        var staged = workspace.StageResource(item.Resource, data);
        var stagedItem = JsonSerializer.Deserialize<ChangeItem>(JsonSerializer.Serialize(item))!;
        ApplyRebase(staged, stagedItem, verified, null);
        var delta = new ResourceRebaseDelta(item.Resource, item.After.LocalId,
            new(verified == null ? RowMutationKind.Delete : RowMutationKind.Upsert, data.Snapshot.FirstOrDefault()),
            new(current == null ? RowMutationKind.Keep : data.Draft.Count == 0 ? RowMutationKind.Delete : RowMutationKind.Upsert, data.Draft.FirstOrDefault()),
            data.SnapshotAt!.Value, true, ApiOperationPolicy.Key(item));
        await run.Sink.WriteRebaseAsync(stagedItem, delta);
        var snapshot = Positions(item.Resource);
        snapshot.Remove(delta.LocalId);
        if (delta.Snapshot.Row != null)
            snapshot.Append(delta.Snapshot.Row);
        if (delta.Draft.Action == RowMutationKind.Delete)
            original.Draft.RemoveAll(r => r.LocalId == delta.LocalId);
        else if (delta.Draft.Row != null)
            original.ReplaceDraftRows([delta.Draft.Row]);
        original.ClearHistory(invalidateIndex: delta.Draft.Action == RowMutationKind.Delete);
        original.SnapshotAt = delta.SnapshotAt;
        workspace.VerifiedOperations = staged.VerifiedOperations;
        item.RemoteId = stagedItem.RemoteId;
        item.After = stagedItem.After;
        item.CreateCompleted = stagedItem.CreateCompleted;
        item.RecoveryCandidateId = stagedItem.RecoveryCandidateId;
        item.Rebased = stagedItem.Rebased;
        item.State = stagedItem.State;
        item.Message = stagedItem.Message;
        item.Description = stagedItem.Description;
        item.DeletionVerification = stagedItem.DeletionVerification;
        api.ConfirmCandidate(item, verified, run.Queries);
        if (verified == null && (item.RemoteId ?? item.Before?.Identity?.RemoteId) is string removed)
            api.Forget(item.Resource, removed);
        run.Sink.NotifyRebased(item);
    }

    // Returns the verified row as stored in the snapshot (carrying the draft row's id and any draft-only columns), or null after a delete.
    // snapshot: removes and appends by position in the (large) live snapshot; null on a one-row staging copy.
    internal static DataRow? ApplyRebase(Workspace workspace, ChangeItem item, DataRow? verified, SnapshotPositions? snapshot)
    {
        var data = workspace.Resources[item.Resource];
        if (snapshot != null)
            snapshot.Remove(item.After.LocalId);
        else
            data.Snapshot.RemoveAll(r => r.LocalId == item.After.LocalId);
        if (verified == null)
        {
            data.Draft.RemoveAll(r => r.LocalId == item.After.LocalId && Schemas.Deleted(r));
        }
        else
        {
            item.RemoteId = verified.Identity!.RemoteId;
            item.After = item.After.WithIdentity(verified.Identity);
            if (item.Kind == ChangeKind.Create)
                item.CreateCompleted = true;
            item.RecoveryCandidateId = null;
            var localId = item.After.LocalId;
            var current = data.FindDraftRow(localId);
            if (current != null)
            {
                var edited = current.ToBuilder();
                foreach (var field in verified.Fields.Keys)
                    if (field == "ID" || current.Get(field) == item.After.Get(field) || LocalReference.ContainsLocal(current.Get(field)))
                        edited[field] = verified.Get(field);
                edited.Identity = verified.Identity;
                edited.RawApiJson = verified.RawApiJson;
                var replacement = edited.ToRow();
                var stored = verified.ToBuilder();
                stored.LocalId = localId;
                foreach (var p in replacement.Fields.Where(p => !verified.Fields.ContainsKey(p.Key)))
                    stored[p.Key] = p.Value;
                verified = stored.ToRow();
                data.ReplaceDraftRows([replacement]);
            }
            else
                verified = verified.WithLocalId(localId);
            if (snapshot != null)
                snapshot.Append(verified);
            else
                data.Snapshot.Add(verified);
        }
        item.Rebased = true;
        item.State = ItemState.Succeeded;
        item.SetMessage(Messages.Define(item.DeletionVerification?.Outcome == "ApiInvisible" ? "ApiDeletedInvisible" : "Text_631F4DBEC2"));
        data.ClearHistory(invalidateIndex: verified is null);
        data.SnapshotAt = DateTimeOffset.UtcNow;
        var operation = ApiOperationPolicy.Key(item);
        if (!workspace.VerifiedOperations.Contains(operation))
            workspace.VerifiedOperations.Add(operation);
        return verified;
    }
}
