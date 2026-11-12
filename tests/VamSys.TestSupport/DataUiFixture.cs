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

    /// <summary>A large task page: 1,000 finished tasks of 20 items each and one active task of 20,000 items.</summary>
    public static void SeedTasks(string directory)
    {
        RequireEmptyDirectory(directory);
        var workspace = StorageScenarios.Fixture();
        workspace.Jobs.Clear();
        workspace.Mode = RunMode.Offline;
        for (var n = 0; n < 1000; n++)
            workspace.Jobs.Add(new() { Created = DateTimeOffset.UnixEpoch.AddMinutes(n), Mode = RunMode.Online, Items = Enumerable.Range(0, 20).Select(k => new ChangeItem { State = ItemState.Succeeded }).ToList() });
        workspace.Jobs.Add(new() { Created = DateTimeOffset.UtcNow, Mode = RunMode.Online, Items = Enumerable.Range(0, 20000).Select(k => new ChangeItem { State = ItemState.Pending }).ToList() });
        var store = new WorkspaceStore(directory);
        store.Save(workspace);
        store.SaveLanguage("en-US");
        Console.WriteLine("Seeded 1000 histories / 20000 history items + 20000 active items");
    }

    static DataRow Airport(string code, string name) => new() { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = code, ["Name"] = name } };

    static void SaveInEnglish(string directory, Workspace workspace)
    {
        var store = new WorkspaceStore(directory);
        store.Save(workspace);
        store.SaveLanguage("en-US");
    }

    /// <summary>An online workspace after a refresh left three conflicts: two on a field, one on a record the server deleted.</summary>
    public static void SeedConflicts(string directory)
    {
        RequireEmptyDirectory(directory);
        var workspace = new Workspace { Name = "QA conflicts", Mode = RunMode.Online, AirlineId = "7" };
        var airports = workspace.Resources[ResourceKind.Airports];
        CsvAdapter.Import(ResourceKind.Airports, airports, [Airport("EGLL", "original one"), Airport("EGKK", "original two"), Airport("EGCC", "original three")], true);
        airports.Draft[0] = airports.Draft[0].With("Name", "mine one");
        airports.Draft[1] = airports.Draft[1].With("Name", "mine two");
        airports.Conflicts.Add(new(airports.Draft[0].LocalId, "Name", "original one", "mine one", "server one"));
        airports.Conflicts.Add(new(airports.Draft[1].LocalId, "Name", "original two", "mine two", "server two"));
        airports.Conflicts.Add(new(airports.Draft[2].LocalId, "*", null, null, null));
        SaveInEnglish(directory, workspace);
    }

    /// <summary>A workspace, which opens offline, with one fleet from the server and one new fleet.</summary>
    public static void SeedParameters(string directory)
    {
        RequireEmptyDirectory(directory);
        var workspace = new Workspace { Name = "QA parameters", Mode = RunMode.Online, AirlineId = "7" };
        var fleets = workspace.Resources[ResourceKind.Fleets];
        var existing = new DataRow { Identity = new("6", "conn"), Fields = new Dictionary<string, string> { ["ID"] = "6", ["Name"] = "Existing fleet", ["Parameter IDs"] = "1" } };
        fleets.Snapshot.Add(existing);
        fleets.Draft.Add(existing);
        fleets.Draft.Add(new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "New fleet" } });
        fleets.SnapshotAt = DateTimeOffset.UtcNow;
        SaveInEnglish(directory, workspace);
    }

    /// <summary>A demo workspace with one edited and one deleted airport, and an older task still pending.</summary>
    public static void SeedSubmission(string directory)
    {
        RequireEmptyDirectory(directory);
        var workspace = new Workspace { Name = "QA submission", Mode = RunMode.Demo };
        var airports = workspace.Resources[ResourceKind.Airports];
        CsvAdapter.Import(ResourceKind.Airports, airports, [Airport("EGLL", "Heathrow"), Airport("EGKK", "Gatwick"), Airport("EGCC", "Manchester")], true);
        airports.Draft[0] = airports.Draft[0].With("Name", "Renamed");
        airports.Draft[1] = airports.Draft[1].With("_delete", "TRUE");
        workspace.Jobs.Add(new() { Mode = RunMode.Demo, Items = [new ChangeItem { Resource = ResourceKind.Airports, Kind = ChangeKind.Update, State = ItemState.Pending, After = airports.Draft[2] }] });
        SaveInEnglish(directory, workspace);
    }

    /// <summary>A demo workspace with one airport in its snapshot, for importing drafts on top.</summary>
    public static void SeedImport(string directory)
    {
        RequireEmptyDirectory(directory);
        var workspace = new Workspace { Name = "QA import", Mode = RunMode.Demo };
        CsvAdapter.Import(ResourceKind.Airports, workspace.Resources[ResourceKind.Airports], [Airport("EGLL", "Heathrow")], true);
        SaveInEnglish(directory, workspace);
    }

    /// <summary>A demo workspace with 20,000 routes of few fields, in the default language.</summary>
    public static void SeedDemoRoutes(string directory)
    {
        RequireEmptyDirectory(directory);
        DataRow Row(params (string Field, string Value)[] fields) => new() { Fields = fields.ToDictionary(p => p.Field, p => p.Value) };
        var workspace = new Workspace { Name = "QA 20000 航线", Mode = RunMode.Demo };
        CsvAdapter.Import(ResourceKind.Airports, workspace.Resources[ResourceKind.Airports], [Row(("ICAO/IATA", "ZBAA"), ("Name", "北京")), Row(("ICAO/IATA", "ZSPD"), ("Name", "上海"))], true);
        CsvAdapter.Import(ResourceKind.Routes, workspace.Resources[ResourceKind.Routes],
            Enumerable.Range(0, 20000).Select(i => Row(("ID", i.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Departure Airport (ICAO/IATA)", "ZBAA"), ("Arrival Airport (ICAO/IATA)", "ZSPD"), ("Type", "jumpseat"), ("Tags", "性能测试"))).ToList(), true);
        new WorkspaceStore(directory).Save(workspace);
        Console.WriteLine("Seeded isolated UI workspace");
    }

    /// <summary>A demo workspace whose four airports have all lost their required name in the draft, so validation has something to say.</summary>
    public static void SeedInvalid(string directory)
    {
        RequireEmptyDirectory(directory);
        var workspace = new Workspace { Name = "QA invalid", Mode = RunMode.Demo };
        var airports = workspace.Resources[ResourceKind.Airports];
        CsvAdapter.Import(ResourceKind.Airports, airports, [Airport("EGLL", "One"), Airport("EGKK", "Two"), Airport("EGCC", "Three"), Airport("EGSS", "Four")], true);
        for (var n = 0; n < airports.Draft.Count; n++)
            airports.Draft[n] = airports.Draft[n].With("Name", "");
        SaveInEnglish(directory, workspace);
    }

    /// <summary>A demo workspace with one fleet, two airports and two scheduled routes between them, so deleting an airport is refused.</summary>
    public static void SeedBatch(string directory) => SeedRoutes(directory, "2099-01-01 00:00:00");

    /// <summary>The batch workspace with a second fleet, for choosing a route's fleets in a single dialog.</summary>
    public static void SeedFleetChoice(string directory)
    {
        SeedBatch(directory);
        var store = new WorkspaceStore(directory);
        var workspace = store.Load(store.List().Single().Id);
        CsvAdapter.Import(ResourceKind.Fleets, workspace.Resources[ResourceKind.Fleets], [
            new DataRow { Fields = new Dictionary<string, string> { ["ID"] = "1", ["Name"] = "Boeing 737-800", ["Type Code"] = "B738", ["Type (pax/cargo/...)"] = "pax" } },
            new DataRow { Fields = new Dictionary<string, string> { ["ID"] = "2", ["Name"] = "Airbus A320", ["Type Code"] = "A320", ["Type (pax/cargo/...)"] = "pax" } }], true);
        store.Save(workspace);
    }

    /// <summary>The batch workspace, except that the second route has no end date.</summary>
    public static void SeedUndatedRoute(string directory) => SeedRoutes(directory, "");

    static void SeedRoutes(string directory, string secondEndDate)
    {
        RequireEmptyDirectory(directory);
        DataRow Route(string id, string endDate) => new()
        {
            Fields = new Dictionary<string, string>
            {
                ["ID"] = id, ["Departure Airport (ICAO/IATA)"] = "EGLL", ["Arrival Airport (ICAO/IATA)"] = "EGKK", ["Type"] = "scheduled", ["Callsign"] = "QA" + id,
                ["Flight Number"] = id, ["Fleet IDs"] = "1", ["End Date"] = endDate
            }
        };
        var workspace = new Workspace { Name = "QA batch", Mode = RunMode.Demo };
        CsvAdapter.Import(ResourceKind.Fleets, workspace.Resources[ResourceKind.Fleets], [new DataRow { Fields = new Dictionary<string, string> { ["ID"] = "1", ["Name"] = "Boeing 737-800", ["Type Code"] = "B738", ["Type (pax/cargo/...)"] = "pax" } }], true);
        CsvAdapter.Import(ResourceKind.Airports, workspace.Resources[ResourceKind.Airports], [Airport("EGLL", "Heathrow"), Airport("EGKK", "Gatwick")], true);
        CsvAdapter.Import(ResourceKind.Routes, workspace.Resources[ResourceKind.Routes], [Route("1", "2099-01-01 00:00:00"), Route("2", secondEndDate)], true);
        SaveInEnglish(directory, workspace);
    }

    /// <summary>A workspace, which opens offline, whose online task has three different unknown results.</summary>
    public static void SeedUnknownResults(string directory)
    {
        RequireEmptyDirectory(directory);
        var workspace = new Workspace { Name = "QA unknown results", Mode = RunMode.Online, AirlineId = "7" };
        var airports = workspace.Resources[ResourceKind.Airports];
        CsvAdapter.Import(ResourceKind.Airports, airports, [Airport("EGLL", "Heathrow"), Airport("EGKK", "Gatwick"), Airport("EGCC", "Manchester")], true);
        ChangeItem Item(ChangeKind kind, DataRow row, bool accepted) => new()
        {
            Resource = ResourceKind.Airports, Kind = kind, State = ItemState.Unknown, After = row, WriteAccepted = accepted,
            Fields = kind == ChangeKind.Update ? new() { ["Name"] = new(FieldIntent.Set, "Changed") } : new()
        };
        workspace.Jobs.Add(new()
        {
            Mode = RunMode.Online,
            // A delete the server accepted; an update it may never have received; an update it accepted, with no ID to read it back by.
            Items = [Item(ChangeKind.Delete, airports.Draft[0], true), Item(ChangeKind.Update, airports.Draft[1], false), Item(ChangeKind.Update, airports.Draft[2], true)]
        });
        SaveInEnglish(directory, workspace);
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
            foreach(var (field,value) in new (string,string)[]{("ID",n.ToString()),("Departure Airport (ICAO/IATA)","ZBAA"),("Arrival Airport (ICAO/IATA)","ZSPD"),("Type","jumpseat"),("Callsign","QA"+n.ToString("D5")),("Flight Number",n.ToString("D5")),("Fleet IDs",""),("Start Date","2026-10-01 00:00:00"),("End Date","2027-10-01 00:00:00"),("Departure Time (HH:MM)","10:00"),("Arrival Time (HH:MM)","12:00"),("Remarks","row "+n),("Tags","performance"),("Is Hidden","FALSE"),("Altitude","33000"),("Cost Index","AUTO"),("_delete","FALSE")})row[field]=value;
            rows.Add(row.ToRow());
        }
        var data=workspace.Resources[ResourceKind.Routes]; CsvAdapter.Import(ResourceKind.Routes,data,rows,true);
        for(var n=0;n<20;n++){var row=data.Draft[n];data.Edit([row.LocalId],()=>data.ReplaceDraftRows([row.With("Internal Remarks","changed "+n)]));}
        var store=new WorkspaceStore(directory);store.Save(workspace);store.SaveLanguage("en-US");
        Console.WriteLine($"Seeded {count} routes with all fields and 20 history steps");
    }
}
