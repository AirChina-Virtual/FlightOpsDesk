using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

static class LifecycleScenarios
{
    static void Check(bool v,string why="Lifecycle assertion failed"){if(!v)throw new Exception(why);}
    static HttpResponseMessage Json(string s)=>new(HttpStatusCode.OK){Content=new StringContent(s)};
    const string OldAirport="""{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR"}""";
    const string NewAirports="""{"data":[{"id":103,"airline_id":7,"icao":"EGLL","iata":"LHR"},{"id":102,"airline_id":7,"icao":"EGKK","iata":"LGW"}],"meta":{"next_cursor_url":null}}""";
    const string Empty="""{"data":[],"meta":{"next_cursor_url":null}}""";
    const string Fleet="""{"id":4,"airline_id":7,"name":"Old"}""";
    static OperationsAdapter Api(Workspace w,AuditHttp h)=>new(new OperationsTransport(new HttpClient(h),OperationsAdapter.BaseUri,Guid.NewGuid().ToString(),new TokenProvider(_=>Task.FromResult(new AccessToken("test",DateTimeOffset.UtcNow.AddHours(1))))),w);
    static DataRow Parse(OperationsAdapter a,ResourceKind k,string raw){using var d=JsonDocument.Parse(raw);return a.FromJson(k,d.RootElement);}
    static ChangeItem Routing(string code)=>new(){Resource=ResourceKind.Routings,Kind=ChangeKind.Create,After=new(){Fields=new(){["Departure Airport (ICAO/IATA)"]=code,["Arrival Airport (ICAO/IATA)"]=code,["Route String"]="DCT"}},Fields=new(){["Departure Airport (ICAO/IATA)"]=new(FieldIntent.Set,code),["Arrival Airport (ICAO/IATA)"]=new(FieldIntent.Set,code),["Route String"]=new(FieldIntent.Set,"DCT")}};
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        foreach(var mode in new[]{"success","read-failure","cancel","save-failure"})
            await test("Full refresh swaps identity index atomically: "+mode,async()=>
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h);
                var old=Parse(a,ResourceKind.Airports,OldAirport);w.Resources[ResourceKind.Airports].Snapshot.Add(old.Copy());w.Resources[ResourceKind.Airports].Draft.Add(old.Copy());
                using var ct=new CancellationTokenSource();var serialized=w.Serialize();string? saved=null;
                h.Next=(r,token)=>
                {
                    if(r.RequestUri!.AbsolutePath.EndsWith("airports"))return Json(NewAirports);
                    if(mode=="read-failure")throw new TimeoutException();
                    if(mode=="cancel"){ct.Cancel();throw new OperationCanceledException(token);}
                    return Json(Empty);
                };
                try
                {
                    await a.RefreshSnapshotsAsync(staged=>
                    {
                        if(mode=="save-failure")throw new IOException("save failed");
                        saved=staged.Serialize();return Task.CompletedTask;
                    },ct.Token);
                    Check(mode=="success");
                }
                catch(Exception e) when(e is TimeoutException or OperationCanceledException or IOException){Check(mode!="success");}
                var id=(long)a.Plan(Routing("LHR"))[0].Body["departure_airport_id"]!;
                Check(id==(mode=="success"?103:101));
                if(mode=="success")
                {
                    Check(saved==w.Serialize());
                    try{a.Plan(Routing("101"));throw new Exception("Old airport ID survived refresh");}catch(InvalidOperationException){}
                }
                else Check(saved==null && w.Serialize()==serialized);
            });
        foreach(var unknown in new[]{false,true})
            await test("Paused task rotates credentials and resumes in the same VA: "+unknown,async()=>
            {
                var w=new Workspace{AirlineId="7",Mode=RunMode.Online,LocalRouteTimes=true,InstanceUrl=OperationsAdapter.BaseUri.AbsoluteUri};
                var h=new AuditHttp();var a=Api(w,h);var before=Parse(a,ResourceKind.Fleets,Fleet);var after=before.Copy();after.Fields["Name"]="New";
                var item=new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Update,Before=before,After=after,State=unknown?ItemState.Unknown:ItemState.Pending,Fields=new(){["Name"]=new(FieldIntent.Set,"New")}};
                var job=new BatchJob{Mode=RunMode.Online,Items=[item]};w.Jobs.Add(job);
                h.Next=(r,ct)=>new(HttpStatusCode.Unauthorized){Content=new StringContent("{}")};
                await new OperationsBatchExecutor(a,w).ExecuteAsync(job,()=>Task.CompletedTask,default);
                Check(job.StatusCode==JobStatus.Paused && item.State==(unknown?ItemState.Unknown:ItemState.Pending));
                var jobJson=JsonSerializer.Serialize(job);var directory=Path.Combine(Path.GetTempPath(),"vamsys-rotation-"+Guid.NewGuid());var store=new WorkspaceStore(directory);
                store.Save(w);store.UpdateOperationsCredentials(w," updated-client ","new-test-secret");
                var creds=JsonSerializer.Deserialize<OperationsCredentials>(store.LoadSecret(w.Id)!)!;
                Check(creds.ClientId=="updated-client" && creds.Secret=="new-test-secret");
                var restored=store.Load(w.Id);Check(restored.AirlineId=="7" && restored.Mode==RunMode.Offline && restored.LocalRouteTimes && restored.InstanceUrl==w.InstanceUrl && JsonSerializer.Serialize(restored.Jobs[0])==jobJson);
                var tokenHttp=new AuditHttp();tokenHttp.Next=(r,ct)=>
                {
                    Check(r.Content!.ReadAsStringAsync().Result.Contains("client_secret=new-test-secret"));
                    return Json("""{"access_token":"renewed","token_type":"Bearer","expires_in":3600}""");
                };
                var tokens=TokenProvider.ClientCredentials(new HttpClient(tokenHttp),creds);h=new AuditHttp();var wrote=false;
                h.Next=(r,ct)=>
                {
                    Check(r.Headers.Authorization!.Parameter=="renewed");if(r.Method==HttpMethod.Put)wrote=true;
                    var raw=unknown||wrote?Fleet.Replace("Old","New"):Fleet;
                    return Json(r.RequestUri!.AbsolutePath.EndsWith("/fleet")?"{\"data\":["+raw+"],\"meta\":{\"next_cursor_url\":null}}":"{\"data\":"+raw+"}");
                };
                a=new(new OperationsTransport(new HttpClient(h),OperationsAdapter.BaseUri,Guid.NewGuid().ToString(),tokens),restored);
                await foreach(var row in a.ReadAllAsync(ResourceKind.Fleets,default))break;
                Check(a.SessionAirlineId==restored.AirlineId);restored.Mode=RunMode.Online;
                await new OperationsBatchExecutor(a,restored).ExecuteAsync(restored.Jobs[0],()=>Task.CompletedTask,default);
                Check(restored.Jobs[0].Items[0].State==ItemState.Succeeded && h.Methods.Count(m=>m==HttpMethod.Put)==(unknown?0:1));
            });
        await test("Rotated credentials cannot change the bound VA",async()=>
        {
            var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h);
            h.Next=(r,ct)=>Json("{\"data\":["+Fleet.Replace("\"airline_id\":7","\"airline_id\":8")+"],\"meta\":{\"next_cursor_url\":null}}");
            try{await foreach(var row in a.ReadAllAsync(ResourceKind.Fleets,default)){}throw new Exception("Foreign VA accepted");}
            catch(InvalidOperationException e){Check(MessageErrors.Describe(e).Code=="ApiWrongVa");}
            Check(w.AirlineId=="7" && a.SessionAirlineId==null && h.Methods.All(m=>m==HttpMethod.Get));
        });
        await test("Successful permanent delete evicts the resource cache",async()=>
        {
            var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h);
            const string aircraft="""{"id":8,"airline_id":7,"fleet_id":4,"name":"Plane"}""";
            var before=Parse(a,ResourceKind.Aircraft,aircraft);w.Resources[ResourceKind.Aircraft].Snapshot.Add(before.Copy());w.Resources[ResourceKind.Aircraft].Draft.Add(before.Copy());
            var deleted=false;h.Next=(r,ct)=>
            {if(r.Method==HttpMethod.Delete){deleted=true;return new(HttpStatusCode.NoContent);}return deleted?new(HttpStatusCode.NotFound){Content=new StringContent("{}")}:Json("{\"data\":"+aircraft+"}");};
            var item=new ChangeItem{Resource=ResourceKind.Aircraft,Kind=ChangeKind.Delete,Before=before,After=before.Copy()};item.After.Fields["_delete"]="TRUE";
            await new OperationsBatchExecutor(a,w).ExecuteAsync(new(){Items=[item]},()=>Task.CompletedTask,default);Check(item.State==ItemState.Succeeded);
            var calls=h.Methods.Count;
            try{await a.FindAsync(ResourceKind.Aircraft,"8",default);throw new Exception("Deleted aircraft cached");}catch(InvalidOperationException e){Check(MessageErrors.Describe(e).Code=="ApiRefreshFirst");}
            Check(h.Methods.Count==calls);
        });
    }
}
