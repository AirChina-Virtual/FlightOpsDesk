using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// A workspace read takes each row's JSON as SQLite stores it and reads the shape the program writes directly
/// (2026-11-23, stage 7 item 3). Text in any other shape, which an older program or a hand-edited database may hold, is
/// read exactly as before: from the string a data reader makes of it (invalid UTF-8 replaced), by the general deserializer.
/// </summary>
public class RowReadTests
{
    [Fact]
    public void Rows_in_another_shape_read_as_the_general_deserializer_reads_the_stored_text()
    {
        using var directory = TempDataDirectory.Create();
        var store = new WorkspaceStore(directory.Path);
        var w = new Workspace { Name = "shapes" };
        var fleets = w.Resources[ResourceKind.Fleets];
        for (var n = 0; n < 5; n++)
            fleets.Draft.Add(new DataRow.Builder(new Guid(n + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)) { ["Name"] = "fleet " + n }.ToRow());
        store.Save(w);

        string Row(int n, string fields, string extra = "") =>
            "{\"LocalId\":\"" + new Guid(n + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) + "\",\"Identity\":null,\"RawApiJson\":null," + extra + fields + "}";
        var stored = new object[]
        {
            Row(0, "\"Fields\":{\"Name\":\"extra property\"}", "\"Extra\":1,"),
            Row(1, "\"Fields\":{\"Name\":null,\"Other\":\"null value\"}"),
            Row(2, "\"Fi\\u0065lds\":{\"Name\":\"escaped property name\"}"),
            Encoding.UTF8.GetBytes(Row(3, "\"Fields\":{\"Name\":\"invalid UTF-8: é\"}")) is var bytes ? Invalid(bytes) : null!,
            Row(4, "\"Fields\":{\"Name\":\"canonical\"}"),
        };
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory.Path, "workspaces.db"), Pooling = false }.ToString()))
        {
            c.Open();
            for (var n = 0; n < stored.Length; n++)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE resource_rows SET json=$json WHERE resource=$r AND collection=1 AND ordinal=$n";
                cmd.Parameters.AddWithValue("$json", stored[n]);
                cmd.Parameters.AddWithValue("$r", (int)ResourceKind.Fleets);
                cmd.Parameters.AddWithValue("$n", n);
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }
        }

        var read = new WorkspaceStore(directory.Path).Load(w.Id).Resources[ResourceKind.Fleets].Draft;
        for (var n = 0; n < stored.Length; n++)
        {
            var text = stored[n] is byte[] b ? Encoding.UTF8.GetString(b) : (string)stored[n];
            var expected = JsonSerializer.Deserialize<DataRow>(text)!;
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(read[n]));
            Assert.Equal(expected.Fields.Keys, read[n].Fields.Keys);
        }
        Assert.Equal(((char)0xFFFD).ToString(), read[3].Get("Name")[^1..]);
        Assert.Null(read[1].Fields["Name"]);
    }

    // The text with its last character's UTF-8 bytes cut in half: stored as a blob, it is not valid UTF-8.
    static byte[] Invalid(byte[] utf8)
    {
        var at = Array.LastIndexOf(utf8, (byte)0xC3);
        return [.. utf8[..(at + 1)], .. utf8[(at + 2)..]];
    }
}
