using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace VamSys.Infrastructure;

// One save = one unit of work: under the directory's write lock, in a single transaction, the stored version is
// compared with the caller's expectation (a stale copy is rejected), the writes run, the version is advanced, and only
// then is the transaction committed. Callers update their in-memory state after Commit returns, never before; an
// exception anywhere leaves the database as it was.
internal sealed class UnitOfWork(StorageContext context)
{
    // allowNew: the workspace may not exist yet (expected must then be 0). Returns the new version.
    public long Commit(Guid id, long expected, string kind, Action<SqliteConnection, SqliteTransaction> write, bool allowNew = false)
    {
        lock (context.WriteLock)
        {
            var metrics = PerformanceRun.Current;
            metrics?.Saving();
            metrics?.Checkpoint(kind);
            var start = Stopwatch.GetTimestamp();
            var committed = false;
            try
            {
                using var c = context.Connections.Open();
                using var tx = c.BeginTransaction();
                var revision = WorkspaceRepository.ReadRevision(c, tx, id);
                if (revision == null ? !allowNew || expected != 0 : Convert.ToInt64(revision) != expected)
                    throw StorageErrors.Block("StorageStale");
                write(c, tx);
                var next = expected + 1;
                WorkspaceRepository.WriteRevision(c, tx, id, next);
                context.Fault(kind + ":BeforeCommit");
                tx.Commit();
                committed = true;
                metrics?.Saved(true, Stopwatch.GetElapsedTime(start));
                context.Fault(kind + ":AfterCommit");
                return next;
            }
            catch { if (!committed) metrics?.Saved(false, Stopwatch.GetElapsedTime(start)); throw; }
        }
    }
}
