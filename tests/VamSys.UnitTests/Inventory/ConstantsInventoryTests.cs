using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Inventory;

/// <summary>I11 / I15: capability keys, contract hash, API base address, data directory, environment variable, executable name.</summary>
public partial class ConstantsInventoryTests
{
    // "Resource:Operation[:Detail...]" literals, e.g. "Routes:Update:FleetIds:Scheduled".
    [GeneratedRegex(@"^[A-Z][A-Za-z]*(?::[A-Z][A-Za-z0-9]*)+$")]
    private static partial Regex CapabilityKey();

    [GeneratedRegex(@"^:[A-Z][A-Za-z]+$")]
    private static partial Regex CapabilitySuffix();

    [Fact]
    public void Capability_keys_are_locked()
    {
        var lines = new List<string>();
        foreach (var group in SourceScan.AllLiterals(SourceScan.Src).Where(l => CapabilityKey().IsMatch(l.Value) || CapabilitySuffix().IsMatch(l.Value))
                     .GroupBy(l => l.Value))
            lines.Add($"literal:{group.Key} => {string.Join(",", group.Select(l => l.File).Distinct().Order(StringComparer.Ordinal))}");

        // The default key "{Resource}:{Kind}" is built, not written; lock the generated set through the real policy.
        foreach (var resource in Enum.GetValues<ResourceKind>())
            foreach (var kind in Enum.GetValues<ChangeKind>())
            {
                var item = new ChangeItem { Resource = resource, Kind = kind };
                lines.Add($"generated:{ApiOperationPolicy.Key(item)} => ApiOperationPolicy.Key({resource},{kind})");
            }
        Golden.Verify("CapabilityKeys.txt", lines);
    }

    static string FileSha256(string relative) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Golden.RepoRoot, relative))));

    [Fact]
    public void Contract_hashes_are_locked_and_consistent()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Golden.RepoRoot, "docs", "contracts", "manifest.json")));
        var lines = new List<string>();
        foreach (var contract in manifest.RootElement.GetProperty("contracts").EnumerateArray())
        {
            var name = contract.GetProperty("name").GetString()!;
            var sha = contract.GetProperty("sha256").GetString()!;
            lines.Add($"manifest:{name}.sha256 => {sha}");
            lines.Add($"manifest:{name}.version => {contract.GetProperty("version").GetString()}");
            lines.Add($"manifest:{name}.source => {contract.GetProperty("source").GetString()}");
            Assert.Equal(sha, FileSha256($"docs/contracts/{name}.json"), ignoreCase: true);
        }
        lines.Add($"ApiContracts.Sha256 => {ApiContracts.Sha256}");
        lines.Add($"ApiContracts.Version => {ApiContracts.Version}");
        lines.Add($"IndependentApiEvidence.ContractHash => {IndependentApiEvidence.ContractHash}");
        lines.Add($"IndependentApiEvidence.EvidenceId => {IndependentApiEvidence.EvidenceId}");
        Golden.Verify("ContractHashes.txt", lines);
        Assert.Equal(ApiContracts.Sha256, IndependentApiEvidence.ContractHash);
    }

    [Fact]
    public void Endpoint_data_directory_and_executable_identity_are_locked()
    {
        var lines = new List<string> { $"OperationsAdapter.BaseUri => {OperationsAdapter.BaseUri.AbsoluteUri}" };

        // Data directory name and environment variable: every source file that hard-codes them.
        foreach (var token in new[] { "VamSysBatch", "VAMSYS_DATA_DIR" })
            foreach (var group in SourceScan.AllLiterals(SourceScan.Src).Where(l => l.Value == token).GroupBy(l => l.File).OrderBy(g => g.Key, StringComparer.Ordinal))
                lines.Add($"literal:{token}@{group.Key} => {group.Count()} occurrence(s)");

        var project = XDocument.Load(Path.Combine(SourceScan.Src, "VamSys.App", "VamSys.App.csproj"));
        foreach (var property in new[] { "AssemblyName", "RootNamespace", "AssemblyTitle", "Product", "ApplicationIcon", "OutputType" })
            lines.Add($"VamSys.App.csproj:{property} => {project.Descendants(property).Select(e => e.Value).FirstOrDefault() ?? "(missing)"}");

        var icon = Path.Combine(SourceScan.Src, "VamSys.App", "Assets", "FlightOpsDesk.ico");
        lines.Add($"icon:src/VamSys.App/Assets/FlightOpsDesk.ico => {(File.Exists(icon) ? "exists" : "missing")}");
        Golden.Verify("Constants.txt", lines);
    }
}
