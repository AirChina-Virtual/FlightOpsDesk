using VamSys.Core;

namespace VamSys.Infrastructure;

// What the program does about the data location once it holds the lease of the directory it opened.
public static class DataLocationStartup
{
    // Sets aside the databases of locations the data has left. The previous process has just closed, so its files may
    // take a moment to be released; whatever still fails is tried again at the next start.
    public static void FinishMoves(string locationFile, DataLocationState state, string opened)
    {
        if (state.Retire.Count == 0)
            return;
        var remaining = state.Retire.Where(directory => !string.Equals(Path.GetFullPath(directory), Path.GetFullPath(opened), StringComparison.OrdinalIgnoreCase) && !SetAside(directory)).ToList();
        if (remaining.Count != state.Retire.Count)
            DataLocationFile.Write(locationFile, state with { Retire = remaining });
    }

    static bool SetAside(string directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (WorkspaceStore.TrySetAside(directory))
                return true;
            if (!Directory.Exists(directory))
                return false;
            Thread.Sleep(100);
        }
        return false;
    }

    // Goes back to the default location after a chosen one could not be found. Nothing is deleted.
    public static void UseDefault() => DataLocationFile.Write(AppPaths.LocationFile, AppPaths.Chosen with { Directory = null });
}
