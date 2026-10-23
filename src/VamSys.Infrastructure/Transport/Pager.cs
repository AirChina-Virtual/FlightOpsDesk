using System.Net;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

// Cursor paging over a GET function. A page size of 0 keeps the server default.
internal sealed class Pager(Uri origin, Func<Uri, CancellationToken, Task<JsonDocument>> get)
{
    const string PageSizeKey = "page%5Bsize%5D";
    static bool HasPageSize(Uri uri) => uri.Query.Contains(PageSizeKey, StringComparison.OrdinalIgnoreCase) || Uri.UnescapeDataString(uri.Query).Contains("page[size]", StringComparison.OrdinalIgnoreCase);
    internal static Uri WithPageSize(Uri uri, int size) => size <= 0 || HasPageSize(uri) ? uri
        : new UriBuilder(uri) { Query = (uri.Query.Length > 1 ? uri.Query[1..] + "&" : "") + PageSizeKey + "=" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Uri;
    public async IAsyncEnumerable<JsonElement> ReadAsync(Uri first, int pageSize, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Uri? next = first;
        var visited = new HashSet<string>();
        var size = pageSize;
        var firstPage = true;
        while (next != null)
        {
            if (!visited.Add(next.AbsoluteUri))
                throw new FormatException(Messages.Define("Text_122031269F"));
            var target = WithPageSize(next, size);
            JsonDocument json;
            try
            { OriginPolicy.Ensure(origin, target); json = await get(target, ct); }
            catch (ApiResponseException e) when (firstPage && target != next && e.Status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            {
                // An instance that rejects the page size still serves its documented default.
                size = 0;
                OriginPolicy.Ensure(origin, next);
                json = await get(next, ct);
            }
            firstPage = false;
            using var page = json;
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new FormatException(Messages.Define("Text_61CA285291"));
            PerformanceRun.Current?.Page();
            foreach (var row in data.EnumerateArray())
                yield return row.Clone();
            next = null;
            if (json.RootElement.TryGetProperty("meta", out var meta))
            {
                if (!meta.TryGetProperty("next_cursor_url", out var cursor))
                    meta.TryGetProperty("next_page_url", out cursor);
                if (cursor.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(cursor.GetString()))
                    next = new Uri(origin, cursor.GetString());
            }
        }
    }
}
