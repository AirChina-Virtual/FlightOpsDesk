using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.TestSupport;

public static class DataUiFixture
{
    public static void RequireEmptyDirectory(string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("QA seeding requires a new empty directory; existing data is never overwritten.");
    }

    /// <summary>A database of the old format, or with <paramref name="future"/> one of a newer version than the program knows.</summary>
    public static void SeedStorage(string directory, bool future)
    {
        RequireEmptyDirectory(directory);
        Directory.CreateDirectory(directory);
        using var db = StorageScenarios.Legacy(directory, StorageScenarios.Fixture());
        if (future)
            StorageScenarios.Sql(db, "PRAGMA user_version=5");
        Console.WriteLine("Seeded isolated storage UI");
    }

    /// <summary>A task with an unknown result on a legacy candidate ID and pending identities, without credentials.</summary>
    public static void SeedRecovery(string directory)
    {
        RequireEmptyDirectory(directory);
        DataRow Row(params (string Field, string Value)[] fields) => new() { Fields = fields.ToDictionary(p => p.Field, p => p.Value) };
        var workspace = new Workspace { Name = "QA recovery candidates", Mode = RunMode.Online, AirlineId = "7" };
        var item = new ChangeItem { Resource = ResourceKind.Airports, Kind = ChangeKind.Create, State = ItemState.Unknown, RemoteId = "102",
            After = Row(("ICAO/IATA", "EGLL"), ("Name", "Hub")), Fields = new() { ["ICAO/IATA"] = new(FieldIntent.Set, "EGLL"), ["Name"] = new(FieldIntent.Set, "Hub") } };
        var pendingAirport = new ChangeItem { Resource = ResourceKind.Airports, Kind = ChangeKind.Create, State = ItemState.Pending, After = Row(("ICAO/IATA", "EGLL")) };
        pendingAirport.SetMessage(Messages.Define("ApiAirportCreatePending"));
        var pendingFleet = new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Create, State = ItemState.Pending, After = Row(("Name", "Pending fleet")) };
        pendingFleet.SetMessage(Messages.Define("ApiIdentityPending"));
        workspace.Resources[ResourceKind.Airports].Draft.Add(item.After.Dup());
        workspace.Jobs.Add(new() { Mode = RunMode.Online, Items = [item, pendingAirport, pendingFleet] });
        new WorkspaceStore(directory).Save(workspace);
        Console.WriteLine("Seeded recovery UI without credentials");
    }

    public static void Seed(string directory, int count)
    {
        if (count is not (20000 or 50000)) throw new ArgumentOutOfRangeException(nameof(count));
        RequireEmptyDirectory(directory);
        var workspace = new Workspace { Name=$"QA {count} full route fields", Mode=RunMode.Demo };
        DataRow Row(params (string Field,string Value)[] fields) => new(){Fields=fields.ToDictionary(p=>p.Field,p=>p.Value)};
        CsvAdapter.Import(ResourceKind.Airports,workspace.Resources[ResourceKind.Airports],[Row(("ICAO/IATA","ZBAA"),("Name","Beijing")),Row(("ICAO/IATA","ZSPD"),("Name","Shanghai"))],true);
        CsvAdapter.Import(ResourceKind.Fleets,workspace.Resources[ResourceKind.Fleets],[Row(("ID","1"),("Name","Boeing 737-800"),("Type Code","B738"),("Type (pax/cargo/...)","pax"),("Max Passengers","189"))],true);
        var rows=new List<DataRow>(count);
        for(var n=0;n<count;n++)
        {
            var row=new DataRow.Builder(); foreach(var field in Schemas.All[ResourceKind.Routes].Columns)row[field]="";
            foreach(var (field,value) in new (string,string)[]{("ID",n.ToString()),("Departure Airport (ICAO/IATA)","ZBAA"),("Arrival Airport (ICAO/IATA)","ZSPD"),("Type","jumpseat"),("Callsign","QA"+n.ToString("D5")),("Flight Number",n.ToString("D5")),("Fleet IDs","1"),("Start Date","2026-10-01 00:00:00"),("End Date","2027-10-01 00:00:00"),("Departure Time (HH:MM)","10:00"),("Arrival Time (HH:MM)","12:00"),("Remarks","row "+n),("Tags","performance"),("Is Hidden","FALSE"),("Altitude","33000"),("Cost Index","AUTO"),("_delete","FALSE")})row[field]=value;
            rows.Add(row.ToRow());
        }
        var data=workspace.Resources[ResourceKind.Routes]; CsvAdapter.Import(ResourceKind.Routes,data,rows,true);
        for(var n=0;n<20;n++){var row=data.Draft[n];data.Edit([row.LocalId],()=>data.ReplaceDraftRows([row.With("Internal Remarks","changed "+n)]));}
        var store=new WorkspaceStore(directory);store.Save(workspace);store.SaveLanguage("en-US");
        Console.WriteLine($"Seeded {count} routes with all fields and 20 history steps");
    }
}
