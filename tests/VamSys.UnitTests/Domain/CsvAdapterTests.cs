using System.Text;
using VamSys.Core;
using static VamSys.UnitTests.Domain.TestRows;

namespace VamSys.UnitTests.Domain;

/// <summary>
/// F11 (import: pending changes only, header mapping, unknown columns kept, patch semantics) and F12 / I06 (export bytes:
/// only changed records, unknown columns kept, split at the byte limit, CRLF, UTF-8 without BOM).
/// </summary>
public class CsvAdapterTests
{
    static readonly CsvAdapter Csv = new();
    static readonly ChangePlanner Planner = new();
    const ResourceKind Airports = ResourceKind.Airports;

    // ---- parsing ----

    [Fact]
    public void Parse_keeps_quoted_commas_and_escaped_quotes()
    {
        var table = Csv.Parse("Name,Note\r\nB738,\"a,b\"\r\nA320,\"say \"\"hi\"\"\"\r\n");

        Assert.Equal(["Name", "Note"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["B738", "a,b"], table.Rows[0]);
        Assert.Equal(["A320", "say \"hi\""], table.Rows[1]);
    }

    [Fact]
    public void Parse_strips_the_bom_and_keeps_unicode_embedded_newlines_and_leading_zeros()
    {
        var table = Csv.Parse("﻿ID,Name,Extra\r\n001,\"中文,机场\",\"a\"\"b\r\nc\"\r\n");
        var rows = Csv.Map(table, new Dictionary<string, string>());

        Assert.Equal(["ID", "Name", "Extra"], table.Headers);
        Assert.Equal("001", rows[0].Get("ID"));
        Assert.Equal("中文,机场", rows[0].Get("Name"));
        Assert.Equal("a\"b\r\nc", rows[0].Get("Extra"));
    }

    [Theory]
    [InlineData("A,B\r\n1,2\r\n")]
    [InlineData("A,B\n1,2\n")]
    [InlineData("A,B\r1,2\r")]
    [InlineData("A,B\n1,2")]
    [InlineData("A,B\n\n1,2\n\n")]
    public void Parse_accepts_every_line_ending_and_ignores_blank_lines(string text)
    {
        var table = Csv.Parse(text);

        Assert.Equal(["A", "B"], table.Headers);
        Assert.Equal([["1", "2"]], table.Rows);
    }

    [Theory]
    [InlineData("A,A\n1,2", "duplicate header")]
    [InlineData("A,\n1,2", "blank header")]
    [InlineData("A\n\"abc", "unterminated quote")]
    [InlineData("A\n\"a\"b", "text after a closing quote")]
    [InlineData("A\nab\"c", "quote inside an unquoted field")]
    [InlineData("A,B\n1", "row shorter than the header")]
    [InlineData("A,B\n1,2,3", "row longer than the header")]
    [InlineData("", "empty file")]
    public void Parse_rejects_malformed_files_with_a_localizable_message(string text, string why)
    {
        var error = Assert.ThrowsAny<FormatException>(() => Csv.Parse(text));

        Assert.NotNull(MessageErrors.Describe(error).Code);
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void Map_renames_headers_and_rejects_names_that_collide_or_are_blank()
    {
        var table = Csv.Parse("机场,名称\r\nZBAA,北京\r\n");

        var rows = Csv.Map(table, new Dictionary<string, string> { ["机场"] = "ICAO/IATA", ["名称"] = "Name" });

        Assert.Equal("ZBAA", rows.Single().Get("ICAO/IATA"));
        Assert.Equal("北京", rows.Single().Get("Name"));
        Assert.Throws<FormatException>(() => Csv.Map(table, new Dictionary<string, string> { ["机场"] = "X", ["名称"] = "X" }));
        Assert.Throws<FormatException>(() => Csv.Map(table, new Dictionary<string, string> { ["机场"] = " " }));
    }

    // ---- export encoding (I06) ----

    static string ExportOne(string value, ChangeKind kind = ChangeKind.Create)
    {
        var item = new ChangeItem { Resource = Airports, Kind = kind, After = Row(("ICAO/IATA", "ZBAA"), ("Name", "Beijing"), ("Extra", value)) };
        return Text(Csv.Export(Airports, [item]).Single());
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData("001", "001")]
    [InlineData("中文", "中文")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("a\rb", "\"a\rb\"")]
    [InlineData("a\r\nb,\"c\"", "\"a\r\nb,\"\"c\"\"\"")]
    [InlineData(" spaced ", " spaced ")]
    public void Export_quotes_a_cell_only_when_it_has_a_comma_quote_or_line_break(string value, string encoded)
    {
        Assert.Equal($"ICAO/IATA,Name,Extra,_delete\r\nZBAA,Beijing,{encoded},FALSE\r\n", ExportOne(value));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("a,b")]
    [InlineData("say \"hi\"")]
    [InlineData("multi\r\nline, \"quoted\"")]
    [InlineData("中文,机场")]
    [InlineData("0001")]
    public void Export_then_parse_returns_the_same_cell(string value)
    {
        var round = Csv.Parse(ExportOne(value));

        Assert.Equal(value, round.Rows.Single()[round.Headers.IndexOf("Extra")]);
    }

    [Fact]
    public void Export_writes_utf8_without_bom_and_crlf_endings()
    {
        var file = Csv.Export(Airports, [new ChangeItem { Resource = Airports, After = Row(("ICAO/IATA", "ZBAA"), ("Name", "北京")) }]).Single();

        Assert.False(file.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal("ICAO/IATA,Name,_delete\r\nZBAA,北京,FALSE\r\n"u8.ToArray(), file);
    }

    [Theory]
    [InlineData(ChangeKind.Create, "FALSE")]
    [InlineData(ChangeKind.Update, "FALSE")]
    [InlineData(ChangeKind.Delete, "TRUE")]
    public void Export_marks_only_deletions_with_TRUE_in_the_delete_column(ChangeKind kind, string flag)
    {
        Assert.EndsWith("," + flag + "\r\n", ExportOne("x", kind), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_of_no_changes_produces_no_file()
    {
        Assert.Empty(Csv.Export(Airports, []));
    }

    [Fact]
    public void Export_orders_columns_key_first_then_required_then_the_rest_as_seen()
    {
        var item = new ChangeItem { Resource = ResourceKind.Fleets, After = Row(("Zeta", "z"), ("Name", "n"), ("ID", "9"), ("Alpha", "a")) };

        var header = Text(Csv.Export(ResourceKind.Fleets, [item]).Single()).Split("\r\n")[0];

        Assert.Equal("ID,Name,Type Code,Type (pax/cargo/...),Zeta,Alpha,_delete", header);
    }

    // ---- export splitting and column groups ----

    [Fact]
    public void Export_splits_at_the_utf8_byte_limit_and_repeats_the_header_without_cutting_rows()
    {
        var data = new ResourceData { Draft = [.. Enumerable.Range(0, 40).Select(i => Row(("ICAO/IATA", $"X{i:000}"), ("Name", new string('中', 30))))] };

        var files = Csv.Export(Airports, Planner.Plan(Airports, data), 300);

        Assert.True(files.Count > 1);
        Assert.All(files, f => Assert.True(f.Length <= 300));
        var tables = files.Select(f => Csv.Parse(Text(f))).ToList();
        Assert.All(tables, t => Assert.Equal(["ICAO/IATA", "Name", "_delete"], t.Headers));
        Assert.Equal(40, tables.Sum(t => t.Rows.Count));
        Assert.Equal(Enumerable.Range(0, 40).Select(i => $"X{i:000}"), tables.SelectMany(t => t.Rows.Select(r => r[0])));
    }

    [Fact]
    public void Export_splits_by_default_below_ten_mebibytes_per_file()
    {
        const int limit = 10 * 1024 * 1024;
        var data = new ResourceData { Draft = [.. Enumerable.Range(0, 3).Select(i => Row(("ICAO/IATA", $"X{i}"), ("Name", new string((char)('a' + i), 4_000_000))))] };

        var files = Csv.Export(Airports, Planner.Plan(Airports, data));

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.True(f.Length <= limit));
        Assert.Equal(3, files.Sum(f => Csv.Parse(Text(f)).Rows.Count));
    }

    [Fact]
    public void Export_fails_when_one_row_alone_exceeds_the_limit()
    {
        var item = new ChangeItem { Resource = Airports, After = Row(("ICAO/IATA", "ZBAA"), ("Name", new string('中', 30))) };

        Assert.Throws<InvalidOperationException>(() => Csv.Export(Airports, [item], 50));
    }

    [Fact]
    public void Export_keeps_unknown_columns_and_never_turns_an_absent_field_into_an_empty_cell()
    {
        var data = new ResourceData { Draft = [Row(("ICAO/IATA", "ZBAA"), ("Name", "A"), ("Unknown", "x")), Row(("ICAO/IATA", "ZSPD"), ("Name", "B"))] };

        var files = Csv.Export(Airports, Planner.Plan(Airports, data));

        Assert.Equal(2, files.Count);
        var withUnknown = Csv.Parse(Text(files.Single(f => Text(f).Contains("Unknown", StringComparison.Ordinal))));
        var without = Csv.Parse(Text(files.Single(f => !Text(f).Contains("Unknown", StringComparison.Ordinal))));
        Assert.Equal("x", withUnknown.Rows.Single()[withUnknown.Headers.IndexOf("Unknown")]);
        Assert.DoesNotContain("Unknown", without.Headers);
    }

    [Theory]
    [InlineData("local:6a3f2a54-7e3e-4d9a-a3b1-0c9d4a2e5f10")]
    [InlineData("LOCAL:6a3f2a54-7e3e-4d9a-a3b1-0c9d4a2e5f10")]
    [InlineData("1,Local:6a3f2a54-7e3e-4d9a-a3b1-0c9d4a2e5f10")]
    public void Export_refuses_a_reference_to_a_row_that_has_no_remote_id_yet(string fleetId)
    {
        var item = new ChangeItem { Resource = ResourceKind.Aircraft, After = Row(("Name", "Plane"), ("Fleet ID", fleetId)) };

        Assert.Throws<InvalidOperationException>(() => Csv.Export(ResourceKind.Aircraft, [item]));
    }

    [Fact]
    public void Export_only_looks_for_local_references_in_reference_columns()
    {
        var item = new ChangeItem { Resource = ResourceKind.Aircraft, After = Row(("Name", "Plane"), ("Fleet ID", "1"), ("Internal Remarks", "local: some note")) };

        var file = Csv.Export(ResourceKind.Aircraft, [item]).Single();

        Assert.Contains("local: some note", Text(file), StringComparison.Ordinal);
    }

    // ---- import ----

    [Fact]
    public void Baseline_import_makes_snapshot_and_draft_equal_and_clears_history()
    {
        var data = new ResourceData();
        data.Edit([], () => { });
        var rows = new List<DataRow> { Row(("ICAO/IATA", "ZBAA"), ("Name", "A")) };

        CsvAdapter.Import(Airports, data, rows, true);

        Assert.Equal(Snapshot(data.Snapshot), Snapshot(data.Draft));
        Assert.Empty(data.Undo);
        Assert.Empty(data.Redo);
        Assert.NotNull(data.SnapshotAt);
        Assert.Contains("Name", data.Columns);
        Assert.Empty(Planner.Plan(Airports, data));
    }

    [Fact]
    public void Baseline_import_rejects_rows_without_a_key_and_changes_nothing()
    {
        var data = Baseline(Airports, [("ICAO/IATA", "ZBAA")]);

        Assert.Throws<FormatException>(() => CsvAdapter.Import(ResourceKind.Fleets, data, [Row(("Name", "new"))], true));
        Assert.Throws<FormatException>(() => CsvAdapter.Import(Airports, data, [Row(("Name", "no key"))], true));
        Assert.Equal("ZBAA", data.Draft.Single().Get("ICAO/IATA"));
    }

    [Fact]
    public void Import_writes_the_local_reference_prefix_in_lowercase_whatever_the_file_used()
    {
        var id = Guid.NewGuid();
        var data = new ResourceData();

        CsvAdapter.Import(ResourceKind.Aircraft, data, [Row(("ID", "1"), ("Fleet ID", $"LOCAL:{id}"), ("Internal Remarks", "LOCAL: keep"))], true);

        Assert.Equal($"local:{id}", data.Draft.Single().Get("Fleet ID"));
        Assert.Equal("LOCAL: keep", data.Draft.Single().Get("Internal Remarks"));
    }

    [Fact]
    public void Patch_import_keeps_unlisted_rows_and_columns_and_an_explicit_empty_value_clears()
    {
        var data = new ResourceData();
        CsvAdapter.Import(Airports, data, [Row(("ICAO/IATA", "ZBAA"), ("Name", "old"), ("Unknown", "retain")), Row(("ICAO/IATA", "ZSPD"), ("Name", "second"))], true);

        CsvAdapter.Import(Airports, data, [Row(("ICAO/IATA", "ZBAA"), ("Name", ""))], false);

        Assert.Equal(2, data.Draft.Count);
        Assert.Equal("retain", data.Draft[0].Get("Unknown"));
        Assert.Equal("second", data.Draft[1].Get("Name"));
        var change = Planner.Plan(Airports, data).Single();
        Assert.Equal(FieldIntent.Clear, change.Fields["Name"].Intent);
        Assert.DoesNotContain("Unknown", change.Fields.Keys);
    }

    [Fact]
    public void Patch_import_is_one_undo_step_that_restores_the_previous_cells()
    {
        var data = Baseline(Airports, [("ICAO/IATA", "ZBAA"), ("Name", "old")]);

        CsvAdapter.Import(Airports, data, [Row(("ICAO/IATA", "ZBAA"), ("Name", ""))], false);
        Assert.Single(data.Undo);

        data.UndoEdit();
        Assert.Equal("old", data.Draft[0].Get("Name"));
        data.RedoEdit();
        Assert.Equal("", data.Draft[0].Get("Name"));
    }

    [Fact]
    public void Patch_import_appends_unknown_keys_as_new_rows_and_undo_removes_them_again()
    {
        var data = Baseline(Airports, [("ICAO/IATA", "ZBAA"), ("Name", "A")]);

        CsvAdapter.Import(Airports, data, [Row(("ICAO/IATA", "ZSPD"), ("Name", "B"))], false);

        Assert.Equal(["ZBAA", "ZSPD"], data.Draft.Select(r => r.Get("ICAO/IATA")));
        Assert.Equal(ChangeKind.Create, Planner.Plan(Airports, data).Single().Kind);
        data.UndoEdit();
        Assert.Equal(["ZBAA"], data.Draft.Select(r => r.Get("ICAO/IATA")));
    }

    [Fact]
    public void Patch_import_matches_keys_ignoring_case_and_padding()
    {
        var data = Baseline(Airports, [("ICAO/IATA", "ZBAA"), ("Name", "A")]);

        CsvAdapter.Import(Airports, data, [Row(("ICAO/IATA", " zbaa "), ("Name", "B"))], false);

        Assert.Single(data.Draft);
        Assert.Equal("B", data.Draft[0].Get("Name"));
    }

    [Fact]
    public void Patch_import_matches_an_airport_by_its_api_alias_but_keeps_the_identified_code()
    {
        var baseline = Row(("ICAO/IATA", "EGLL"), ("Name", "Hub"))
            .WithIdentity(new("101", "7"))
            .WithRawApiJson("""{"icao":"EGLL","iata":"LHR"}""");
        var data = Baseline(Airports, baseline);

        CsvAdapter.Import(Airports, data, [Row(("ICAO/IATA", "LHR"), ("Name", "Heathrow"))], false);

        Assert.Single(data.Draft);
        Assert.Equal("EGLL", data.Draft[0].Get("ICAO/IATA"));
        Assert.Equal("Heathrow", data.Draft[0].Get("Name"));
    }

    [Fact]
    public void Patch_import_rejects_duplicate_keys_in_the_file_and_in_the_draft_without_changing_anything()
    {
        var data = Baseline(Airports, [("ICAO/IATA", "ZBAA"), ("Name", "A")]);
        Assert.Throws<FormatException>(() => CsvAdapter.Import(Airports, data, [Row(("ICAO/IATA", "ZSPD")), Row(("ICAO/IATA", "zspd"))], false));
        Assert.Single(data.Draft);
        Assert.Empty(data.Undo);

        var duplicated = Baseline(Airports, [("ICAO/IATA", "ZBAA")], [("ICAO/IATA", "ZBAA")]);
        Assert.Throws<FormatException>(() => CsvAdapter.Import(Airports, duplicated, [Row(("ICAO/IATA", "ZSPD"))], false));
        Assert.Equal(2, duplicated.Draft.Count);
    }

    // ---- explicit deletion (F11/F17) ----

    [Fact]
    public void Rows_missing_from_a_patch_are_not_deletions_only_an_explicit_delete_flag_is()
    {
        var data = Baseline(Airports, [("ICAO/IATA", "ZBAA"), ("Name", "Beijing")], [("ICAO/IATA", "ZSPD"), ("Name", "Shanghai")]);

        CsvAdapter.Import(Airports, data, [Row(("ICAO/IATA", "ZBAA"), ("Name", "Beijing"))], false);
        Assert.Empty(Planner.Plan(Airports, data));

        CsvAdapter.Import(Airports, data, [Row(("ICAO/IATA", "ZSPD"), ("_delete", "TRUE"))], false);
        var change = Planner.Plan(Airports, data).Single();
        Assert.Equal(ChangeKind.Delete, change.Kind);
        Assert.Equal("ZSPD", change.After.Get("ICAO/IATA"));
    }

    // ---- regression: CSV export for a complete import cycle ----

    [Fact]
    public void A_planned_export_can_be_read_back_into_equal_rows()
    {
        var data = new ResourceData { Draft = [Row(("ID", "001"), ("Name", "A \"quoted\", name"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "pax"), ("Extra", "a\"b\r\nc"))] };

        var files = Csv.Export(ResourceKind.Fleets, Planner.Plan(ResourceKind.Fleets, data));
        var round = Reimport(Csv, files.Single()).Single();

        Assert.Equal("001", round.Get("ID"));
        Assert.Equal("A \"quoted\", name", round.Get("Name"));
        Assert.Equal("a\"b\r\nc", round.Get("Extra"));
        Assert.Equal("FALSE", round.Get("_delete"));
        Assert.Equal(Encoding.UTF8.GetByteCount(Text(files.Single())), files.Single().Length);
    }
}
