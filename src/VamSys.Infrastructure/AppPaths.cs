using VamSys.Infrastructure;

namespace VamSys.Core;

public enum DataLocationProblem { None, Unreadable, Missing }

public static class AppPaths
{
    public const string DataDirectoryVariable = "VAMSYS_DATA_DIR";
    const string DatabaseFile = "workspaces.db";
    static DataLocationState? chosen;
    static bool unreadable;

    // %LOCALAPPDATA%\VamSysBatch (name kept for existing installations). Also holds the file that names a chosen location.
    public static string DefaultDirectory
    {
        get
        {
#if VAMSYS_TEST_SUPPORT
            // Window tests move data without touching the real default directory; the product build has no such switch.
            if (Environment.GetEnvironmentVariable("VAMSYS_QA_DEFAULT_DIR") is { } redirected)
                return redirected;
#endif
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VamSysBatch");
        }
    }

    public static string LocationFile => Path.Combine(DefaultDirectory, DataLocationFile.FileName);

    // The environment variable wins and then the location chosen in the settings. A change takes effect at the next start,
    // so this keeps naming the directory the running program opened.
    public static string DataDirectory
        => Environment.GetEnvironmentVariable(DataDirectoryVariable) ?? Chosen.Directory ?? DefaultDirectory;

    public static bool ManagedByEnvironment => Environment.GetEnvironmentVariable(DataDirectoryVariable) != null;

    public static DataLocationState Chosen
    {
        get
        {
            if (chosen is null)
                ReloadLocation();
            return chosen!;
        }
    }

    public static void ReloadLocation()
    {
        try
        {
            chosen = DataLocationFile.Read(LocationFile);
            unreadable = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            chosen = DataLocationState.Default;
            unreadable = true;
        }
    }

    // A chosen location always holds a database. Without one the drive is probably absent, and opening the directory
    // would create an empty database that looks like lost data, so the start stops and asks instead.
    public static DataLocationProblem CheckLocation()
    {
        if (ManagedByEnvironment)
            return DataLocationProblem.None;
        ReloadLocation();
        if (unreadable)
            return DataLocationProblem.Unreadable;
        return chosen!.Directory is { } directory && !File.Exists(Path.Combine(directory, DatabaseFile)) ? DataLocationProblem.Missing : DataLocationProblem.None;
    }
}
