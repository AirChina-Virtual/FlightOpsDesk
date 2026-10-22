using VamSys.Core;

namespace VamSys.UnitTests.Domain;

/// <summary>
/// Builders shared by the domain tests. Rows that sit in a ResourceData list are frozen in Debug builds, so every
/// helper here changes data by replacing rows, never by writing into one in place; the tests then hold in Debug and Release.
/// </summary>
internal static class TestRows
{
    public static DataRow Row(params (string Field, string Value)[] fields) => new() { Fields = fields.ToDictionary(p => p.Field, p => p.Value) };

    /// <summary>"Name=A|Remarks=" style spelling for table-driven tests; "-" is no row.</summary>
    public static DataRow? Parse(string spec)
        => spec == "-" ? null : Row(spec.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => { var i = p.IndexOf('=', StringComparison.Ordinal); return (p[..i], p[(i + 1)..]); }).ToArray());

    public static DataRow With(this DataRow row, string field, string value)
    { var copy = row.Copy(); copy.Fields[field] = value; return copy; }

    public static DataRow Without(this DataRow row, string field)
    { var copy = row.Copy(); copy.Fields.Remove(field); return copy; }

    /// <summary>A cell edit as the application performs it: a recorded step that replaces the row.</summary>
    public static void Change(this ResourceData data, Guid id, string field, string value, bool newStep = true)
        => data.Edit([id], () => data.ReplaceDraftRows([data.FindDraftRow(id)!.With(field, value)]), newStep);

    /// <summary>A baseline import, as the CSV "baseline" option does: snapshot = draft, no history.</summary>
    public static ResourceData Baseline(ResourceKind kind, params DataRow[] rows)
    {
        var data = new ResourceData();
        CsvAdapter.Import(kind, data, [.. rows], true);
        return data;
    }

    public static ResourceData Baseline(ResourceKind kind, params (string Field, string Value)[][] rows) => Baseline(kind, rows.Select(r => Row(r)).ToArray());

    public static Workspace WorkspaceWith(ResourceKind kind, params DataRow[] rows)
    {
        var workspace = new Workspace();
        CsvAdapter.Import(kind, workspace.Resources[kind], [.. rows], true);
        return workspace;
    }

    public static string Text(byte[] file) => System.Text.Encoding.UTF8.GetString(file);

    /// <summary>The one planned change of a resource after an export round trip.</summary>
    public static List<DataRow> Reimport(CsvAdapter csv, byte[] file) => csv.Map(csv.Parse(Text(file)), new Dictionary<string, string>());

    public static string Snapshot(IEnumerable<DataRow> rows)
        => System.Text.Json.JsonSerializer.Serialize(rows.Select(r => new { r.LocalId, r.Identity, r.RawApiJson, Fields = r.Fields.OrderBy(p => p.Key, StringComparer.Ordinal) }));
}
