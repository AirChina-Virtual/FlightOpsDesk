using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace VamSys.IntegrationTests.Packaging;

// The package check (tools/VamSys.QaSeed, run by scripts/build.ps1 on every publish). Each case starts from a valid
// product or QA package made of small generated assemblies, breaks one thing, and expects that exact refusal.
public sealed class PackageChecksTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vamsys-package-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    // What one generated assembly declares.
    sealed record Assembly(string Name)
    {
        public List<string> Types { get; } = [];
        public List<string> Methods { get; } = [];
        public List<string> Strings { get; } = [];
        public List<string> References { get; } = [];
        public List<string> Resources { get; } = [];
        public bool Friends { get; set; }
    }

    // The assemblies of a valid package: the QA build has its entry points, switches, storage hooks and friend grants.
    static Dictionary<string, Assembly> Assemblies(bool qa)
    {
        var app = new Assembly("FlightOpsDesk");
        app.Types.Add("MainWindow");
        app.Methods.Add("Render");
        if (qa)
        {
            app.Methods.AddRange(["CreateVerification", "InitializeVerification", "ConfigureVerificationTheme", "VerificationCommand"]);
            app.Types.Add("WindowVerification");
            app.Strings.AddRange(["VAMSYS_QA_PIPE", "VAMSYS_QA_THEME"]);
        }
        var storage = new Assembly("VamSys.Storage") { Friends = qa };
        storage.Types.Add("WorkspaceStore");
        if (qa)
        {
            storage.Methods.AddRange(["get_Barrier", "set_Barrier", "get_Faults", "set_Faults"]);
            storage.Types.AddRange(["ActionStorageFaults", "StorageTestHooks"]);
        }
        var infrastructure = new Assembly("VamSys.Infrastructure") { Friends = qa };
        infrastructure.Resources.Add("Operations.OpenApi.json");
        if (qa)
            infrastructure.Strings.Add("VAMSYS_QA_OPERATIONS_ORIGIN");
        var all = new[] { app, storage, infrastructure, new Assembly("VamSys.Domain"), new Assembly("VamSys.Application"), new Assembly("VamSys.Presentation") };
        return all.ToDictionary(a => a.Name);
    }

    string Package(bool qa, Action<Dictionary<string, Assembly>>? change = null)
    {
        var assemblies = Assemblies(qa);
        change?.Invoke(assemblies);
        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        Directory.CreateDirectory(Path.Combine(directory, "docs"));
        foreach (var assembly in assemblies.Values)
            Write(assembly, Path.Combine(directory, assembly.Name + ".dll"));
        foreach (var file in new[] { "FlightOpsDesk.exe", "FlightOpsDesk.runtimeconfig.json", "FlightOpsDesk.pri", "MainWindow.xbf", "Theme.xbf", "Assets/FlightOpsDesk.ico", "Assets/FlightOpsDesk.png" })
            File.WriteAllText(Path.Combine(directory, file), "x");
        File.WriteAllText(Path.Combine(directory, "FlightOpsDesk.deps.json"), """{"libraries":{"FlightOpsDesk/1.0.0":{},"Microsoft.Data.Sqlite/10.0.12":{}}}""");
        File.WriteAllText(Path.Combine(directory, "README.md"), "[Guide](docs/USER-GUIDE.md) · [Upgrade](docs/STORAGE-MIGRATION.md#backup) · [Site](https://example.com/)");
        File.WriteAllText(Path.Combine(directory, "docs", "USER-GUIDE.md"), "[Home](../README.md)");
        File.WriteAllText(Path.Combine(directory, "docs", "STORAGE-MIGRATION.md"), "Plain text.");
        if (qa)
            File.WriteAllText(Path.Combine(directory, "FlightOpsDesk.pdb"), "x");
        return directory;
    }

    static string Refusal(string package, bool qa) => Assert.Throws<InvalidOperationException>(() => PackageChecks.Verify(package, qa)).Message;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_valid_product_or_QA_package_passes(bool qa)
        => PackageChecks.Verify(Package(qa), qa);

    [Fact]
    public void A_product_package_with_any_QA_entry_point_switch_or_hook_is_refused()
    {
        Assert.Equal("Unexpected QA entry point VerificationCommand in FlightOpsDesk (QA=False)",
            Refusal(Package(false, a => a["FlightOpsDesk"].Methods.Add("VerificationCommand")), false));
    }

    [Fact]
    public void A_product_package_with_a_QA_switch_string_is_refused()
        => Assert.Equal("Unexpected QA switch in FlightOpsDesk (QA=False)", Refusal(Package(false, a => a["FlightOpsDesk"].Strings.Add("VAMSYS_QA_DATAGRID")), false));

    // 4.1: only the QA build can be pointed at a mock server.
    [Fact]
    public void A_product_package_with_the_mock_server_switch_is_refused_and_a_QA_package_without_it_too()
    {
        Assert.Equal("Unexpected mock-server switch in VamSys.Infrastructure (QA=False)",
            Refusal(Package(false, a => a["VamSys.Infrastructure"].Strings.Add("VAMSYS_QA_OPERATIONS_ORIGIN")), false));
        Dispose();
        Assert.Equal("Unexpected mock-server switch in VamSys.Infrastructure (QA=True)",
            Refusal(Package(true, a => a["VamSys.Infrastructure"].Strings.Remove("VAMSYS_QA_OPERATIONS_ORIGIN")), true));
    }

    [Fact]
    public void A_product_package_with_the_window_hook_implementation_is_refused()
        => Assert.Equal("Unexpected QA hook implementation in FlightOpsDesk (QA=False)", Refusal(Package(false, a => a["FlightOpsDesk"].Types.Add("WindowVerification")), false));

    [Theory]
    [InlineData("StorageTestHooks")]
    [InlineData("ActionStorageFaults")]
    public void A_product_package_with_a_storage_test_type_is_refused(string type)
        => Assert.Equal($"Unexpected storage test type {type} (QA=False)", Refusal(Package(false, a => a["VamSys.Storage"].Types.Add(type)), false));

    [Fact]
    public void A_product_package_with_the_storage_barrier_or_friend_grants_is_refused()
    {
        Assert.Equal("Unexpected storage test hook set_Barrier (QA=False)", Refusal(Package(false, a => a["VamSys.Storage"].Methods.Add("set_Barrier")), false));
        Dispose();
        Assert.Equal("Unexpected test friend assembly access (QA=False)", Refusal(Package(false, a => a["VamSys.Infrastructure"].Friends = true), false));
    }

    [Fact]
    public void A_QA_package_missing_its_hooks_is_refused()
    {
        Assert.Equal("Unexpected QA entry point CreateVerification in FlightOpsDesk (QA=True)",
            Refusal(Package(true, a => a["FlightOpsDesk"].Methods.Remove("CreateVerification")), true));
        Dispose();
        Assert.Equal("Unexpected storage test type StorageTestHooks (QA=True)", Refusal(Package(true, a => a["VamSys.Storage"].Types.Remove("StorageTestHooks")), true));
    }

    // The valid QA package above already carries a PDB.
    [Fact]
    public void Debug_symbols_ship_only_in_the_QA_package()
    {
        var package = Package(false);
        File.WriteAllText(Path.Combine(package, "VamSys.Domain.pdb"), "x");
        Assert.Equal("Debug symbols in product package: VamSys.Domain.pdb", Refusal(package, false));
    }

    [Theory]
    [InlineData("VamSys.QaSeed.dll", "Test or tool assembly in package: VamSys.QaSeed.dll")]
    [InlineData("VamSys.UnitTests.dll", "Test or tool assembly in package: VamSys.UnitTests.dll")]
    [InlineData("tests/x.txt", "Developer directory in package: tests/x.txt")]
    [InlineData("Build.ps1", "Developer file in package: Build.ps1")]
    [InlineData("docs/ARCHITECTURE.md", "Developer document in package: docs/ARCHITECTURE.md")]
    public void Test_tool_and_developer_files_are_refused(string file, string refusal)
    {
        var package = Package(false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(package, file))!);
        File.WriteAllText(Path.Combine(package, file), "x");
        Assert.Equal(refusal, Refusal(package, false));
    }

    [Fact]
    public void A_shipped_assembly_that_references_a_test_assembly_is_refused()
        => Assert.Equal("Test assembly referenced by VamSys.Application", Refusal(Package(false, a => a["VamSys.Application"].References.Add("VamSys.TestSupport")), false));

    [Fact]
    public void A_test_library_in_the_dependency_manifest_is_refused()
    {
        var package = Package(false);
        File.WriteAllText(Path.Combine(package, "FlightOpsDesk.deps.json"), """{"libraries":{"VamSys.Benchmarks/1.0.0":{}}}""");
        Assert.Equal("Test dependency in product dependency manifest", Refusal(package, false));
    }

    [Fact]
    public void Missing_runtime_files_and_the_API_contract_are_refused()
    {
        var package = Package(false);
        File.Delete(Path.Combine(package, "Theme.xbf"));
        Assert.Equal("Missing package file: Theme.xbf", Refusal(package, false));
        Dispose();
        Assert.Equal("Runtime API contract missing", Refusal(Package(false, a => a["VamSys.Infrastructure"].Resources.Clear()), false));
    }

    [Fact]
    public void A_documentation_link_to_a_file_left_out_of_the_package_is_refused()
    {
        var package = Package(false);
        File.WriteAllText(Path.Combine(package, "docs", "USER-GUIDE.md"), "[Plan](REFACTOR-PLAN.md)");
        Assert.Equal("Broken package documentation link in USER-GUIDE.md: REFACTOR-PLAN.md", Refusal(package, false));
    }

    // A metadata-only assembly with the declared types, parameterless static methods (no bodies), user strings,
    // assembly references, embedded resources and, optionally, an InternalsVisibleTo attribute.
    static void Write(Assembly assembly, string path)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(assembly.Name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(assembly.Name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(10, 0, 0, 0), default, default, 0, default);
        foreach (var reference in assembly.References)
            metadata.AddAssemblyReference(metadata.GetOrAddString(reference), new Version(1, 0, 0, 0), default, default, 0, default);
        var objectType = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        foreach (var text in assembly.Strings)
            metadata.GetOrAddUserString(text);

        var voidSignature = new BlobBuilder();
        new BlobEncoder(voidSignature).MethodSignature().Parameters(0, returnType => returnType.Void(), _ => { });
        var signature = metadata.GetOrAddBlob(voidSignature);
        var firstField = MetadataTokens.FieldDefinitionHandle(1);
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, firstField, MetadataTokens.MethodDefinitionHandle(1));
        // Every method belongs to the first declared type (the check reads method names, not their owners).
        var types = assembly.Types.Count > 0 ? assembly.Types : ["Holder"];
        for (var n = 0; n < types.Count; n++)
        {
            metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class, metadata.GetOrAddString("Fake"), metadata.GetOrAddString(types[n]), objectType, firstField,
                MetadataTokens.MethodDefinitionHandle(n == 0 ? 1 : assembly.Methods.Count + 1));
            if (n == 0)
                foreach (var method in assembly.Methods)
                    metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL, metadata.GetOrAddString(method), signature, -1, default);
        }

        if (assembly.Friends)
        {
            var friendType = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System.Runtime.CompilerServices"), metadata.GetOrAddString("InternalsVisibleToAttribute"));
            var constructorSignature = new BlobBuilder();
            new BlobEncoder(constructorSignature).MethodSignature(isInstanceMethod: true).Parameters(1, returnType => returnType.Void(), parameters => parameters.AddParameter().Type().String());
            var constructor = metadata.AddMemberReference(friendType, metadata.GetOrAddString(".ctor"), metadata.GetOrAddBlob(constructorSignature));
            var value = new BlobBuilder();
            new BlobEncoder(value).CustomAttributeSignature(fixedArguments => fixedArguments.AddArgument().Scalar().Constant("VamSys.UnitTests"), _ => { });
            metadata.AddCustomAttribute(EntityHandle.AssemblyDefinition, constructor, metadata.GetOrAddBlob(value));
        }

        var resources = new BlobBuilder();
        foreach (var resource in assembly.Resources)
        {
            metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString(resource), default, (uint)resources.Count);
            resources.WriteInt32(2);
            resources.WriteBytes("{}"u8.ToArray());
        }

        var pe = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder(),
            managedResources: assembly.Resources.Count > 0 ? resources : null);
        var image = new BlobBuilder();
        pe.Serialize(image);
        File.WriteAllBytes(path, image.ToArray());
    }
}
