namespace VamSys.Core;

public enum RowMutationKind { Keep = 0, Upsert = 1, Delete = 2 }
public sealed record RowMutation(RowMutationKind Action, DataRow? Row = null);
// Conflicts, when set, replaces the resource's whole conflict list in the same commit.
public sealed record ResourceRebaseDelta(ResourceKind Resource, Guid LocalId, RowMutation Snapshot, RowMutation Draft, DateTimeOffset SnapshotAt, bool ClearHistory, string? VerifiedOperation, IReadOnlyList<MergeConflict>? Conflicts = null);
