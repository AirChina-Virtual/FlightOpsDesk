using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using VamSys.Infrastructure;

static class CandidateIndexScenarios
{
    static void Check(bool ok,string message="Index assertion failed") { if(!ok) throw new Exception(message); }
    static HttpResponseMessage Json(string value,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(value)};
    static string Page(IEnumerable<string> rows)=>"{\"data\":["+string.Join(',',rows)+"],\"meta\":{\"next_cursor_url\":null}}";
    static string Fleet(int id,string name)=>JsonSerializer.Serialize(new{id,airline_id=7,name,code="B738",type="pax"});
    static DataRow Row(params (string,string)[] fields)=>new(){Fields=fields.ToDictionary(p=>p.Item1,p=>p.Item2)};
    static DataRow NewFleet(string name)=>Row(("Name",name),("Type Code","B738"),("Type (pax/cargo/...)","pax"));
    static ChangeItem Create(ResourceKind kind,DataRow row)=>new(){Resource=kind,Kind=ChangeKind.Create,After=row,Fields=row.Fields.ToDictionary(p=>p.Key,p=>new FieldChange(FieldIntent.Set,p.Value))};
    static DataRow Parse(OperationsAdapter api,ResourceKind kind,string raw){using var doc=JsonDocument.Parse(raw);return api.FromJson(kind,doc.RootElement);}
    static OperationsAdapter Api(Workspace workspace,AuditHttp http,TestTime clock)=>new(new OperationsTransport(new HttpClient(http),OperationsAdapter.BaseUri,"indexes",new TokenProvider(_=>Task.FromResult(new AccessToken("test",clock.GetUtcNow().AddDays(1))),clock),new RequestCoordinator(clock)),workspace);
    static async Task<List<DataRow>> Candidates(OperationsAdapter api,ChangeItem item,CandidateQuerySession session)
    {var result=new List<DataRow>();await foreach(var row in api.ReadDuplicateCandidatesAsync(item,session,default)) result.Add(row);return result;}

    public sealed record IndexBenchmark(int Rows,int Queries,long Builds,int Lists,int Candidates,double Milliseconds,long AllocatedBytes);
    public static async Task<IndexBenchmark> Benchmark()
    {
        var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var clock=new TestTime();var api=Api(w,h,clock);var session=new CandidateQuerySession();
        var page=Page(Enumerable.Range(1,20000).Select(i=>Fleet(i,"Fleet"+i)));
        h.Next=(r,ct)=>Json(page);using var metrics=PerformanceRun.Begin(w.Id,Guid.NewGuid());
        long bytes=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();int candidates=0,queries=0;
        async Task<List<DataRow>> Read(string name){
            queries++;var query=Create(ResourceKind.Fleets,NewFleet(name));var task=Candidates(api,query,session);await clock.Drive(task);foreach(var row in task.Result){Check(api.IsDuplicate(query,row));metrics.Candidate();}candidates+=task.Result.Count;return task.Result;
        }
        for(int i=1;i<=100;i++){
            var before=(await Read("Fleet"+i)).Single();
            api.ConfirmCandidate(new(){Resource=ResourceKind.Fleets,Kind=ChangeKind.Update,RemoteId=i.ToString()},Parse(api,ResourceKind.Fleets,Fleet(i,"Renamed"+i)),session);
            Check((await Read("Fleet"+i)).Count==0);
            Check((await Read("renamed"+i)).Count==1);
            Check((await Read("Absent"+i)).Count==0);
        }
        watch.Stop();return new(20000,queries,metrics.IndexBuilds,h.Methods.Count,candidates,watch.Elapsed.TotalMilliseconds,GC.GetTotalAllocatedBytes(true)-bytes);
    }
}
