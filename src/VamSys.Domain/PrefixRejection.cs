using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VamSys.Core;

// 2.4: a route the server refused (422) only because some of its fleets are not approved for its callsign and flight
// number prefixes. The server names every such fleet at once, as "fleet_ids.N" errors (recorded live on 2026-11-06):
//   "fleet_ids.6": ["Fleet 'Airbus A330-200P2F' (ID: 26725) is not approved for callsign prefix 'CCA' and flight number prefix 'CA'."]
// The fleet is taken from the "(ID: n)" in the message, or else from position N of the fleet_ids the request sent. Any
// other error in the answer means it is not only about prefixes, and nothing is proposed.
public static partial class PrefixRejection
{
    [GeneratedRegex(@"\(ID: (\d+)\)")]
    private static partial Regex IdInMessage();

    public static IReadOnlyList<string> DeniedFleetIds(ChangeItem item)
    {
        if (item.Resource != ResourceKind.Routes || item.DiagnosticDetails is not { Status: 422, FieldErrors.Count: > 0 } details)
            return [];
        var sent = SentFleetIds(item);
        var ids = new List<string>();
        foreach (var (field, messages) in details.FieldErrors)
        {
            if (!field.StartsWith("fleet_ids.", StringComparison.Ordinal)
                || messages.Length == 0 || !messages.All(m => m.Contains("is not approved for callsign prefix", StringComparison.Ordinal)))
                return [];
            var id = messages.Select(m => IdInMessage().Match(m)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
            if (id == null && int.TryParse(field["fleet_ids.".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < sent.Count)
                id = sent[index];
            if (id == null)
                return [];
            if (!ids.Contains(id))
                ids.Add(id);
        }
        return ids;
    }

    static List<string> SentFleetIds(ChangeItem item)
    {
        var body = item.ApiSteps.FirstOrDefault(s => s.Method is "POST" or "PUT")?.Body;
        if (body == null || !body.TryGetValue("fleet_ids", out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray().Select(v => v.ToString()).ToList();
    }
}
