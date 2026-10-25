using VamSys.Core;

// Test helper: a content-identical but distinct row object (what the removed DataRow.Copy() produced);
// used where a test needs reference identity to differ, as the change journal does.
static class TestRowExtensions
{
    public static DataRow Dup(this DataRow row) => row.ToBuilder().ToRow();
}