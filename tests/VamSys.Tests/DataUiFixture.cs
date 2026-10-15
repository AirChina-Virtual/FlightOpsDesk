using VamSys.Core;
using VamSys.Infrastructure;

static class DataUiFixture
{
    public static void RequireEmptyDirectory(string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("QA seeding requires a new empty directory; existing data is never overwritten.");
    }

    public static void Seed(string directory, int count)
    {
        if (count is not (20000 or 50000)) throw new ArgumentOutOfRangeException(nameof(count));
        RequireEmptyDirectory(directory);
        var workspace = new Workspace { Name=$"QA {count} full route fields", Mode=RunMode.Demo };
        DataRow Row(params (string Field,string Value)[] fields) => new(){Fields=fields.ToDictionary(p=>p.Field,p=>p.Value)};
        CsvAdapter.Import(ResourceKind.Airports,workspace.Resources[ResourceKind.Airports],[Row(("ICAO/IATA","ZBAA"),("Name","Beijing")),Row(("ICAO/IATA","ZSPD"),("Name","Shanghai"))],true);
        CsvAdapter.Import(ResourceKind.Fleets,workspace.Resources[ResourceKind.Fleets],[Row(("ID","1"),("Name","Boeing 737-800"),("Type Code","B738"),("Type (pax/cargo/...)","pax"))],true);
        var rows=new List<DataRow>(count);
        for(var n=0;n<count;n++)
        {
            var row=new DataRow(); foreach(var field in Schemas.All[ResourceKind.Routes].Columns)row.Fields[field]="";
            foreach(var (field,value) in new (string,string)[]{("ID",n.ToString()),("Departure Airport (ICAO/IATA)","ZBAA"),("Arrival Airport (ICAO/IATA)","ZSPD"),("Type","jumpseat"),("Callsign","CCA"+n),("Flight Number",n.ToString()),("Fleet IDs","1"),("Start Date","2026-10-01 00:00:00"),("End Date","2027-10-01 00:00:00"),("Departure Time (HH:MM)","10:00"),("Arrival Time (HH:MM)","12:00"),("Remarks","row "+n),("Tags","performance"),("Is Hidden","FALSE"),("Altitude","33000"),("Cost Index","AUTO"),("_delete","FALSE")})row.Fields[field]=value;
            rows.Add(row);
        }
        var data=workspace.Resources[ResourceKind.Routes]; CsvAdapter.Import(ResourceKind.Routes,data,rows,true);
        for(var n=0;n<20;n++){var row=data.Draft[n];data.Edit([row.LocalId],()=>row.Fields["Internal Remarks"]="changed "+n);}
        var store=new WorkspaceStore(directory);store.Save(workspace);store.SaveLanguage("en-US");
        Console.WriteLine($"Seeded {count} routes with all fields and 20 history steps");
    }
}
