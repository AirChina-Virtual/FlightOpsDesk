using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

static class PackageChecks
{
    public static void Verify(string directory, bool qa)
    {
        directory = Path.GetFullPath(directory);
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        foreach (var name in new[] { "FlightOpsDesk.exe", "FlightOpsDesk.dll", "VamSys.Core.dll", "VamSys.Infrastructure.dll",
            "FlightOpsDesk.deps.json", "FlightOpsDesk.runtimeconfig.json", "FlightOpsDesk.pri", "MainWindow.xbf",
            "Assets/FlightOpsDesk.ico", "Assets/FlightOpsDesk.png", "README.md", "docs/USER-GUIDE.md", "docs/STORAGE-MIGRATION.md" })
            Require(File.Exists(Path.Combine(directory, name)), $"Missing package file: {name}");

        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            Require(!relative.Split('/').Any(part => part is "tests" or "src" or "obj" or ".tools"), $"Developer directory in package: {relative}");
            Require(!Regex.IsMatch(relative, @"\.(cs|csproj|slnx|ps1)$", RegexOptions.IgnoreCase), $"Developer file in package: {relative}");
            Require(qa || !file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase), $"Debug symbols in product package: {relative}");
            Require(!Path.GetFileName(file).StartsWith("VamSys.Tests", StringComparison.OrdinalIgnoreCase), $"Test runner in package: {relative}");
            if (relative.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
                Require(relative is "docs/USER-GUIDE.md" or "docs/STORAGE-MIGRATION.md", $"Developer document in package: {relative}");
        }

        foreach (var name in new[] { "FlightOpsDesk", "VamSys.Core", "VamSys.Infrastructure" })
        {
            using var stream = File.OpenRead(Path.Combine(directory, name + ".dll"));
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var methods = metadata.MethodDefinitions.Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name)).ToHashSet();
            var references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name));
            Require(!references.Any(reference => reference.Contains("Tests", StringComparison.OrdinalIgnoreCase)), $"Test assembly referenced by {name}");
            if (name == "FlightOpsDesk")
                foreach (var method in new[] { "InitializeVerification", "ConfigureVerificationTheme", "VerificationCommand" })
                    Require(methods.Contains(method) == qa, $"Unexpected QA entry point {method} in {name} (QA={qa})");
            if (name == "VamSys.Infrastructure")
            {
                foreach (var method in new[] { "get_Barrier", "set_Barrier", "OnStorageBarrier" })
                    Require(methods.Contains(method) == qa, $"Unexpected storage test hook {method} (QA={qa})");
                var friends = metadata.GetAssemblyDefinition().GetCustomAttributes().Any(handle =>
                {
                    var constructor = metadata.GetCustomAttribute(handle).Constructor;
                    if (constructor.Kind != HandleKind.MemberReference) return false;
                    var parent = metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                    return parent.Kind == HandleKind.TypeReference &&
                        metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name) == "InternalsVisibleToAttribute";
                });
                Require(friends == qa, $"Unexpected test friend assembly access (QA={qa})");
                Require(metadata.ManifestResources.Any(handle => metadata.GetString(metadata.GetManifestResource(handle).Name) == "Operations.OpenApi.json"),
                    "Runtime API contract missing");
            }
        }
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "FlightOpsDesk.deps.json")));
        Require(!deps.RootElement.GetProperty("libraries").EnumerateObject().Any(library => library.Name.Contains("VamSys.Tests", StringComparison.OrdinalIgnoreCase)),
            "Test dependency in product dependency manifest");

        // User-facing links must still work after leaving the developer documentation out.
        foreach (var file in files.Where(file => file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\]\(([^)]+)\)"))
            {
                var link = match.Groups[1].Value;
                if (link.StartsWith('#') || Uri.TryCreate(link, UriKind.Absolute, out _)) continue;
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, link.Split('#')[0]));
                Require(target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(target),
                    $"Broken package documentation link in {Path.GetFileName(file)}: {link}");
            }
        Console.WriteLine($"PASS {(qa ? "QA" : "product")} package: required runtime files, isolated test hooks, dependencies and documentation links; {files.Length} files, {files.Sum(file => new FileInfo(file).Length):N0} bytes");
    }
}
