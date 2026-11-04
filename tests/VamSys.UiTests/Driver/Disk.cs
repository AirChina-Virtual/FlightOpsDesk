using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UiTests.Driver;

/// <summary>What a restarted program would read, for checking what a closed window left behind.</summary>
public static class Disk
{
    /// <summary>The stored workspace, the first one or the first whose name passes <paramref name="named"/>.</summary>
    public static Workspace Stored(string directory, Func<string, bool>? named = null)
    {
        var store = new WorkspaceStore(directory);
        return store.Load(store.List().First(w => named?.Invoke(w.Name) ?? true).Id);
    }
}
