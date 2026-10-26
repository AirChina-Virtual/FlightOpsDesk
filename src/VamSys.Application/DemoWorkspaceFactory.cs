namespace VamSys.Core;

// The sample workspace for trying the program without a server: two airports, two fleets, an aircraft, a routing and
// twelve routes, all already "on the server" so they can be edited and submitted to the demo.
public static class DemoWorkspaceFactory
{
    public static Workspace Create(string name)
    {
        var w = new Workspace { Name = name, Mode = RunMode.Demo };
        void Add(ResourceKind k, params (string, string)[] fields)
        {
            var builder = new DataRow.Builder(fields.Length + 1);
            foreach (var (fieldName, text) in fields)
                builder[fieldName] = text;
            builder["_delete"] = "FALSE";
            var r = builder.ToRow();
            w.Resources[k].Draft.Add(r);
            w.Resources[k].Snapshot.Add(r);
            w.Resources[k].Columns = w.Resources[k].Columns.Concat(r.Fields.Keys).Distinct().ToList();
            w.Resources[k].SnapshotAt = DateTimeOffset.UtcNow;
        }
        Add(ResourceKind.Airports, ("ICAO/IATA", "ZBAA"), ("Name", "北京首都"));
        Add(ResourceKind.Airports, ("ICAO/IATA", "ZSPD"), ("Name", "上海浦东"));
        Add(ResourceKind.Fleets, ("ID", "1"), ("Name", "Boeing 737-800"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"));
        Add(ResourceKind.Fleets, ("ID", "2"), ("Name", "Airbus A320"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "180"));
        Add(ResourceKind.Aircraft, ("ID", "10"), ("Name", "Boeing 737-800"), ("Registration", "B-DEMO"), ("Fleet ID", "1"));
        Add(ResourceKind.Routings, ("ID", "20"), ("Departure Airport (ICAO/IATA)", "ZBAA"), ("Arrival Airport (ICAO/IATA)", "ZSPD"), ("Route String", "DCT"));
        for (int i = 0; i < 12; i++)
            Add(ResourceKind.Routes, ("ID", (100 + i).ToString()), ("Departure Airport (ICAO/IATA)", i % 2 == 0 ? "ZBAA" : "ZSPD"), ("Arrival Airport (ICAO/IATA)", i % 2 == 0 ? "ZSPD" : "ZBAA"), ("Type", "scheduled"), ("Callsign", "DEM" + (100 + i)), ("Flight Number", "DM" + (100 + i)), ("Fleet IDs", "1"), ("Departure Time (HH:MM)", $"{6 + i:00}:30"), ("Arrival Time (HH:MM)", $"{8 + i:00}:45"), ("Tags", "演示"));
        return w;
    }
}
