namespace VamSys.Core;

// 3.3 (F3): how many requests a submission will roughly send, for the confirmation dialog. Counted per item as the
// executor sends them against a normal server (measured on the fake server, see SubmissionEstimateTests):
//   update or move          read before, write, read back                                   3
//   delete                  as above; an airport also checks routes and routings (4 lists), a fleet its aircraft and routes   3 / 7 / 5
//   create                  write and read back                                             2
// plus what creates and prefix checks read once per task: the duplicate candidates (every page of airports or fleets, the
// fleet list and each fleet's aircraft, one list per airport pair of routes and routings), the airline parameters and each
// fleet a checked route names. Retries, 429 waits and unknown results are not foreseen, so the dialog says "about".
public static class SubmissionEstimate
{
    // The program sends at most one request per second (RequestBudget).
    public static readonly TimeSpan PerRequest = TimeSpan.FromSeconds(1);

    public static int Requests(IReadOnlyCollection<ChangeItem> items, Workspace workspace)
    {
        static int Pages(int rows) => Math.Max(1, (rows + 99) / 100);
        var total = 0;
        var parameters = false;
        var checkedFleets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new HashSet<(ResourceKind, string, string)>();
        var creates = new HashSet<ResourceKind>();
        foreach (var item in items)
        {
            total += item.Kind switch
            {
                ChangeKind.Create => 2,
                ChangeKind.Delete => item.Resource switch { ResourceKind.Airports => 7, ResourceKind.Fleets => 5, _ => 3 },
                _ => 3
            };
            if (item.Kind == ChangeKind.Create)
            {
                creates.Add(item.Resource);
                if (item.Resource is ResourceKind.Routes or ResourceKind.Routings)
                    pairs.Add((item.Resource, item.After.Get("Departure Airport (ICAO/IATA)").ToUpperInvariant(), item.After.Get("Arrival Airport (ICAO/IATA)").ToUpperInvariant()));
                if (item.Resource == ResourceKind.Fleets && (item.RequiresAirlineParameter || item.After.Get("Parameter IDs") != ""))
                    parameters = true;
            }
            if (item.Resource == ResourceKind.Routes && item.Kind != ChangeKind.Delete && item.After.Get("Type") is not ("" or "jumpseat")
                && (item.Kind == ChangeKind.Create || item.Fields.Keys.Any(f => f is "Fleet IDs" or "Callsign" or "Flight Number" or "Type")))
            {
                parameters = true;
                foreach (var fleet in item.After.Get("Fleet IDs").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    checkedFleets.Add(fleet);
            }
        }
        foreach (var kind in creates)
            total += kind switch
            {
                ResourceKind.Airports or ResourceKind.Fleets => Pages(workspace.Resources[kind].Snapshot.Count),
                ResourceKind.Aircraft => Pages(workspace.Resources[ResourceKind.Fleets].Snapshot.Count) + workspace.Resources[ResourceKind.Fleets].Snapshot.Count,
                _ => 0
            };
        return total + pairs.Count + (parameters ? 1 : 0) + checkedFleets.Count;
    }

    public static TimeSpan Duration(int requests) => PerRequest * requests;
}
