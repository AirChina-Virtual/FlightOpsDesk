using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;

namespace VamSys.IntegrationTests.Golden.Csv;

/// <summary>
/// I06: CSV export bytes (create / update / delete changes of every resource, byte for byte) and the draft rows produced by
/// importing CSV. Expected files live in Golden/Csv/Expected/{export,import,errors}.
/// </summary>
public class CsvGoldenTests
{
    const string Extra = "Extra 备注";
    const string CarriageReturn = "CR only";

    // ---- data ---------------------------------------------------------------------------------------------------------

    /// <summary>Field order of rows that came from the API (_delete first), then the schema columns, then an unknown column.</summary>
    static string[] FieldsOf(ResourceKind kind) => ["_delete", ..Schemas.All[kind].Columns.Where(c => c != "_delete").Take(kind == ResourceKind.Routes ? 9 : 6), Extra, CarriageReturn];

    static string Value(ResourceKind kind, string field, string variant)
    {
        if (field == "_delete") return variant == "delete" ? "TRUE" : "FALSE";
        if (field == CarriageReturn) return variant == "special" ? "line\rbreak" : "";
        if (field == Extra) return variant == "special" ? "红白,\"双\"线\r\n第二行" : variant == "update-new" ? "更新后" : "";
        var key = Schemas.All[kind].Key;
        if (field == key) return variant.StartsWith("create") && key == "ID" ? "" : key == "ICAO/IATA" ? variant switch { "update-base" or "update-new" => "EGLL", "delete" => "EGKK", "create" => "EDDB", _ => "ZBAA" } : variant switch { "update-base" or "update-new" => "11", "delete" => "12", _ => "" };
        var plain = $"{field} {variant}";
        if (variant == "special" && field is "Name" or "Remarks" or "Route String" or "Internal Remarks")
            return "He said \"go\", then\nstop 中文,逗号";
        if (variant == "update-new" && field is "Name" or "Remarks" or "Route String" or "Tags" or "Category") return plain + " changed";
        return plain;
    }

    static DataRow Row(ResourceKind kind, string variant, bool identity)
    {
        var row = new DataRow { Fields = new(StringComparer.Ordinal) };
        foreach (var f in FieldsOf(kind)) row.Fields[f] = Value(kind, f, variant);
        if (identity) { row.Identity = new(variant.StartsWith("update") ? "11" : "12", "7"); row.RawApiJson = "{\"id\":" + row.Identity.RemoteId + "}"; }
        return row;
    }

    /// <summary>Update + delete (existing rows), then two creates, the second with quotes, commas, newlines and Chinese text.</summary>
    static List<ChangeItem> Changes(ResourceKind kind)
    {
        var update = Row(kind, "update-base", true); var delete = Row(kind, "delete", true);
        delete.Fields["_delete"] = "FALSE";
        var data = new ResourceData { Snapshot = [update.Copy(), delete.Copy()] };
        var updated = Row(kind, "update-new", true); updated.LocalId = update.LocalId;
        var deleted = delete.Copy(); deleted.Fields["_delete"] = "TRUE";
        data.Draft = [updated, deleted, Row(kind, "create", false), Row(kind, "special", false)];
        var items = new ChangePlanner().Plan(kind, data);
        Xunit.Assert.Equal([ChangeKind.Update, ChangeKind.Delete, ChangeKind.Create, ChangeKind.Create], items.Select(i => i.Kind));
        return items;
    }

    static void Parts(GoldenSession golden, string name, IReadOnlyList<byte[]> parts)
    {
        for (var i = 0; i < parts.Count; i++) golden.CheckBytes($"{name}.part{i + 1}.csv", parts[i]);
        golden.CheckText($"{name}.parts.txt", $"parts: {parts.Count}\nbytes: {string.Join(", ", parts.Select(p => p.Length))}\n");
    }

    // ---- export -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Export_bytes_are_the_same_for_every_resource()
    {
        var golden = new GoldenSession("Csv", "export");
        var csv = new CsvAdapter();
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var name = kind.ToString().ToLowerInvariant();

            // All four changes share one column set: one file.
            var items = Changes(kind);
            var whole = csv.Export(kind, items);
            Xunit.Assert.Single(whole);
            Parts(golden, $"{name}.mixed", whole);

            // A change without the unknown column forms its own column set and is exported separately.
            var differing = Changes(kind);
            differing[2].After.Fields.Remove(Extra);
            Parts(golden, $"{name}.column-sets", csv.Export(kind, differing));

            // maxBytes exactly fits the header and the first two rows, and one byte less.
            var firstTwo = HeaderLength(whole[0]) + RowLength(csv, kind, items[0]) + RowLength(csv, kind, items[1]);
            Parts(golden, $"{name}.max-bytes-exact-fit", csv.Export(kind, items, firstTwo));
            Parts(golden, $"{name}.max-bytes-one-less", csv.Export(kind, items, firstTwo - 1));
        }
        golden.Finish();
    }

    static int HeaderLength(byte[] part) => Encoding.UTF8.GetString(part).IndexOf("\r\n", StringComparison.Ordinal) + 2;
    static int RowLength(CsvAdapter csv, ResourceKind kind, ChangeItem item) { var single = csv.Export(kind, [item])[0]; return single.Length - HeaderLength(single); }

    // ---- import -------------------------------------------------------------------------------------------------------

    static string Quote(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    /// <summary>
    /// A hand-built file: BOM, CRLF then LF then CR line ends, a blank line, quoted fields with commas, quotes and
    /// newlines, Chinese text, an unknown column, one renamed header and no final line end.
    /// </summary>
    static (string Text, Dictionary<string, string> Mapping) ImportFile(ResourceKind kind)
    {
        var schema = Schemas.All[kind];
        var columns = new[] { schema.Key }.Concat(schema.Required.Where(r => r != schema.Key)).Append(Extra).Append(CarriageReturn).ToArray();
        var renamed = columns[1];
        var headers = columns.Select(c => c == renamed ? renamed + " (csv)" : c).ToArray();
        string[] Cells(string variant) => columns.Select(c => c == schema.Key ? (kind == ResourceKind.Airports ? variant switch { "update-new" => "EGLL", "create" => "EGKK", _ => "ZBAA" } : variant switch { "update-new" => "11", "create" => "12", _ => "13" }) : Value(kind, c, variant)).ToArray();
        var sb = new StringBuilder("﻿");
        sb.Append(string.Join(',', headers.Select(Quote))).Append("\r\n");
        sb.Append(string.Join(',', Cells("update-new").Select(Quote))).Append('\n');
        sb.Append('\n');
        sb.Append(string.Join(',', Cells("create").Select(Quote))).Append('\r');
        sb.Append(string.Join(',', Cells("special").Select(Quote)));
        return (sb.ToString(), new() { [headers[1]] = renamed });
    }

    static ResourceData Existing(ResourceKind kind)
    {
        var row = Row(kind, "update-base", true);
        row.Fields["Local only"] = "kept";
        var data = new ResourceData { Snapshot = [row.Copy()], Draft = [row] };
        data.Columns = [..row.Fields.Keys];
        return data;
    }

    [Fact]
    public void Imported_rows_become_the_same_drafts_for_every_resource()
    {
        var golden = new GoldenSession("Csv", "import");
        var masker = new Masker();
        var csv = new CsvAdapter();
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var (text, mapping) = ImportFile(kind);
            var table = csv.Parse(text);

            var baseline = new ResourceData();
            CsvAdapter.Import(kind, baseline, csv.Map(table, mapping), baseline: true);

            var merged = Existing(kind);
            CsvAdapter.Import(kind, merged, csv.Map(table, mapping), baseline: false);

            var result = new JsonObject
            {
                ["headers"] = JsonSerializer.SerializeToNode(table.Headers),
                ["rows"] = JsonSerializer.SerializeToNode(table.Rows),
                ["baseline"] = JsonNode.Parse(JsonSerializer.Serialize(baseline)),
                ["merge-into-existing-draft"] = JsonNode.Parse(JsonSerializer.Serialize(merged)),
            };
            golden.CheckText($"{kind.ToString().ToLowerInvariant()}.json", masker.MaskJson(result.ToJsonString()));
        }
        golden.Finish();
    }

    // ---- failures -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Rejected_input_gives_the_same_errors()
    {
        var golden = new GoldenSession("Csv", "errors");
        var csv = new CsvAdapter();
        var results = new JsonArray();
        void Case(string name, Action action)
        {
            var entry = new JsonObject { ["case"] = name };
            try { action(); entry["error"] = null; }
            catch (Exception e)
            {
                var message = MessageErrors.Describe(e);
                entry["error"] = new JsonObject { ["type"] = e.GetType().Name, ["code"] = message.Code, ["text"] = message.ToString() };
            }
            results.Add(entry);
        }
        ResourceData Drafts(params string[] ids) => new() { Draft = ids.Select(id => new DataRow { Fields = new() { ["ID"] = id, ["Name"] = "n" } }).ToList() };
        List<DataRow> Rows(params string[] ids) => ids.Select(id => new DataRow { Fields = new() { ["ID"] = id, ["Name"] = "n" } }).ToList();

        Case("parse-empty", () => csv.Parse(""));
        Case("parse-ragged-row", () => csv.Parse("a,b\r\n1\r\n"));
        Case("parse-unterminated-quote", () => csv.Parse("a,b\r\n\"1,2\r\n"));
        Case("parse-text-after-closing-quote", () => csv.Parse("a,b\r\n\"1\"x,2\r\n"));
        Case("parse-quote-inside-unquoted-field", () => csv.Parse("a,b\r\n1\"x,2\r\n"));
        Case("parse-duplicate-headers", () => csv.Parse("a,a\r\n1,2\r\n"));
        Case("parse-blank-header", () => csv.Parse("a,\r\n1,2\r\n"));
        Case("map-two-headers-to-one-name", () => csv.Map(csv.Parse("a,b\r\n1,2\r\n"), new Dictionary<string, string> { ["a"] = "b" }));
        Case("import-baseline-without-key", () => CsvAdapter.Import(ResourceKind.Fleets, new ResourceData(), Rows("1", ""), baseline: true));
        Case("import-duplicate-incoming-keys", () => CsvAdapter.Import(ResourceKind.Fleets, new ResourceData(), Rows("1", "1"), baseline: false));
        Case("import-duplicate-draft-keys", () => CsvAdapter.Import(ResourceKind.Fleets, Drafts("1", "1"), Rows("2"), baseline: false));
        Case("export-local-reference", () =>
        {
            var item = Changes(ResourceKind.Aircraft)[2];
            item.After.Fields["Fleet ID"] = "local:" + Guid.Empty;
            csv.Export(ResourceKind.Aircraft, [item]);
        });
        Case("export-row-larger-than-max-bytes", () => csv.Export(ResourceKind.Fleets, Changes(ResourceKind.Fleets), 10));
        Case("export-nothing", () => Xunit.Assert.Empty(csv.Export(ResourceKind.Fleets, [])));

        golden.CheckText("errors.json", new Masker().MaskJson(results.ToJsonString()));
        golden.Finish();
    }
}
