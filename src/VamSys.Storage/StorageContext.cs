using System.Collections.Concurrent;
using System.Diagnostics;

namespace VamSys.Infrastructure;

// What every storage component shares: the connection factory, the per-directory write lock and the fault hook.
internal sealed partial class StorageContext
{
    static readonly ConcurrentDictionary<string, object> locks = new(StringComparer.OrdinalIgnoreCase);
    public StorageContext(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        Connections = new(directory);
        WriteLock = locks.GetOrAdd(directory, _ => new());
    }
    public SqliteConnectionFactory Connections { get; }
    public object WriteLock { get; }
    // Calls (and their argument expressions) are removed by the compiler unless VAMSYS_TEST_SUPPORT is defined.
    [Conditional("VAMSYS_TEST_SUPPORT")]
    public void Fault(string point)
    {
#if VAMSYS_TEST_SUPPORT
        Faults?.Reached(point);
#endif
    }
}
