namespace VamSys.Infrastructure;

public enum DataLocationDecision { CopyHere, UseExisting, Same, Network, Nested, BeingRetired, Unusable, NotEnoughSpace }

public sealed record DataLocationPlan(DataLocationDecision Decision, string Target);

// Decides what choosing a folder means, before anything is changed.
public static class DataLocationPlanner
{
    const string Database = "workspaces.db", Lock = "application.lock", SubFolder = "FlightOpsDesk";

    // A folder picked by hand may be a drive root or someone's documents: unless it is empty or already ours, the data
    // goes into a subfolder rather than among other files. An exact target (the default location) is taken as it is.
    public static DataLocationPlan Plan(string chosen, string current, IReadOnlyList<string> retiring, long bytesNeeded, bool exact = false, Func<string, bool>? isNetwork = null)
    {
        string target;
        try
        {
            if (!Path.IsPathFullyQualified(chosen))
                return new(DataLocationDecision.Unusable, chosen);
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return new(DataLocationDecision.Unusable, chosen); }
        if (!exact && !Ours(target) && (Same(Path.GetPathRoot(target)!, target) || !IsEmpty(target)))
            target = Path.Combine(target, SubFolder);
        // SQLite's write-ahead log needs shared memory, which network file systems do not reliably provide.
        if ((isNetwork ?? IsNetworkPath)(target))
            return new(DataLocationDecision.Network, target);
        var here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        if (Same(target, here))
            return new(DataLocationDecision.Same, target);
        if (Within(target, here) || Within(here, target))
            return new(DataLocationDecision.Nested, target);
        if (retiring.Any(r => Same(r, target)))
            return new(DataLocationDecision.BeingRetired, target);
        if (!Writable(target))
            return new(DataLocationDecision.Unusable, target);
        if (File.Exists(Path.Combine(target, Database)))
            return new(DataLocationDecision.UseExisting, target);
        return HasSpace(target, bytesNeeded) ? new(DataLocationDecision.CopyHere, target) : new(DataLocationDecision.NotEnoughSpace, target);
    }

    static bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
    static bool Within(string inner, string outer) => inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static bool Ours(string directory) => File.Exists(Path.Combine(directory, Database)) || File.Exists(Path.Combine(directory, Lock));
    static bool IsEmpty(string directory) => !Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any();

    static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        try
        { return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    static bool Writable(string directory)
    {
        var existed = Directory.Exists(directory);
        var probe = Path.Combine(directory, "write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            if (!existed)
                Directory.Delete(directory);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    static bool HasSpace(string directory, long bytes)
    {
        try
        { return new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace > bytes; }
        // A drive that does not report its free space is not refused; the copy itself reports a full disk.
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return true; }
    }
}
