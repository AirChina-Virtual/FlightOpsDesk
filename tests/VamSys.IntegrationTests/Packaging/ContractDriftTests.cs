using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace VamSys.IntegrationTests.Packaging;

// 4.3: the contract drift check (tools/VamSys.QaSeed --check-contract). Each case changes one thing in a small document,
// or in a copy of the pinned Operations contract, and expects exactly that in the report.
public sealed class ContractDriftTests : IDisposable
{
    readonly string repository = Path.Combine(Path.GetTempPath(), "vamsys-contract-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(repository))
            Directory.Delete(repository, recursive: true);
    }

    const string Small = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Ops", "version": "3.0.0" },
          "paths": {
            "/airports": {
              "get": { "summary": "List", "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Page" } } } } } },
              "post": { "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Airport" } } } }, "responses": { "201": { "description": "made" } } }
            }
          },
          "components": { "schemas": {
            "Page": { "type": "object", "properties": { "data": { "type": "array", "items": { "$ref": "#/components/schemas/Airport" } } } },
            "Airport": { "type": "object", "description": "An airport", "properties": { "icao": { "type": "string", "maxLength": 4 }, "title": { "type": "string" } } }
          } }
        }
        """;

    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
    static JsonObject Parse() => JsonNode.Parse(Small)!.AsObject();

    [Fact]
    public void SameBytesAreUnchanged()
    {
        var report = ContractDrift.Compare("operations", Bytes(Small), Bytes(Small));
        Assert.False(report.HashChanged);
        Assert.Equal(["operations: unchanged (SHA-256 " + ContractDrift.Sha256(Bytes(Small)) + ")"], ContractDrift.Format(report));
    }

    [Fact]
    public void WordingAndKeyOrderAloneAreNotAStructureChange()
    {
        var changed = Parse();
        changed["paths"]!["/airports"]!["get"]!["summary"] = "List all airports";
        changed["components"]!["schemas"]!["Airport"]!["description"] = "An airport of the airline";
        var reordered = new JsonObject(changed.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        var report = ContractDrift.Compare("operations", Bytes(Small), Bytes(reordered));
        Assert.True(report.HashChanged);
        Assert.False(report.StructureChanged, string.Join(" | ", ContractDrift.Format(report)));
        Assert.Contains(ContractDrift.Format(report), line => line.Contains("Only descriptions", StringComparison.Ordinal));
    }

    [Fact]
    public void AChangedSchemaNamesTheOperationsThatReachIt()
    {
        var changed = Parse();
        changed["components"]!["schemas"]!["Airport"]!["properties"]!["icao"]!["maxLength"] = 5;
        var report = ContractDrift.Compare("operations", Bytes(Small), Bytes(changed));
        Assert.Equal(["components/schemas/Airport"], report.ChangedComponents);
        Assert.Empty(report.ChangedOperations);
        // POST names Airport directly; GET reaches it through Page.
        Assert.Equal(["GET /airports", "POST /airports"], report.AffectedOperations.Keys);
        Assert.Equal(["components/schemas/Airport"], report.AffectedOperations["GET /airports"]);
    }

    [Fact]
    public void AFieldNamedTitleIsAField()
    {
        var changed = Parse();
        changed["components"]!["schemas"]!["Airport"]!["properties"]!.AsObject().Remove("title");
        var report = ContractDrift.Compare("operations", Bytes(Small), Bytes(changed));
        Assert.Equal(["components/schemas/Airport"], report.ChangedComponents);
    }

    [Fact]
    public void OperationsAndComponentsAddedRemovedAndChanged()
    {
        var changed = Parse();
        var airports = changed["paths"]!["/airports"]!.AsObject();
        airports.Remove("post");
        airports["get"]!["parameters"] = new JsonArray(new JsonObject { ["name"] = "page[size]", ["in"] = "query" });
        changed["paths"]!["/fleets"] = new JsonObject { ["get"] = new JsonObject { ["responses"] = new JsonObject() } };
        changed["components"]!["schemas"]!["Fleet"] = new JsonObject { ["type"] = "object" };
        changed["info"]!["version"] = "3.1.0";
        var report = ContractDrift.Compare("operations", Bytes(Small), Bytes(changed));
        Assert.Equal(["info.version: 3.0.0 → 3.1.0"], report.Header);
        Assert.Equal(["GET /fleets"], report.AddedOperations);
        Assert.Equal(["POST /airports"], report.RemovedOperations);
        Assert.Equal(["parameters"], report.ChangedOperations["GET /airports"]);
        Assert.Equal(["components/schemas/Fleet"], report.AddedComponents);
        var text = ContractDrift.Format(report).ToList();
        Assert.Contains("  Removed operations (1):", text);
        Assert.Contains("    GET /airports: parameters", text);
    }

    [Fact]
    public void ThePinnedOperationsContractComparedWithAnEditedCopy()
    {
        var root = RepositoryRoot();
        var pinned = File.ReadAllBytes(Path.Combine(root, "docs", "contracts", "operations.json"));
        Assert.False(ContractDrift.Compare("operations", pinned, pinned).HashChanged);
        var edited = JsonNode.Parse(pinned)!.AsObject();
        var schemas = edited["components"]!["schemas"]!.AsObject();
        var name = schemas.Select(p => p.Key).First(k => schemas[k]?["properties"] is JsonObject);
        schemas[name]!["properties"]!.AsObject()["addedForTest"] = new JsonObject { ["type"] = "string" };
        var report = ContractDrift.Compare("operations", pinned, Bytes(edited));
        Assert.Equal(["components/schemas/" + name], report.ChangedComponents);
        Assert.Empty(report.AddedOperations);
        Assert.Empty(report.RemovedOperations);
        Assert.Empty(report.ChangedOperations);
    }

    // The command against a fake source: unchanged → 0 and nothing saved; changed → 1 and the fetched copy saved.
    [Fact]
    public async Task TheCommandFetchesComparesAndSavesOnlyAChangedDocument()
    {
        var contracts = Directory.CreateDirectory(Path.Combine(repository, "docs", "contracts")).FullName;
        await File.WriteAllTextAsync(Path.Combine(contracts, "operations.json"), Small, Ct);
        await File.WriteAllTextAsync(Path.Combine(contracts, "manifest.json"), $$"""
            { "contracts": [ { "name": "operations", "source": "https://vamsys.io/api-docs/operations.json", "sha256": "{{ContractDrift.Sha256(Bytes(Small))}}" } ] }
            """, Ct);
        var served = Small;
        using var handler = new Source(() => served);

        using var output = new StringWriter();
        Assert.Equal(0, await ContractDrift.RunAsync(repository, handler, output));
        Assert.Contains("operations: unchanged", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(repository, "artifacts")));
        Assert.Equal(["https://vamsys.io/api-docs/operations.json"], handler.Requested);

        var changed = Parse();
        changed["info"]!["version"] = "3.0.1";
        served = changed.ToJsonString();
        using var second = new StringWriter();
        Assert.Equal(1, await ContractDrift.RunAsync(repository, handler, second));
        Assert.Contains("info.version: 3.0.0 → 3.0.1", second.ToString(), StringComparison.Ordinal);
        var saved = Assert.Single(Directory.GetFiles(Path.Combine(repository, "artifacts", "contract-drift")));
        Assert.Equal(served, await File.ReadAllTextAsync(saved, Ct));
        // The pinned copy is never replaced.
        Assert.Equal(Small, await File.ReadAllTextAsync(Path.Combine(contracts, "operations.json"), Ct));
    }

    [Fact]
    public async Task APinnedCopyThatNoLongerMatchesTheManifestStopsTheCheck()
    {
        var contracts = Directory.CreateDirectory(Path.Combine(repository, "docs", "contracts")).FullName;
        await File.WriteAllTextAsync(Path.Combine(contracts, "operations.json"), Small, Ct);
        await File.WriteAllTextAsync(Path.Combine(contracts, "manifest.json"), """
            { "contracts": [ { "name": "operations", "source": "https://vamsys.io/api-docs/operations.json", "sha256": "00" } ] }
            """, Ct);
        using var handler = new Source(() => Small);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ContractDrift.RunAsync(repository, handler, TextWriter.Null));
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task ASourceOutsideVamsysIsRefused()
    {
        var contracts = Directory.CreateDirectory(Path.Combine(repository, "docs", "contracts")).FullName;
        await File.WriteAllTextAsync(Path.Combine(contracts, "operations.json"), Small, Ct);
        await File.WriteAllTextAsync(Path.Combine(contracts, "manifest.json"), """
            { "contracts": [ { "name": "operations", "source": "http://example.com/operations.json", "sha256": "00" } ] }
            """, Ct);
        using var handler = new Source(() => Small);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ContractDrift.RunAsync(repository, handler, TextWriter.Null));
        Assert.Empty(handler.Requested);
    }

    // The manifest names the pinned documents with their real SHA-256.
    [Fact]
    public void TheManifestMatchesThePinnedDocuments()
    {
        var contracts = Path.Combine(RepositoryRoot(), "docs", "contracts");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(contracts, "manifest.json")))!;
        foreach (var entry in manifest["contracts"]!.AsArray())
            Assert.Equal((string)entry!["sha256"]!, ContractDrift.Sha256(File.ReadAllBytes(Path.Combine(contracts, (string)entry["name"]! + ".json"))));
    }

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "contracts", "manifest.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    sealed class Source(Func<string> body) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(), Encoding.UTF8, "application/json") });
        }
    }
}
