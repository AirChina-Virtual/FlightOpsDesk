using VamSys.Core;

// A content-identical but distinct row object (what the removed DataRow.Copy() produced).
static class TestRowExtensions
{
    public static DataRow Dup(this DataRow row) => row.ToBuilder().ToRow();
}
