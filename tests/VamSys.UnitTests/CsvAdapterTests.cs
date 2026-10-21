using VamSys.Core;

namespace VamSys.UnitTests;

public class CsvAdapterTests
{
    [Fact]
    public void Parse_keeps_quoted_commas_and_escaped_quotes()
    {
        var table = new CsvAdapter().Parse("Name,Note\r\nB738,\"a,b\"\r\nA320,\"say \"\"hi\"\"\"\r\n");

        Assert.Equal(["Name", "Note"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["B738", "a,b"], table.Rows[0]);
        Assert.Equal(["A320", "say \"hi\""], table.Rows[1]);
    }
}
