using VamSys.Core;
using VamSys.Infrastructure;
using static VamSys.IntegrationTests.Golden.Storage.WorkspaceScenario;

namespace VamSys.IntegrationTests.Golden.Storage;

/// <summary>
/// I08: one fixed operation sequence on a v4 database. After every step the whole database is dumped and compared
/// with Expected/sequence/NN-name.json, so any change to schema, row JSON, ordering or write scope shows up as a diff.
/// </summary>
public class StorageSequenceGoldenTests
{
    [Fact]
    public async Task Operation_sequence_leaves_the_same_database_content_after_every_step()
    {
        var golden = new GoldenSession("Storage", "sequence");
        var masker = new Masker();
        var dump = new SqliteDump(masker);
        using var s = new WorkspaceScenario();
        var step = 0;
        void Snap(string name) => golden.CheckText($"{++step:00}-{name}.json", dump.Dump(s.DatabasePath, maskBlobs: true));

        Snap("empty-store");

        s.CreateWorkspace();
        Snap("create-workspace");

        await s.Refresh(ReadServer([Heathrow, Gatwick, Beijing], [FleetAlpha, FleetBeta], [AlphaOne, AlphaTwo], [Routing], [RouteOut, RouteBack]));
        Snap("initial-snapshot-load");

        // Cell edits saved row by row: a fleet name (new undo step) and an airport name (second resource in the same save).
        var fleet = s.Row(ResourceKind.Fleets, "4");
        var airport = s.Row(ResourceKind.Airports, "103");
        s.EditCell(ResourceKind.Fleets, fleet, "Name", "Fleet Alpha 2");
        s.EditCell(ResourceKind.Airports, airport, "Name", "Beijing Capital");
        await s.SaveRows();
        Snap("cell-edit-row-save");

        // Same cell again amends the open undo step instead of adding one.
        s.EditCell(ResourceKind.Fleets, fleet, "Name", "Fleet Alpha 3", newStep: false);
        await s.SaveRows();
        Snap("cell-edit-continuation-row-save");

        await s.Move(ResourceKind.Fleets, undo: true);
        Snap("undo");
        await s.Move(ResourceKind.Fleets, undo: false);
        Snap("redo");

        // Add row (structural, full save) followed by cell fills (row-level saves).
        var added = await s.AddRow(ResourceKind.Fleets);
        Snap("add-row-full-save");
        s.EditCell(ResourceKind.Fleets, added, "Name", "New Fleet Gamma");
        s.EditCell(ResourceKind.Fleets, added, "Type Code", "A320");
        s.EditCell(ResourceKind.Fleets, added, "Type (pax/cargo/...)", "pax");
        s.EditCell(ResourceKind.Fleets, added, "Max Passengers", "150");
        await s.SaveRows();
        Snap("new-row-filled");

        var back = s.Row(ResourceKind.Routes, "42");
        await s.MarkDeleted(ResourceKind.Routes, back);
        Snap("delete-mark");
        await s.Restore(ResourceKind.Routes, back);
        Snap("restore");

        await s.BatchEdit(ResourceKind.Routes, new EditRule(RuleKind.AddTags, "Tags", "golden,批量"), s.Data(ResourceKind.Routes).Draft);
        Snap("batch-edit");

        const string csv = "ID,Fleet Name,Type Code,Type (pax/cargo/...),Max Passengers,Max Freight,Livery\r\n"
            + "5,Cargo Beta CSV,B77F,cargo,,100000,\"白色,\"\"蓝\"\"条\"\r\n"
            + ",CSV 新机队,E190,pax,100,,红白\r\n";
        await s.ImportCsv(ResourceKind.Fleets, csv, new Dictionary<string, string> { ["Fleet Name"] = "Name" }, baseline: false);
        Snap("csv-import-into-drafts");

        // Remote changed under us: fleet 4 and fleet 5 names (both also edited locally -> conflicts), route 41 remarks
        // (clean merge), airport 103 removed (edited locally -> row conflict), airport 104 and aircraft 23 added.
        var aircraft23 = """{"id":23,"airline_id":7,"fleet_id":4,"name":"Alpha Three","registration":"B-1003"}""";
        await s.Refresh(ReadServer([Heathrow, Gatwick, Berlin],
            ["""{"id":4,"airline_id":7,"name":"Fleet Alpha Remote","code":"B738","type":"pax","max_pax":180}""",
             """{"id":5,"airline_id":7,"name":"Cargo Beta Remote","code":"B77F","type":"cargo","max_cargo":100000}"""],
            [AlphaOne, aircraft23], [Routing],
            ["""{"id":41,"type":"scheduled","departure_id":101,"arrival_id":102,"callsign":"ACA123","flight_number":"AC123","fleet_ids":[4],"remarks":"remote note"}""", RouteBack]));
        Xunit.Assert.NotEmpty(s.Data(ResourceKind.Fleets).Conflicts);
        Xunit.Assert.Contains(s.Data(ResourceKind.Airports).Conflicts, c => c.Field == "*");
        Snap("snapshot-refresh-with-conflicts");

        // Fleet 4 and the removed airport take the remote side, fleet 5 keeps the local CSV name.
        await s.ResolveConflicts((kind, conflict) => conflict.Field == "*" || s.Data(kind).FindDraftRow(conflict.RowId)?.Identity?.RemoteId == "4");
        Xunit.Assert.All(Enum.GetValues<ResourceKind>(), k => Xunit.Assert.Empty(s.Data(k).Conflicts));
        Snap("resolve-conflicts");

        var job = await s.SubmitFleetCreates();
        Snap("submit-online-job-pending");

        var checkpoints = 0;
        var server = await s.Execute(job, kind => Snap($"checkpoint-{++checkpoints:00}-{kind}"));
        Xunit.Assert.True(job.Items.All(i => i.State == ItemState.Succeeded),
            string.Join("\n", job.Items.Select(i => $"{i.State} {i.Message} {i.Diagnostic}")) + "\n" + string.Join("\n", server.Requests.Select(r => $"{r.Method} {r.PathAndQuery} {r.Body}")));
        Xunit.Assert.Contains(server.Requests, r => r.Method == "POST");
        Snap("job-succeeded");

        s.Store.SaveConnectionSettings(s.W, "golden-client", "golden-secret", "https://operations.example.test", true);
        Xunit.Assert.Equal("""{"ClientId":"golden-client","Secret":"golden-secret"}""", s.Store.LoadSecret(s.W.Id));
        Xunit.Assert.Equal(RunMode.Offline, s.W.Mode);
        Snap("save-connection-settings");

        s.Store.SaveLanguage("en-US");
        Snap("language-setting");
        s.Store.SaveTableDensity("compact");
        Snap("density-setting");

        var preview = s.Store.PreviewMaintenance(s.W.Id);
        Xunit.Assert.True(preview.CanClear);
        var result = await s.Store.BackupAndClearAsync(s.W.Id, s.W.StorageRevision, TestContext.Current.CancellationToken);
        Snap("backup-and-clear");

        // The backup is a full copy of the database as it was before clearing; the manifest describes it.
        Xunit.Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(result.Backup.DatabasePath))), result.Backup.Sha256);
        golden.CheckText("maintenance-backup-database.json", dump.Dump(result.Backup.DatabasePath, maskBlobs: true));
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(result.Backup.ManifestPath))!.AsObject();
        Xunit.Assert.Equal(result.Backup.Sha256, manifest["databaseSha256"]!.GetValue<string>());
        manifest["databaseSha256"] = "<sha256 of backup file>";
        golden.CheckText("maintenance-backup-manifest.json", masker.MaskJson(manifest.ToJsonString()));

        golden.Finish();
    }
}
