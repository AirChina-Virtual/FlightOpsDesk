using System.Globalization;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

// Errors and value rules that need no state. OperationsAdapter.Block is the public name callers outside this project use.
internal static class ApiErrors
{
    public static InvalidOperationException Block(string key, string? field = null)
        => MessageErrors.Attach(new InvalidOperationException(Messages.Define(key, ("field", field))), Messages.Define(key, ("field", field)));

    public static long PositiveId(string text)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw Block("ApiInvalid", text);

    // The text of a JSON value as it appears in a grid cell: null is empty, arrays are comma separated, booleans are upper case.
    public static string CellText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => "",
        JsonValueKind.Array => string.Join(',', e.EnumerateArray().Select(CellText)),
        JsonValueKind.True => "TRUE",
        JsonValueKind.False => "FALSE",
        JsonValueKind.String => e.GetString()!,
        _ => e.ToString()
    };
}
