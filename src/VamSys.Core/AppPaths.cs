namespace VamSys.Core;

public static class AppPaths
{
    public const string DataDirectoryVariable = "VAMSYS_DATA_DIR";

    // The environment variable wins; otherwise %LOCALAPPDATA%\VamSysBatch (name kept for existing installations).
    public static string DataDirectory
        => Environment.GetEnvironmentVariable(DataDirectoryVariable)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VamSysBatch");
}
