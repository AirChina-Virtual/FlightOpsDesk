using System.Reflection;
using System.Text;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.UnitTests.Domain;

// The fast row reader of a workspace read (2026-11-23, stage 7 item 3): for the shape the program writes it builds what
// JsonSerializer.Deserialize<DataRow> builds, to the field order, the shared field names and the dictionary's capacity;
// for every other text it declines, so the general way decides.
public class DataRowJsonTests
{
    static readonly FieldInfo fieldsField = typeof(DataRow).GetField("fields", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static Dictionary<string, string> Owned(DataRow row) => (Dictionary<string, string>)fieldsField.GetValue(row)!;

    static IEnumerable<DataRow> Rows()
    {
        yield return new DataRow();
        yield return new DataRow { Fields = new Dictionary<string, string> { ["ID"] = "1", ["Name"] = "Boeing \"737\"", ["Type Code"] = "B738" } };
        var all = new DataRow.Builder(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));
        foreach (var field in Schemas.All[ResourceKind.Routes].Columns) all[field] = "";
        all["Remarks"] = "line\nbreak\ttab \\ back </script> 中文 ✈ 😀 \u0001";
        all["Imported Only"] = "x";
        all["导入列"] = "值";
        all[new string('n', 200)] = "a name longer than the reader's buffer";
        all["Escaped \"name\""] = new string('v', 300);
        var full = all.ToRow();
        yield return full;
        yield return full.WithIdentity(new ResourceIdentity("500", "7")).WithRawApiJson("{\"id\":500,\"name\":\"raw \\\"q\\\"\"}");
        yield return full.WithIdentity(new ResourceIdentity("501", "7", "3"));
    }

    static void AssertSame(DataRow expected, DataRow actual)
    {
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(expected.LocalId, actual.LocalId);
        Assert.Equal(expected.Identity, actual.Identity);
        Assert.Equal(expected.RawApiJson, actual.RawApiJson);
        Assert.Equal(expected.Fields.Keys, actual.Fields.Keys);
        Assert.Equal(Owned(expected).EnsureCapacity(0), Owned(actual).EnsureCapacity(0));
        foreach (var key in actual.Fields.Keys)
            Assert.Same(FieldNames.Share(key), key);
    }

    [Fact]
    public void A_row_in_the_written_shape_reads_as_the_general_way_reads_it()
    {
        foreach (var row in Rows())
        {
            var json = JsonSerializer.Serialize(row);
            using var names = FieldNames.BeginScope();
            var expected = JsonSerializer.Deserialize<DataRow>(json)!;
            var actual = DataRowJson.TryRead(Encoding.UTF8.GetBytes(json));
            Assert.NotNull(actual);
            AssertSame(expected, actual);
        }
    }

    [Fact]
    public void Names_unknown_to_the_field_table_and_equal_values_are_shared_within_one_read()
    {
        var a = new DataRow { Fields = new Dictionary<string, string> { ["Imported Only"] = "same value", ["Name"] = "same value" } };
        var b = new DataRow { Fields = new Dictionary<string, string> { [new string("Imported Only".AsSpan())] = new string("same value".AsSpan()) } };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        using (FieldNames.BeginScope())
        {
            var first = DataRowJson.TryRead(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(a)), values)!;
            var second = DataRowJson.TryRead(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(b)), values)!;
            Assert.Same(first.Fields.Keys.First(), second.Fields.Keys.Single());
            Assert.Same(first.Get("Imported Only"), first.Get("Name"));
            Assert.Same(first.Get("Name"), second.Get("Imported Only"));
        }
        // Without a table each value is its own string, as the general way makes it.
        var plain = DataRowJson.TryRead(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(a)))!;
        Assert.NotSame(plain.Get("Imported Only"), plain.Get("Name"));
    }

    const string Id = "\"LocalId\":\"01234567-89ab-cdef-0123-456789abcdef\"";

    [Theory]
    [InlineData("{" + Id + ",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{},\"Extra\":1}")]
    [InlineData("{" + Id + ",\"Identity\":null,\"Fields\":{}}")]
    [InlineData("{\"Identity\":null,\"RawApiJson\":null,\"Fields\":{}}")]
    [InlineData("{" + Id + ",\"Identity\":null,\"RawApiJson\":null}")]
    [InlineData("{" + Id + "," + Id + ",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{}}")]
    [InlineData("{" + Id + ",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{},\"Fields\":{}}")]
    [InlineData("{" + Id + ",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{\"A\":\"1\",\"A\":\"2\"}}")]
    [InlineData("{" + Id + ",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{\"A\":null}}")]
    [InlineData("{" + Id + ",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{\"A\":1}}")]
    [InlineData("{" + Id + ",\"Identity\":null,\"RawApiJson\":null,\"Fields\":null}")]
    [InlineData("{" + Id + ",\"Identity\":null,\"RawApiJson\":5,\"Fields\":{}}")]
    [InlineData("{" + Id + ",\"Identity\":{\"RemoteId\":\"1\",\"ConnectionId\":\"7\"},\"RawApiJson\":null,\"Fields\":{}}")]
    [InlineData("{" + Id + ",\"Identity\":{\"RemoteId\":\"1\",\"ConnectionId\":\"7\",\"ParentFleetId\":null,\"Other\":1},\"RawApiJson\":null,\"Fields\":{}}")]
    [InlineData("{" + Id + ",\"Identity\":\"x\",\"RawApiJson\":null,\"Fields\":{}}")]
    [InlineData("{\"LocalId\":\"not a guid\",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{}}")]
    [InlineData("{\"LocalId\":null,\"Identity\":null,\"RawApiJson\":null,\"Fields\":{}}")]
    [InlineData("{\"localId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{}}")]
    [InlineData("{\"Local\\u0049d\":\"01234567-89ab-cdef-0123-456789abcdef\",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{}}")]
    [InlineData("{" + Id + ",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{}} trailing")]
    [InlineData("{" + Id + ",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{},}")]
    [InlineData("{" + Id + ",/* comment */\"Identity\":null,\"RawApiJson\":null,\"Fields\":{}}")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("{" + Id + ",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{\"A\":\"1\"")]
    public void Any_other_shape_is_left_to_the_general_way(string json)
        => Assert.Null(DataRowJson.TryRead(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Text_that_is_not_valid_UTF8_is_left_to_the_general_way()
    {
        var bytes = Encoding.UTF8.GetBytes("{" + Id + ",\"Identity\":null,\"RawApiJson\":null,\"Fields\":{\"A\":\"xx\"}}");
        bytes[^5] = 0xFF;
        Assert.Null(DataRowJson.TryRead(bytes));
    }
}
