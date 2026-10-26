using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Domain;

// These inspect the compiled code, so a new empty project cannot satisfy the boundary.
public class ArchitectureBoundaryTests
{
    [Fact]
    public void Domain_owns_business_rules_and_has_no_outward_project_dependency()
    {
        var domain = typeof(Workspace).Assembly;
        Assert.Equal("VamSys.Domain", domain.GetName().Name);
        foreach (var type in new[] { typeof(ChangePlanner), typeof(FieldSpec), typeof(CsvAdapter), typeof(EditHistory), typeof(BatchStep) })
            Assert.Same(domain, type.Assembly);
        Assert.DoesNotContain(domain.GetReferencedAssemblies(), a => a.Name!.StartsWith("VamSys.", StringComparison.Ordinal));
    }

    [Fact]
    public void Effect_ports_and_orchestration_live_outside_domain()
    {
        foreach (var type in new[] { typeof(IResourceReader), typeof(IResourceWriter), typeof(IBatchExecutor),
            typeof(IBatchCheckpointWriter), typeof(BatchExecutor), typeof(DraftEdits), typeof(WindowCloseController) })
            Assert.Equal("VamSys.Application", type.Assembly.GetName().Name);
        Assert.Equal("VamSys.Infrastructure", typeof(AppPaths).Assembly.GetName().Name);
        Assert.DoesNotContain(typeof(BatchExecutor).Assembly.GetReferencedAssemblies(), a =>
            a.Name is "VamSys.Infrastructure" or "VamSys.Api" or "VamSys.Storage" or "FlightOpsDesk" or "Microsoft.Data.Sqlite");
    }

    [Fact]
    public void Application_references_no_winui_sqlite_or_httpclient()
    {
        var application = typeof(BatchExecutor).Assembly;
        Assert.DoesNotContain(application.GetReferencedAssemblies(), a => a.Name!.StartsWith("Microsoft.UI", StringComparison.Ordinal)
            || a.Name.StartsWith("Microsoft.WinUI", StringComparison.Ordinal) || a.Name.StartsWith("Microsoft.Windows", StringComparison.Ordinal)
            || a.Name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) || a.Name.StartsWith("SQLitePCL", StringComparison.Ordinal));
        using var stream = File.OpenRead(application.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var types = metadata.TypeReferences.Select(h => metadata.GetTypeReference(h))
            .Select(t => metadata.GetString(t.Namespace) + "." + metadata.GetString(t.Name)).ToList();
        Assert.DoesNotContain(types, t => t is "System.Net.Http.HttpClient" or "System.Net.Http.HttpMessageInvoker" || t.StartsWith("Microsoft.UI.", StringComparison.Ordinal) || t.StartsWith("Microsoft.Data.Sqlite.", StringComparison.Ordinal));
    }

    [Fact]
    public void Storage_owns_sqlite_and_dpapi_and_references_only_domain_and_application()
    {
        var storage = typeof(WorkspaceStore).Assembly;
        Assert.Equal("VamSys.Storage", storage.GetName().Name);
        foreach (var type in new[] { typeof(WorkspaceStore), typeof(DataDirectoryLease), typeof(PerformanceRun), typeof(OperationsCredentials), typeof(WorkspaceMaintenanceResult) })
            Assert.Same(storage, type.Assembly);
        // Layering: Storage sits above Domain/Application and below Infrastructure and the app.
        Assert.DoesNotContain(storage.GetReferencedAssemblies(), a => a.Name is "VamSys.Infrastructure" or "VamSys.Api" or "FlightOpsDesk");
        Assert.Contains(storage.GetReferencedAssemblies(), a => a.Name == "Microsoft.Data.Sqlite");
        // Nothing but Storage opens the database or touches DPAPI, and only Infrastructure builds on Storage.
        foreach (var other in new[] { typeof(Workspace).Assembly, typeof(BatchExecutor).Assembly, typeof(OperationsAdapter).Assembly })
            Assert.DoesNotContain(other.GetReferencedAssemblies(), a => a.Name is "Microsoft.Data.Sqlite" or "Microsoft.Data.Sqlite.Core" or "System.Security.Cryptography.ProtectedData");
        foreach (var lower in new[] { typeof(Workspace).Assembly, typeof(BatchExecutor).Assembly })
            Assert.DoesNotContain(lower.GetReferencedAssemblies(), a => a.Name == "VamSys.Storage");
        Assert.Contains(typeof(OperationsAdapter).Assembly.GetReferencedAssemblies(), a => a.Name == "VamSys.Storage");
    }

    [Fact]
    public void Domain_cannot_call_host_file_network_environment_or_log_apis()
    {
        using var stream = File.OpenRead(typeof(Workspace).Assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.IO.File", "System.IO.Directory", "System.IO.FileStream", "System.IO.FileInfo", "System.IO.DirectoryInfo",
            "System.Console", "System.Diagnostics.Process", "System.Diagnostics.Trace",
            "System.Net.Http.HttpClient", "System.Net.Http.HttpMessageInvoker", "System.Net.Sockets.Socket",
            "System.Security.Cryptography.ProtectedData"
        };
        var types = metadata.TypeReferences.Select(handle => metadata.GetTypeReference(handle))
            .Select(type => metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name));
        Assert.DoesNotContain(types, forbidden.Contains);
        // C# iterator state machines use CurrentManagedThreadId to reuse their enumerator.
        // That generated call is allowed; environment variables, paths and process control are not.
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
                continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (metadata.GetString(type.Namespace) == "System" && metadata.GetString(type.Name) == "Environment")
                Assert.Equal("get_CurrentManagedThreadId", metadata.GetString(member.Name));
        }
    }

    [Fact]
    public void Compiled_messages_keep_both_cultures_and_legacy_persisted_text()
    {
        var language = new LocalizationService();
        Assert.Equal("待执行", language.Get("Text_637DA56123"));
        language.SetLanguage("en-US");
        Assert.NotEqual("待执行", language.Get("Text_637DA56123"));
        Assert.DoesNotContain("[Text_", language.Get("Text_637DA56123"), StringComparison.Ordinal);
        var item = new ChangeItem();
        Assert.Equal("待执行", item.Message);
        Assert.Equal("待执行", Messages.FromLegacy("待执行").ToString());
        string? missing = null;
        language.MissingResource += key => missing = key;
        Assert.Equal("[R2MissingKey]", language.Get("R2MissingKey"));
        Assert.Equal("R2MissingKey", missing);
    }
}
