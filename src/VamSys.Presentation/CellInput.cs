using System.Globalization;
using VamSys.Core;

namespace VamSys.Presentation;

// Turns what was typed into a cell into the format the column stores, when it can be read unambiguously: "930" becomes
// "09:30", "2026/3/1" becomes "2026-03-01 00:00:00", "mon,FRI" becomes "monday,friday". Anything it cannot read is left
// as typed, and the checks then say what is wrong with it.
public static class CellInput
{
    // "%H": a lone "H" would be read as a standard format name.
    static readonly string[] TimeFormats = ["H:mm", "H:m", "HHmm", "Hmm", "%H", "HH", "H.mm", "H:mm:ss"];
    static readonly string[] DateFormats = ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyyMMdd"];
    static readonly string[] DateTimeFormats = ["yyyy-M-d H:mm", "yyyy-M-d H:mm:ss", "yyyy/M/d H:mm", "yyyy/M/d H:mm:ss", "yyyy-M-d'T'H:mm:ss", "yyyy-M-d'T'H:mm"];
    public const string StoredDate = "yyyy-MM-dd HH:mm:ss";

    public static string Normalize(FieldType type, string text)
    {
        var typed = text.Trim();
        if (typed.Length == 0)
            return text;
        // Three digits are read as H mm ("930"): the parser's H takes two digits when it can.
        if (type == FieldType.Time && typed.Length == 3 && typed.All(char.IsAsciiDigit))
            typed = "0" + typed;
        if (type == FieldType.Time)
            return TimeOnly.TryParseExact(typed, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? text
                : TimeOnly.TryParseExact(typed, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
                : text;
        if (type == FieldType.Date)
        {
            // A stored value keeps its sub-second part: only text in another format is rewritten.
            if (DateTime.TryParseExact(typed, StoredDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || typed.Length > StoredDate.Length && typed[StoredDate.Length] == '.')
                return text;
            if (DateTime.TryParseExact(typed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return day.ToString(StoredDate, CultureInfo.InvariantCulture);
            return DateTime.TryParseExact(typed, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
                ? moment.ToString(StoredDate, CultureInfo.InvariantCulture) : text;
        }
        if (type == FieldType.DayList)
        {
            var days = new List<string>();
            foreach (var token in typed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var lower = token.ToLowerInvariant();
                var match = Schemas.WeekdayOrder.Where(d => lower.Length >= 2 && d.StartsWith(lower, StringComparison.Ordinal)).ToList();
                if (match.Count != 1)
                    return text;
                days.Add(match[0]);
            }
            return JoinDays(days);
        }
        if (type == FieldType.Bool)
            return typed.ToUpperInvariant() switch { "TRUE" or "YES" or "Y" or "1" => "TRUE", "FALSE" or "NO" or "N" or "0" => "FALSE", _ => text };
        return text;
    }

    // The days of a day list, and a day list from days, in week order without duplicates.
    public static IReadOnlySet<string> Days(string value)
        => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(d => d.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
    public static string JoinDays(IEnumerable<string> days)
    {
        var chosen = days.ToHashSet(StringComparer.Ordinal);
        return string.Join(",", Schemas.WeekdayOrder.Where(chosen.Contains));
    }
    public static readonly IReadOnlyList<string> WorkingDays = ["monday", "tuesday", "wednesday", "thursday", "friday"];

    // The date picked in a calendar, keeping the time of day the cell had.
    public static string WithDate(string current, DateOnly date)
    {
        var time = DateTime.TryParseExact(current.Trim(), StoredDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var old)
            ? old.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "00:00:00";
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + time;
    }
    public static DateOnly? DateOf(string current)
        => DateTime.TryParseExact(current.Trim().Length >= 10 ? current.Trim()[..10] : "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? DateOnly.FromDateTime(day) : null;

    // Clipboard text as rows of cells: tab separated, one row per line, a final line break ignored.
    public static List<string[]> Table(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines.Select(l => l.Split('\t')).ToList();
    }
}
