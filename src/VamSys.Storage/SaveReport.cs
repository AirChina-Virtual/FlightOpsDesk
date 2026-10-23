namespace VamSys.Infrastructure;

// What the last successful save of a WorkspaceStore wrote, for diagnostics and tests. Kind is "Journal" for a save
// that wrote only recorded changes and "Full" for a save that rewrote the workspace. Row counts are rows whose JSON was
// written (RowWrites), rows removed (RowDeletes) and rows that only got a new position (RowMoves).
public sealed record SaveReport(string Kind, int RowWrites, int RowDeletes, int RowMoves, int MetadataWrites, int HistoryWrites, int JobWrites, int ItemWrites, bool HeaderWritten, long JsonBytes)
{
    public bool WroteNothing => RowWrites + RowDeletes + RowMoves + MetadataWrites + HistoryWrites + JobWrites + ItemWrites == 0 && !HeaderWritten;
}

// Row counts of one stored workspace, read without loading it.
public sealed record ResourceCounts(int Snapshots, int Drafts, int Conflicts, int HistorySteps);
public sealed record WorkspaceCounts(Guid WorkspaceId, string Name, long Revision, int Jobs, IReadOnlyDictionary<VamSys.Core.ResourceKind, ResourceCounts> Resources)
{
    public int Snapshots => Resources.Values.Sum(r => r.Snapshots);
    public int Drafts => Resources.Values.Sum(r => r.Drafts);
    public int Conflicts => Resources.Values.Sum(r => r.Conflicts);
    public int HistorySteps => Resources.Values.Sum(r => r.HistorySteps);
}
