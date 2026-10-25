using System.Text;

namespace VamSys.Core;

public record CsvTable(List<string> Headers, List<List<string>> Rows);
public sealed class CsvAdapter : ICsvResourceAdapter
{
    public CsvTable Parse(string text)
    {
        text = text.TrimStart('\uFEFF');
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, endedQuote = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    { field.Append('"'); i++; }
                    else
                    { quoted = false; endedQuote = true; }
                }
                else
                    field.Append(c);
                continue;
            }
            if (c == ',' || c == '\r' || c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                endedQuote = false;
                if (c != ',')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    if (row.Count != 1 || row[0] != "")
                        rows.Add(row);
                    row = [];
                }
            }
            else if (c == '"' && field.Length == 0 && !endedQuote)
                quoted = true;
            else if (endedQuote || c == '"')
                throw MessageErrors.Attach(new FormatException(Messages.Define("Text_269E09BE6D")), Messages.Define("Text_269E09BE6D"));
            else
                field.Append(c);
        }
        if (quoted)
            throw MessageErrors.Attach(new FormatException(Messages.Define("Text_FDC11D46D9")), Messages.Define("Text_FDC11D46D9"));
        if (field.Length > 0 || row.Count > 0 || endedQuote)
        { row.Add(field.ToString()); rows.Add(row); }
        if (rows.Count == 0)
            throw MessageErrors.Attach(new FormatException(Messages.Define("Text_EB79E7A05C")), Messages.Define("Text_EB79E7A05C"));
        var headers = rows[0];
        rows.RemoveAt(0);
        if (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct().Count() != headers.Count)
            throw MessageErrors.Attach(new FormatException(Messages.Define("Text_147B7E591C")), Messages.Define("Text_147B7E591C"));
        if (rows.Any(r => r.Count != headers.Count))
            throw MessageErrors.Attach(new FormatException(Messages.Define("Text_7D9C54257B")), Messages.Define("Text_7D9C54257B"));
        return new(headers, rows);
    }

    public List<DataRow> Map(CsvTable table, IReadOnlyDictionary<string, string> mapping)
    {
        var names = table.Headers.Select(h => mapping.GetValueOrDefault(h, h)).ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct().Count() != names.Length)
            throw MessageErrors.Attach(new FormatException(Messages.Define("Text_E93E44505B")), Messages.Define("Text_E93E44505B"));
        var result = new List<DataRow>(table.Rows.Count);
        foreach (var values in table.Rows)
        {
            var builder = new DataRow.Builder(names.Length);
            for (var i = 0; i < names.Length && i < values.Count; i++)
                builder[names[i]] = values[i];
            result.Add(builder.ToRow());
        }
        return result;
    }

    public static void Import(ResourceKind kind, ResourceData data, List<DataRow> rows, bool baseline)
    {
        // Stored drafts always spell the prefix in lowercase, whatever the file used.
        var referenceFields = Schemas.ReferenceFields(kind);
        for (var i = 0; i < rows.Count; i++)
        {
            DataRow.Builder? edit = null;
            foreach (var field in referenceFields)
                if (rows[i].Fields.TryGetValue(field, out var text) && LocalReference.ContainsLocal(text))
                    (edit ??= rows[i].ToBuilder())[field] = LocalReference.Normalize(text);
            if (edit != null)
                rows[i] = edit.ToRow();
        }
        if (baseline)
        {
            if (rows.Any(r => Schemas.Key(kind, r) == ""))
                throw MessageErrors.Attach(new FormatException(Messages.Define("Text_FF27EDEC0D")), Messages.Define("Text_FF27EDEC0D"));
            data.Snapshot = [.. rows];
            data.Draft = rows;
            data.ClearHistory();
            data.SnapshotAt = DateTimeOffset.UtcNow;
        }
        else
        {
            var incomingKeys = rows.Where(r => Schemas.Key(kind, r) != "").Select(r => Schemas.Key(kind, r)).ToList();
            if (incomingKeys.Count != incomingKeys.Distinct().Count())
                throw MessageErrors.Attach(new FormatException(Messages.Define("Text_FC5C1BF085")), Messages.Define("Text_FC5C1BF085"));
            if (data.Draft.Where(r => Schemas.Key(kind, r) != "").GroupBy(r => Schemas.Key(kind, r)).Any(g => g.Count() > 1))
                throw MessageErrors.Attach(new FormatException(Messages.Define("Text_DFA07F45EA")), Messages.Define("Text_DFA07F45EA"));
            var existing = data.Draft.Where(r => Schemas.Key(kind, r) != "").GroupBy(r => Schemas.Key(kind, r)).ToDictionary(g => g.Key, g => g.ToList());
            if (kind == ResourceKind.Airports)
                existing = data.Draft.SelectMany(r => Schemas.MatchKeys(kind, r).Where(k => k != "").Select(k => (Key: k, Row: r))).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Select(p => p.Row).DistinctBy(r => r.LocalId).ToList());
            var matchedIds = rows.Select(r => existing.GetValueOrDefault(Schemas.Key(kind, r))).Where(m => m?.Count == 1).Select(m => m![0].LocalId).ToList();
            if (matchedIds.Distinct().Count() != matchedIds.Count)
                throw MessageErrors.Attach(new FormatException(Messages.Define("Text_FC5C1BF085")), Messages.Define("Text_FC5C1BF085"));
            foreach (var incoming in rows)
                if (existing.TryGetValue(Schemas.Key(kind, incoming), out var matches) && matches.Count != 1)
                    throw new InvalidOperationException(Messages.Define("Text_C73CE653CA", ("arg0", Schemas.Key(kind, incoming))));
            var added = rows.Where(r => !existing.ContainsKey(Schemas.Key(kind, r))).ToArray();
            data.Edit(matchedIds.Concat(added.Select(r => r.LocalId)), () =>
            {
                // Matched rows are replaced by edited copies, never edited in place.
                var replacements = new List<DataRow>();
                foreach (var incoming in rows)
                {
                    var key = Schemas.Key(kind, incoming);
                    if (key != "" && existing.TryGetValue(key, out var matches))
                    {
                        if (matches.Count != 1)
                            throw MessageErrors.Attach(new InvalidOperationException(Messages.Define("Text_C73CE653CA", ("arg0", key))), Messages.Define("Text_C73CE653CA", ("arg0", key)));
                        var replacement = matches[0].ToBuilder();
                        foreach (var (f, v) in incoming.Fields)
                        {
                            if (kind == ResourceKind.Airports && f == "ICAO/IATA" && matches[0].Identity != null)
                                continue;
                            replacement[f] = v;
                        }
                        replacements.Add(replacement.ToRow());
                    }
                    else
                        data.Draft.Add(incoming);
                }
                data.ReplaceDraftRows(replacements);
            }, structural: added.Length > 0);
        }
        data.Columns = data.Columns.Concat(rows.SelectMany(r => r.Fields.Keys)).Distinct().ToList();
    }

    private static string Encode(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    public IReadOnlyList<byte[]> Export(ResourceKind kind, IEnumerable<ChangeItem> changes, int maxBytes = 10 * 1024 * 1024)
    {
        var items = changes.ToList();
        if (items.Count == 0)
            return [];
        if (items.Any(i => Schemas.ReferenceFields(kind).Any(f => LocalReference.ContainsLocal(i.After.Get(f)))))
            throw MessageErrors.Attach(new InvalidOperationException(Messages.Define("Text_6597D6DF98")), Messages.Define("Text_6597D6DF98"));
        // Separate differing column sets: absent fields must not become explicit empty cells.
        var groups = items.GroupBy(i => string.Join("\u001f", i.After.Fields.Keys.Order(StringComparer.Ordinal))).ToList();
        if (groups.Count > 1)
            return groups.SelectMany(g => Export(kind, g, maxBytes)).ToList();
        var spec = FieldSpecs.Get(kind);
        // Include full materialized rows to preserve unknown fields and required values.
        var headers = new[] { spec.Key }.Concat(spec.Required).Concat(items.SelectMany(i => i.After.Fields.Keys)).Append("_delete").Distinct().ToArray();
        byte[] header = Encoding.UTF8.GetBytes(string.Join(',', headers.Select(Encode)) + "\r\n");
        var parts = new List<byte[]>();
        using var stream = new MemoryStream();
        stream.Write(header);
        foreach (var item in items)
        {
            var fields = new Dictionary<string, string>(item.After.Fields) { ["_delete"] = item.Kind == ChangeKind.Delete ? "TRUE" : "FALSE" };
            var bytes = Encoding.UTF8.GetBytes(string.Join(',', headers.Select(h => Encode(fields.GetValueOrDefault(h, "")))) + "\r\n");
            if (header.Length + bytes.Length > maxBytes)
                throw MessageErrors.Attach(new InvalidOperationException(Messages.Define("Text_924C111685")), Messages.Define("Text_924C111685"));
            if (stream.Length + bytes.Length > maxBytes)
            { parts.Add(stream.ToArray()); stream.SetLength(0); stream.Write(header); }
            stream.Write(bytes);
        }
        if (stream.Length > header.Length)
            parts.Add(stream.ToArray());
        return parts;
    }
}
