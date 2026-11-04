namespace VamSys.UiTests.Driver;

/// <summary>Paths in the checkout the tests run from.</summary>
public static class Repo
{
    public static readonly string Root = FindRoot();

    /// <summary>The QA package; VAMSYS_QA_EXE overrides it (for example to test a package built elsewhere).</summary>
    public static string QaExecutable =>
        Environment.GetEnvironmentVariable("VAMSYS_QA_EXE") is { Length: > 0 } path ? path : Path.Combine(Root, "artifacts", "v3-qa", "FlightOpsDesk.exe");

    /// <summary>The product package, as scripts/build.ps1 -Publish leaves it.</summary>
    public static string ProductExecutable => Path.Combine(Root, "artifacts", "flightops-app", "FlightOpsDesk.exe");

    /// <summary>A directory under the git-ignored artifacts folder, created if missing.</summary>
    public static string Artifacts(params string[] parts) =>
        Directory.CreateDirectory(Path.Combine([Root, "artifacts", .. parts])).FullName;

    static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "VamSys.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("VamSys.slnx not found above " + AppContext.BaseDirectory);
    }
}
