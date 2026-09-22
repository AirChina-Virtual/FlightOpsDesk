using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

static class AuditScenarios
{
    const string London="""{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"Hub"}""";
    const string Gatwick="""{"id":102,"airline_id":7,"icao":"EGKK","iata":"LGW","name":"Hub"}""";
    const string Fleet="""{"id":4,"airline_id":7,"name":"Old"}""";
    static void Check(bool value,string why="Audit regression failed") { if(!value) throw new Exception(why); }
    static DataRow Row(params (string,string)[] fields)=>new(){Fields=fields.ToDictionary(p=>p.Item1,p=>p.Item2)};
    static ChangeItem Create(ResourceKind kind,DataRow row)=>new(){Resource=kind,Kind=ChangeKind.Create,After=row,Fields=row.Fields.ToDictionary(p=>p.Key,p=>new FieldChange(FieldIntent.Set,p.Value))};
    static OperationsAdapter Api(Workspace w,AuditHttp h)=>new(new OperationsTransport(new HttpClient(h),OperationsAdapter.BaseUri,Guid.NewGuid().ToString(),new TokenProvider(_=>Task.FromResult(new AccessToken("test",DateTimeOffset.UtcNow.AddHours(1))))),w);
    static DataRow Parse(OperationsAdapter a,ResourceKind k,string raw) { using var d=JsonDocument.Parse(raw);return a.FromJson(k,d.RootElement); }
    static HttpResponseMessage Json(string raw,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(raw)};
    static string Envelope(ResourceKind kind,string raw)=>kind==ResourceKind.Routings?raw:"{\"data\":"+raw+"}";
    static string List(string raw)=>"{\"data\":["+raw+"],\"meta\":{\"next_cursor_url\":null}}";
    static ChangeItem Route(ResourceKind k,string dep,string arr)=>Create(k,k==ResourceKind.Routings
        ? Row(("Departure Airport (ICAO/IATA)",dep),("Arrival Airport (ICAO/IATA)",arr),("Route String","DCT"))
        : Row(("Departure Airport (ICAO/IATA)",dep),("Arrival Airport (ICAO/IATA)",arr),("Type","scheduled"),("Callsign","ACA123"),("Flight Number","AC123"),("Fleet IDs","4")));
    static string RouteJson(ResourceKind k)=>k==ResourceKind.Routings
        ? """{"id":9,"airline_id":7,"departure_airport_id":101,"arrival_airport_id":102,"route":"DCT"}"""
        : """{"id":9,"airline_id":7,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"ACA123","flight_number":"AC123","fleet_ids":[4]}""";
    static void Airports(Workspace w,OperationsAdapter a)
    { foreach(var json in new[]{London,Gatwick}) w.Resources[ResourceKind.Airports].Snapshot.Add(Parse(a,ResourceKind.Airports,json)); }
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        foreach(var fault in new[]{"timeout","network","cancel","401","403","429","500","422","json","unexpected"})
            await test("Unknown recovery survives restart and never writes: "+fault,async()=>
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var api=Api(w,h);
                var before=Parse(api,ResourceKind.Fleets,Fleet);var after=before.Copy();after.Fields["Name"]="New";
                var item=new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Update,State=ItemState.Unknown,Before=before,After=after,Fields=new(){["Name"]=new(FieldIntent.Set,"New")}};
                var job=new BatchJob{Items=[item]};w.Jobs.Add(job);
                using var cancel=new CancellationTokenSource();
                h.Next=(r,ct)=>
                {
                    if(fault=="timeout") throw new TimeoutException();
                    if(fault=="network") throw new HttpRequestException();
                    if(fault=="cancel") {cancel.Cancel();throw new OperationCanceledException(ct);}
                    if(fault=="unexpected") throw new FormatException("Malformed response");
                    if(fault=="json") return Json("{");
                    var response=Json("{}",(HttpStatusCode)int.Parse(fault));
                    if(fault=="429") response.Headers.RetryAfter=new(TimeSpan.FromSeconds(1));
                    return response;
                };
                string saved="";
                await new OperationsBatchExecutor(api,w).ExecuteAsync(job,()=>{saved=w.Serialize();return Task.CompletedTask;},cancel.Token);
                Check(item.State==ItemState.Unknown,fault+" lost unknown state");
                if(fault is "401" or "403") Check(job.StatusCode==JobStatus.Paused);
                if(fault=="cancel") Check(job.StatusCode==JobStatus.Canceled);
                w=Workspace.Deserialize(saved);job=w.Jobs.Single();item=job.Items.Single();
                h.Next=(r,ct)=>Json(Envelope(ResourceKind.Fleets,Fleet));
                await new OperationsBatchExecutor(Api(w,h),w).ExecuteAsync(job,()=>Task.CompletedTask,default);
                Check(item.State==ItemState.Unknown && h.Methods.All(m=>m==HttpMethod.Get));
                h.Next=(r,ct)=>Json(Envelope(ResourceKind.Fleets,Fleet.Replace("Old","New")));
                await new OperationsBatchExecutor(Api(w,h),w).ExecuteAsync(job,()=>Task.CompletedTask,default);
                Check(item.State==ItemState.Succeeded && h.Methods.All(m=>m==HttpMethod.Get));
            });
        await test("Interrupted and legacy partially written items remain read-only",async()=>
        {
            foreach(var mode in new[]{"running","created","accepted"})
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var api=Api(w,h);
                var item=Create(ResourceKind.Airports,Row(("ICAO/IATA","EGLL"),("Name","Hub")));
                item.RemoteId="101";item.State=mode=="running"?ItemState.Running:ItemState.Pending;
                item.CreateCompleted=mode=="created";item.WriteAccepted=mode=="accepted";
                h.Next=(r,ct)=>throw new TimeoutException();
                await new OperationsBatchExecutor(api,w).ExecuteAsync(new(){Items=[item]},()=>Task.CompletedTask,default);
                Check(item.State==ItemState.Unknown && h.Methods.All(m=>m==HttpMethod.Get));
                Check(item.RemoteId=="101" && item.CreateCompleted==(mode=="created") && item.WriteAccepted==(mode=="accepted"));
            }
        });
        await test("Recovery cannot be downgraded by unresolved dependencies or conflicts",async()=>
        {
            foreach(var conflict in new[]{false,true})
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var api=Api(w,h);
                var parent=new ChangeItem{Resource=ResourceKind.Airports,State=ItemState.Failed};
                var item=Route(ResourceKind.Routings,"local:"+parent.After.LocalId,"LGW");item.State=ItemState.Running;item.RemoteId="9";item.Dependencies.Add(parent.Id);
                if(conflict) w.Resources[item.Resource].Conflicts.Add(new(item.After.LocalId,"Remarks","a","b","c"));
                await new OperationsBatchExecutor(api,w).ExecuteAsync(new(){Items=[parent,item]},()=>Task.CompletedTask,default);
                Check(item.State==ItemState.Unknown && h.Methods.Count==0);
            }
        });
        await test("Airport recovery validates aliases before binding a supplied ID",async()=>
        {
            foreach(var code in new[]{"EGLL","lhr"})
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var api=Api(w,h);
                var item=Create(ResourceKind.Airports,Row(("ICAO/IATA",code),("Name","Hub")));item.State=ItemState.Unknown;item.RemoteId="102";
                w.Resources[item.Resource].Draft.Add(item.After.Copy());var job=new BatchJob{Items=[item]};
                h.Next=(r,ct)=>Json(Envelope(item.Resource,Gatwick));
                await new OperationsBatchExecutor(api,w).ExecuteAsync(job,()=>Task.CompletedTask,default);
                Check(item.State==ItemState.Unknown && !item.Rebased && w.Resources[item.Resource].Draft[0].Identity==null && w.Resources[item.Resource].Snapshot.Count==0 && w.VerifiedOperations.Count==0);
                item.RemoteId="101";h.Next=(r,ct)=>Json(Envelope(item.Resource,London));
                await new OperationsBatchExecutor(api,w).ExecuteAsync(job,()=>Task.CompletedTask,default);
                Check(item.State==ItemState.Succeeded && w.Resources[item.Resource].Draft[0].Identity!.RemoteId=="101" && h.Methods.All(m=>m==HttpMethod.Get));
            }
        });
        await test("Airport duplicate checks reject both aliases and allow a distinct airport",async()=>
        {
            foreach(var code in new[]{"EGLL","lhr","LGW"})
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var api=Api(w,h);
                h.Next=(r,ct)=>r.Method==HttpMethod.Post ? Json(Gatwick,HttpStatusCode.Created)
                    : Json(r.RequestUri!.AbsolutePath.EndsWith("/102")?Envelope(ResourceKind.Airports,Gatwick):List(London));
                var item=Create(ResourceKind.Airports,Row(("ICAO/IATA",code),("Name","Hub")));
                await new OperationsBatchExecutor(api,w).ExecuteAsync(new(){Items=[item]},()=>Task.CompletedTask,default);
                Check(code=="LGW" ? item.State==ItemState.Succeeded && h.Methods.Count(m=>m==HttpMethod.Post)==1
                    : item.State==ItemState.Failed && item.Description!.Code=="ApiDuplicate" && h.Methods.All(m=>m==HttpMethod.Get));
            }
        });
        foreach(var kind in new[]{ResourceKind.Routings,ResourceKind.Routes})
        {
            await test(kind+" readback uses airport IDs for ICAO, IATA and numeric references",()=>
            {
                foreach(var code in new[]{"EGLL","lhr","101"})
                {
                    var w=new Workspace{AirlineId="7"};var api=Api(w,new());Airports(w,api);
                    var item=Route(kind,code,"LGW");var current=Parse(api,kind,RouteJson(kind));
                    Check(api.Matches(item,current,item.After,false));
                    item.After.Fields["Departure Airport (ICAO/IATA)"]="LGW";
                    item.Fields["Departure Airport (ICAO/IATA)"]=new(FieldIntent.Set,"LGW");
                    Check(!api.Matches(item,current,item.After,false));
                }
                return Task.CompletedTask;
            });
            await test(kind+" duplicate endpoints are compared by ID before POST",async()=>
            {
                foreach(var code in new[]{"EGLL","lhr","101"})
                {
                    var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var api=Api(w,h);Airports(w,api);
                    h.Next=(r,ct)=>Json(List(RouteJson(kind)));
                    var item=Route(kind,code,"LGW");
                    await new OperationsBatchExecutor(api,w).ExecuteAsync(new(){Items=[item]},()=>Task.CompletedTask,default);
                    Check(item.State==ItemState.Failed && item.Description!.Code=="ApiDuplicate" && h.Methods.All(m=>m==HttpMethod.Get));
                }
            });
            await test(kind+" resolved local dependency completes creation and survives restart",async()=>
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var api=Api(w,h);Airports(w,api);
                var parent=Create(ResourceKind.Airports,w.Resources[ResourceKind.Airports].Snapshot[0].Copy());parent.State=ItemState.Succeeded;parent.RemoteId="101";
                var item=Route(kind,"local:"+parent.After.LocalId,"LGW");item.Dependencies.Add(parent.Id);
                w.Resources[kind].Draft.Add(item.After.Copy());var job=new BatchJob{Items=[parent,item]};w.Jobs.Add(job);
                h.Next=(r,ct)=>r.Method==HttpMethod.Post ? Json(RouteJson(kind),HttpStatusCode.Created)
                    : Json(r.RequestUri!.AbsolutePath.EndsWith("/9")?Envelope(kind,RouteJson(kind)):List(""));
                string saved="";await new OperationsBatchExecutor(api,w).ExecuteAsync(job,()=>{saved=w.Serialize();return Task.CompletedTask;},default);
                Check(item.State==ItemState.Succeeded && h.Methods.Count(m=>m==HttpMethod.Post)==1 && w.Resources[kind].Draft[0].Identity!.RemoteId=="9");
                w=Workspace.Deserialize(saved);await new OperationsBatchExecutor(Api(w,h),w).ExecuteAsync(w.Jobs[0],()=>Task.CompletedTask,default);
                Check(h.Methods.Count(m=>m==HttpMethod.Post)==1);
            });
            await test(kind+" ambiguity and cross-VA references cannot confirm recovery",async()=>
            {
                foreach(var mode in new[]{"ambiguous","foreign","missing"})
                {
                    var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var api=Api(w,h);Airports(w,api);
                    if(mode=="ambiguous") Parse(api,ResourceKind.Airports,London.Replace("101","103"));
                    if(mode=="foreign") {w.Resources[ResourceKind.Airports].Snapshot[0].Identity=new("101","8");api=Api(w,h);}
                    if(mode=="missing") {w.Resources[ResourceKind.Airports].Snapshot.Clear();api=Api(w,h);}
                    var item=Route(kind,"LHR","LGW");item.State=ItemState.Unknown;item.RemoteId="9";
                    h.Next=(r,ct)=>Json(Envelope(kind,RouteJson(kind)));
                    await new OperationsBatchExecutor(api,w).ExecuteAsync(new(){Items=[item]},()=>Task.CompletedTask,default);
                    Check(item.State==ItemState.Unknown && !item.Rebased && w.VerifiedOperations.Count==0 && h.Methods.All(m=>m==HttpMethod.Get));
                }
            });
        }
    }
}
sealed class AuditHttp:HttpMessageHandler
{
    public List<HttpMethod> Methods=[];
    public Func<HttpRequestMessage,CancellationToken,HttpResponseMessage> Next=(r,ct)=>throw new Exception("Unexpected request");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    { Methods.Add(request.Method);return Task.FromResult(Next(request,ct)); }
}
