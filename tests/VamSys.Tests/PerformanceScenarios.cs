using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

static class PerformanceScenarios
{
    static void Check(bool ok,string why="Performance assertion failed"){if(!ok)throw new Exception(why);}
    static HttpResponseMessage Json(string s,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(s)};
    static string Page(string rows,string? next=null)=>"{\"data\":["+rows+"],\"meta\":{\"next_cursor_url\":"+JsonSerializer.Serialize(next)+"}}";
    const string Airport="""{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR"}""";
    static DataRow Parse(OperationsAdapter a,ResourceKind k,string raw){using var d=JsonDocument.Parse(raw);return a.FromJson(k,d.RootElement);}
    static OperationsTransport Transport(AuditHttp h,TestTime time,RequestCoordinator? pool=null,string key="test")=>new(new HttpClient(h),OperationsAdapter.BaseUri,key,new TokenProvider(_=>Task.FromResult(new AccessToken("test",time.GetUtcNow().AddHours(1))),time),pool??new(time));
    static OperationsAdapter Api(Workspace w,AuditHttp h,TestTime time)=>new(Transport(h,time),w);
    static ChangeItem Create(ResourceKind k,params (string,string)[] fields)
    {var row=new DataRow{Fields=fields.ToDictionary(p=>p.Item1,p=>p.Item2)};return new(){Resource=k,Kind=ChangeKind.Create,After=row,Fields=row.Fields.ToDictionary(p=>p.Key,p=>new FieldChange(FieldIntent.Set,p.Value))};}
    static ChangeItem Route(ResourceKind k,string flight="AC123")=>k==ResourceKind.Routings
        ?Create(k,("Departure Airport (ICAO/IATA)","LHR"),("Arrival Airport (ICAO/IATA)","101"),("Route String","DCT"))
        :Create(k,("Departure Airport (ICAO/IATA)","LHR"),("Arrival Airport (ICAO/IATA)","101"),("Flight Number",flight),("Callsign","ACA123"),("Type","scheduled"),("Fleet IDs","4"));
    static string RouteJson(ResourceKind k,string flight="AC123")=>k==ResourceKind.Routings
        ?"""{"id":9,"airline_id":7,"departure_airport_id":101,"arrival_airport_id":101,"route":"DCT"}"""
        :"{\"id\":9,\"departure_id\":101,\"arrival_id\":101,\"flight_number\":"+JsonSerializer.Serialize(flight)+",\"callsign\":\"ACA123\",\"type\":\"scheduled\",\"fleet_ids\":[4]}";
    public sealed record QueryBenchmark(int Existing,int New,int Gets,int Posts,int Readbacks,double Seconds,int ApprovalGets);
    static async Task<QueryBenchmark> RouteBenchmark()
    {
        var time=new TestTime();var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h,time);Parse(a,ResourceKind.Airports,Airport);
        // Existing routes belong to other airport pairs; the requested pair has sparse candidates.
        var existing=Enumerable.Range(0,20000).Select(i=>new {departure_id=1000+i,arrival_id=30000+i}).ToArray();
        int gets=0,posts=0,readbacks=0,approvalGets=0;var created=new Dictionary<string,string>();
        h.Next=(r,ct)=>
        {
            if(RouteApprovalFixture.Respond(r) is {} approval){approvalGets++;return approval;}
            if(r.Method==HttpMethod.Post)
            {
                posts++;using var body=JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result);var flight=body.RootElement.GetProperty("flight_number").GetString()!;
                var raw=RouteJson(ResourceKind.Routes,flight).Replace("\"id\":9,","\"id\":"+(posts+100)+",");created[(posts+100).ToString()]=raw;return Json(raw,HttpStatusCode.Created);
            }
            if(r.RequestUri!.AbsolutePath.EndsWith("/routes"))
            {gets++;Check(Uri.UnescapeDataString(r.RequestUri.Query).Contains("filter[departure_id]=101"));Check(!existing.Any(x=>x.departure_id==101&&x.arrival_id==101));return Json(Page(""));}
            readbacks++;return Json("{\"data\":"+created[r.RequestUri.Segments.Last()]+"}");
        };
        var job=new BatchJob{Items=Enumerable.Range(0,100).Select(i=>Route(ResourceKind.Routes,"AC"+i.ToString("000"))).ToList()};
        await time.Drive(new OperationsBatchExecutor(a,w).ExecuteAsync(job,()=>Task.CompletedTask,default));Check(job.Items.All(i=>i.State==ItemState.Succeeded));
        return new(existing.Length,100,gets,posts,readbacks,(time.GetUtcNow()-DateTimeOffset.UnixEpoch).TotalSeconds,approvalGets);
    }
    public static async Task Benchmark(string directory)
    {
        Directory.CreateDirectory(directory);var queries=await RouteBenchmark();var saves=new List<object>();
        foreach(var undo in new[]{0,3})
        {
            var w=new Workspace{Name="Synthetic performance workspace",AirlineId="7"};var data=w.Resources[ResourceKind.Routes];
            data.Snapshot=Enumerable.Range(1,20000).Select(i=>new DataRow{Identity=new(i.ToString(),"7"),RawApiJson=RouteJson(ResourceKind.Routes,"AC123"),Fields = new Dictionary<string, string> {["ID"]=i.ToString(),["Departure Airport (ICAO/IATA)"]="EGLL",["Arrival Airport (ICAO/IATA)"]="EGLL",["Flight Number"]="AC123",["Remarks"]=new string('x',100)}}).ToList();
            data.Draft=data.Snapshot.Select(r=>r.Dup()).ToList();for(var n=0;n<undo;n++){var row=data.Draft[n];data.Edit([row.LocalId],()=>data.ReplaceDraftRows([row.With("Remarks","history "+n)]));}
            var store=new WorkspaceStore(Path.Combine(directory,"storage-"+undo));using var run=PerformanceRun.Begin(w.Id,Guid.NewGuid());store.Save(w);run.Finish("Completed");run.TryWrite(directory);
            saves.Add(new{UndoSnapshots=undo,Rows=20000,run.SaveAttempts,run.SaveSuccesses,run.SaveFailures,run.JsonBytes,run.SerializationMs,run.SqliteMs});
        }
        var json=JsonSerializer.Serialize(new{Queries=queries,Index=await CandidateIndexScenarios.Benchmark(),Saves=saves},new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(directory,"benchmark.json"),json);Console.WriteLine(json);
    }
}
