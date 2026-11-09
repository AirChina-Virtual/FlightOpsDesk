namespace VamSys.Presentation;

// A table sort as one string, as the page state keeps it: the column, and a mark after it when it is descending. The
// mark is a control character, which no column name contains.
public static class DataSort
{
    const string DescendingMark = "\u001Fdesc";

    public static string? Format(string? field, bool descending) => field is null ? null : descending ? field + DescendingMark : field;
    public static (string? Field, bool Descending) Parse(string? sort)
        => sort is null ? (null, false) : sort.EndsWith(DescendingMark, StringComparison.Ordinal) ? (sort[..^DescendingMark.Length], true) : (sort, false);
    // A click on a column header: sort by it, then the other way round, then not at all.
    public static string? Next(string? sort, string field)
    {
        var (current, descending) = Parse(sort);
        return current != field ? Format(field, false) : descending ? null : Format(field, true);
    }
}
