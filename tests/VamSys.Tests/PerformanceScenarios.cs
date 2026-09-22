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
    const string Fleet="""{"id":4,"airline_id":7,"name":"Fleet","code":"B738","type":"pax"}""";
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
        :"{\"id\":9,\"airline_id\":7,\"departure_id\":101,\"arrival_id\":101,\"flight_number\":"+JsonSerializer.Serialize(flight)+",\"callsign\":\"ACA123\",\"type\":\"scheduled\",\"fleet_ids\":[4]}";
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        foreach(var kind in new[]{ResourceKind.Routes,ResourceKind.Routings})
            await test("Filtered candidates preserve paging and block second-page duplicate: "+kind,async()=>
            {
                var clock=new TestTime();var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h,clock);Parse(a,ResourceKind.Airports,Airport);
                var suffix=kind==ResourceKind.Routes?"_id":"_airport_id";
                h.Next=(r,ct)=>
                {
                    Check(r.Method==HttpMethod.Get);var q=Uri.UnescapeDataString(r.RequestUri!.Query);
                    if(q.Contains("cursor="))return Json(Page(RouteJson(kind)));
                    Check(q.Contains("filter[departure"+suffix+"]=101")&&q.Contains("filter[arrival"+suffix+"]=101")&&!q.Contains("flight_number"));
                    return Json(Page("",OperationsAdapter.BaseUri+OperationsAdapter.Collection(kind)+"?cursor=second"));
                };
                var item=Route(kind);await clock.Drive(new OperationsBatchExecutor(a,w).ExecuteAsync(new(){Items=[item]},()=>Task.CompletedTask,default));
                Check(item.State==ItemState.Failed&&item.Description!.Code=="ApiDuplicate"&&h.Methods.Count==2);
                var uri=OperationsAdapter.FilterUri("routes",new Dictionary<string,string>{["tag"]="A&B + 中"});
                Check(uri.Query.Contains("%26")&&uri.Query.Contains("%2B")&&Uri.UnescapeDataString(uri.Query).Contains("A&B + 中"));
            });
        await test("Complete indexes keep case, aliases and cross-fleet aircraft duplicates",async()=>
        {
            foreach(var kind in new[]{ResourceKind.Airports,ResourceKind.Fleets,ResourceKind.Aircraft})
            {
                var clock=new TestTime();var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h,clock);
                h.Next=(r,ct)=>
                {
                    Check(r.RequestUri!.Query=="");var path=r.RequestUri.AbsolutePath;
                    if(path.EndsWith("airports"))return Json(Page(Airport));
                    if(path.EndsWith("/fleet"))return Json(Page(Fleet+",{\"id\":5,\"airline_id\":7,\"name\":\"Other\"}"));
                    return Json(Page(path.EndsWith("5/aircraft")?"{\"id\":8,\"airline_id\":7,\"fleet_id\":5,\"registration\":\"G-TEST\"}":""));
                };
                var item=kind==ResourceKind.Airports?Create(kind,("ICAO/IATA","lhr"),("Name","London")):kind==ResourceKind.Fleets?Create(kind,("Name","fleet"),("Type Code","B738"),("Type (pax/cargo/...)","pax")):Create(kind,("Fleet ID","4"),("Name","Plane"),("Registration","g-test"));
                await clock.Drive(new OperationsBatchExecutor(a,w).ExecuteAsync(new(){Items=[item]},()=>Task.CompletedTask,default));
                Check(item.Description?.Code=="ApiDuplicate"&&h.Methods.All(m=>m==HttpMethod.Get),kind+": "+item.Message);
                Check(h.Methods.Count==(kind==ResourceKind.Aircraft?3:1));
            }
        });
        await test("Index failure is not published and independent creates reuse a complete index",async()=>
        {
            var clock=new TestTime();var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h,clock);var queries=new CandidateQuerySession();var item=Create(ResourceKind.Fleets,("Name","New"),("Type Code","B738"),("Type (pax/cargo/...)","pax"));
            var fail=true;h.Next=(r,ct)=>{if(r.RequestUri!.Query!=""){if(fail)throw new TimeoutException();return Json(Page(""));}return Json(Page(Fleet,OperationsAdapter.BaseUri+"fleet?cursor=2"));};
            async Task Read(){await foreach(var row in a.ReadDuplicateCandidatesAsync(item,queries,default)){} }
            try{await clock.Drive(Read());throw new Exception("Failure not injected");}catch(TimeoutException){}
            fail=false;await clock.Drive(Read());Check(h.Methods.Count==4);await clock.Drive(Read());Check(h.Methods.Count==4);
        });
        await test("Successful and unknown creates reserve duplicate signatures in the same batch",async()=>
        {
            foreach(var unknown in new[]{false,true})
            {
                var clock=new TestTime();var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h,clock);int lists=0;
                h.Next=(r,ct)=>
                {
                    if(r.Method==HttpMethod.Post){if(unknown)throw new TimeoutException();return Json(Fleet.Replace("Fleet","New"),HttpStatusCode.Created);}
                    if(r.RequestUri!.AbsolutePath.EndsWith("/fleet")){lists++;return Json(Page(Fleet));}
                    return Json("{\"data\":"+Fleet.Replace("Fleet","New")+"}");
                };
                ChangeItem Item(string name)=>Create(ResourceKind.Fleets,("Name",name),("Type Code","B738"),("Type (pax/cargo/...)","pax"));
                var first=Item("New");var second=Item("new");var job=new BatchJob{Items=[first,second]};
                await clock.Drive(new OperationsBatchExecutor(a,w).ExecuteAsync(job,()=>Task.CompletedTask,default));
                Check(first.State==(unknown?ItemState.Unknown:ItemState.Succeeded)&&second.Description?.Code==(unknown?"ApiIdentityPending":"ApiDuplicate") && second.State==(unknown?ItemState.Pending:ItemState.Failed)&&h.Methods.Count(m=>m==HttpMethod.Post)==1&&lists==1);
            }
        });
        await test("Airport dependency queries use OR union, paging, and self-loop deduplication",async()=>
        {
            foreach(var mode in new[]{"departure","arrival","second-page","self","empty","failure"})
            {
                var clock=new TestTime();var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h,clock);var airport=Parse(a,ResourceKind.Airports,Airport);
                var item=new ChangeItem{Resource=ResourceKind.Airports,Kind=ChangeKind.Delete,Before=airport,After=airport.Copy()};
                var raw=RouteJson(ResourceKind.Routes);
                h.Next=(r,ct)=>
                {
                    var q=Uri.UnescapeDataString(r.RequestUri!.Query);Check(q.Contains("departure")^q.Contains("arrival"),"AND dependency query");
                    if(mode=="failure")throw new TimeoutException();
                    if(mode=="second-page"&&q.Contains("cursor"))return Json(Page(raw));
                    if(mode=="second-page"&&q.Contains("departure_id"))return Json(Page("",OperationsAdapter.BaseUri+"routes?filter%5Bdeparture_id%5D=101&cursor=2"));
                    var found=mode=="self"&&r.RequestUri.AbsolutePath.EndsWith("routes") || mode=="departure"&&q.Contains("departure_id") || mode=="arrival"&&q.Contains("arrival_id");
                    return Json(Page(found?raw:""));
                };
                if(mode=="failure")
                {try{await clock.Drive(a.HasDeletionDependenciesAsync(item,default));throw new Exception();}catch(TimeoutException){} }
                else if(mode=="self")
                {var rows=new List<DataRow>();async Task Read(){await foreach(var pair in a.ReadDependencyCandidatesAsync(item,default))rows.Add(pair.Row);}await clock.Drive(Read());Check(rows.Count==1&&h.Methods.Count==4);}
                else {var task=a.HasDeletionDependenciesAsync(item,default);await clock.Drive(task);Check(task.Result==(mode!="empty"));if(mode=="empty")Check(h.Methods.Count==4);}
                Check(h.Methods.All(m=>m==HttpMethod.Get));
            }
        });
        await test("Fleet deletion only reads its aircraft and fleet-filtered routes",async()=>
        {
            var clock=new TestTime();var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h,clock);var fleet=Parse(a,ResourceKind.Fleets,Fleet);
            h.Next=(r,ct)=>
            {if(r.RequestUri!.AbsolutePath.EndsWith("fleet/4/aircraft"))return Json(Page(""));Check(Uri.UnescapeDataString(r.RequestUri.Query)=="?filter[fleet_id]=4");return Json(Page(RouteJson(ResourceKind.Routes)));};
            var task=a.HasDeletionDependenciesAsync(new(){Resource=ResourceKind.Fleets,Kind=ChangeKind.Delete,Before=fleet,After=fleet.Copy()},default);await clock.Drive(task);Check(task.Result&&h.Methods.Count==2);
        });
        await test("Shared virtual clock enforces request spacing and waiting cancellation",async()=>
        {
            var time=new TestTime();var pool=new RequestCoordinator(time);var h=new AuditHttp();var stamps=new List<DateTimeOffset>();h.Next=(r,ct)=>{stamps.Add(time.GetUtcNow());return Json("{}");};
            var a=Transport(h,time,pool);var b=Transport(h,time,pool);async Task Read(){for(var i=0;i<61;i++)using(await (i%2==0?a:b).GetAsync(new(OperationsAdapter.BaseUri,"fleet"),default)){} }
            await time.Drive(Read());Check(stamps.Count==61&&(stamps[^1]-stamps[0]).TotalSeconds>=60);Check(stamps.Zip(stamps.Skip(1)).All(p=>(p.Second-p.First).TotalSeconds>=1));
            using var ct=new CancellationTokenSource();var pending=a.GetAsync(new(OperationsAdapter.BaseUri,"fleet"),ct.Token);ct.Cancel();try{await pending;throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}Check(stamps.Count==61);
        });
        await test("Concurrent clients share one budget and cancel a queued request",async()=>
        {
            var time=new TestTime();var pool=new RequestCoordinator(time);var h=new AuditHttp();var stamps=new List<DateTimeOffset>();
            h.Next=(r,ct)=>{stamps.Add(time.GetUtcNow());return Json("{}");};
            var clients=Enumerable.Range(0,3).Select(_=>Transport(h,time,pool)).ToArray();
            using(await clients[0].GetAsync(new(OperationsAdapter.BaseUri,"fleet"),default)){}
            using var cancel=new CancellationTokenSource();
            var first=clients[0].GetAsync(new(OperationsAdapter.BaseUri,"fleet"),default);
            var queued=clients[1].GetAsync(new(OperationsAdapter.BaseUri,"fleet"),cancel.Token);
            var last=clients[2].GetAsync(new(OperationsAdapter.BaseUri,"fleet"),default);
            cancel.Cancel();try{await queued;throw new Exception("Queued cancellation ignored");}catch(OperationCanceledException){}
            await time.Drive(Task.WhenAll(first,last));first.Result.Dispose();last.Result.Dispose();
            Check(stamps.Count==3 && stamps.Zip(stamps.Skip(1)).All(p=>(p.Second-p.First).TotalSeconds>=1));
        });
        await test("Virtual Retry-After, server budget and read backoff preserve production policy",async()=>
        {
            foreach(var mode in new[]{"delta","date","budget","network","500"})
            {
                var time=new TestTime();var h=new AuditHttp();var start=time.GetUtcNow();int calls=0;var a=Transport(h,time);using var metrics=PerformanceRun.Begin(Guid.NewGuid(),Guid.NewGuid());
                h.Next=(r,ct)=>
                {
                    if(++calls>1)return Json("{}");if(mode=="network")throw new HttpRequestException();
                    if(mode=="500")return Json("{}",HttpStatusCode.InternalServerError);
                    var response=Json("{}",HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter=mode=="date"?new(time.GetUtcNow().AddSeconds(5)):new(TimeSpan.FromSeconds(5));
                    if(mode=="budget")response.Headers.Add("X-RateLimit-Remaining","0");return response;
                };
                var task=a.GetAsync(new(OperationsAdapter.BaseUri,"fleet"),default);await time.Drive(task);task.Result.Dispose();
                Check(calls==2&&metrics.Retries==1&&(time.GetUtcNow()-start).TotalSeconds>=(mode=="budget"?60:mode is "network" or "500"?1:5));
                Check(metrics.RateWaitMs+metrics.BackoffMs>=1000);
            }
        });
        await test("Token expiry uses injected time and metrics never expose credentials",async()=>
        {
            var time=new TestTime();var h=new AuditHttp();h.Next=(r,ct)=>Json("""{"access_token":"PRIVATE_TOKEN","token_type":"Bearer","expires_in":120}""");
            using var metrics=PerformanceRun.Begin(Guid.NewGuid(),Guid.NewGuid());var provider=TokenProvider.ClientCredentials(new HttpClient(h),new("PRIVATE_ID","PRIVATE_SECRET"),time);
            await provider.GetAsync(default);time.Advance(TimeSpan.FromSeconds(59));await provider.GetAsync(default);Check(h.Methods.Count==1);time.Advance(TimeSpan.FromSeconds(2));await provider.GetAsync(default);Check(h.Methods.Count==2);
            metrics.MarkCancellation();metrics.Finish("Canceled");Check(metrics.CancellationMs.HasValue);
            var output=JsonSerializer.Serialize(metrics);Check(!output.Contains("PRIVATE")&&metrics.Requests["OAuth/Other/POST"]==2);
            var file=Path.GetTempFileName();try{Check(!metrics.TryWrite(file));}finally{File.Delete(file);}
        });
        await test("Dense candidate pairs retain all pages instead of assuming one GET",async()=>
        {
            var time=new TestTime();var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var api=Api(w,h,time);Parse(api,ResourceKind.Airports,Airport);int pages=0;
            h.Next=(r,ct)=>
            {
                if(r.Method==HttpMethod.Post)return Json(RouteJson(ResourceKind.Routes),HttpStatusCode.Created);
                if(r.RequestUri!.AbsolutePath.EndsWith("/9"))return Json("{\"data\":"+RouteJson(ResourceKind.Routes)+"}");
                pages++;return Json(Page(RouteJson(ResourceKind.Routes,"ZZ999"),pages<3?OperationsAdapter.BaseUri+"routes?cursor="+pages:null));
            };
            var item=Route(ResourceKind.Routes);await time.Drive(new OperationsBatchExecutor(api,w).ExecuteAsync(new(){Items=[item]},()=>Task.CompletedTask,default));
            Check(item.State==ItemState.Succeeded&&pages==3&&h.Methods.Count(m=>m==HttpMethod.Post)==1);
        });
        await test("Save metrics count failures and JSON bytes without adding business writes",()=>
        {
            var folder=Path.Combine(Path.GetTempPath(),"vamsys-metrics-"+Guid.NewGuid());var store=new WorkspaceStore(folder);var workspace=new Workspace();
            using var run=PerformanceRun.Begin(workspace.Id,Guid.NewGuid());store.Save(workspace);
            using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(folder,"workspaces.db")}.ToString()))
            {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="CREATE TRIGGER fail_metrics BEFORE UPDATE ON workspaces BEGIN SELECT RAISE(ABORT,'injected'); END;";cmd.ExecuteNonQuery();}
            try{store.Save(workspace);throw new Exception("Save failure not injected");}catch(Microsoft.Data.Sqlite.SqliteException){}
            Check(run.SaveAttempts==2&&run.SaveSuccesses==1&&run.SaveFailures==1&&run.JsonBytes>0 && run.Checkpoints["Full"]==2);
            run.Finish("Failed");Check(run.TryWrite(Path.Combine(folder,"reports"))&&run.SaveAttempts==2);return Task.CompletedTask;
        });
        await test("20,000 existing routes and 100 sparse candidates require 100 filtered GETs",async()=>
        {var report=await RouteBenchmark();Check(report.Gets==100&&report.Posts==100&&report.Readbacks==100);Console.WriteLine($"  20k/100: duplicate GET={report.Gets}, POST={report.Posts}, read-back GET={report.Readbacks}, virtual seconds={report.Seconds}");});
    }
    public sealed record QueryBenchmark(int Existing,int New,int Gets,int Posts,int Readbacks,double Seconds);
    static async Task<QueryBenchmark> RouteBenchmark()
    {
        var time=new TestTime();var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h,time);Parse(a,ResourceKind.Airports,Airport);
        // Existing routes belong to other airport pairs; the requested pair has sparse candidates.
        var existing=Enumerable.Range(0,20000).Select(i=>new {departure_id=1000+i,arrival_id=30000+i}).ToArray();
        int gets=0,posts=0,readbacks=0;var created=new Dictionary<string,string>();
        h.Next=(r,ct)=>
        {
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
        return new(existing.Length,100,gets,posts,readbacks,(time.GetUtcNow()-DateTimeOffset.UnixEpoch).TotalSeconds);
    }
    public static async Task Benchmark(string directory)
    {
        Directory.CreateDirectory(directory);var queries=await RouteBenchmark();var saves=new List<object>();
        foreach(var undo in new[]{0,3})
        {
            var w=new Workspace{Name="Synthetic performance workspace",AirlineId="7"};var data=w.Resources[ResourceKind.Routes];
            data.Snapshot=Enumerable.Range(1,20000).Select(i=>new DataRow{Identity=new(i.ToString(),"7"),RawApiJson=RouteJson(ResourceKind.Routes,"AC123"),Fields=new(){["ID"]=i.ToString(),["Departure Airport (ICAO/IATA)"]="EGLL",["Arrival Airport (ICAO/IATA)"]="EGLL",["Flight Number"]="AC123",["Remarks"]=new string('x',100)}}).ToList();
            data.Draft=data.Snapshot.Select(r=>r.Copy()).ToList();for(var n=0;n<undo;n++)data.Checkpoint();
            var store=new WorkspaceStore(Path.Combine(directory,"storage-"+undo));using var run=PerformanceRun.Begin(w.Id,Guid.NewGuid());store.Save(w);run.Finish("Completed");run.TryWrite(directory);
            saves.Add(new{UndoSnapshots=undo,Rows=20000,run.SaveAttempts,run.SaveSuccesses,run.SaveFailures,run.JsonBytes,run.SerializationMs,run.SqliteMs});
        }
        var json=JsonSerializer.Serialize(new{Queries=queries,Index=await CandidateIndexScenarios.Benchmark(),Saves=saves},new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(directory,"benchmark.json"),json);Console.WriteLine(json);
    }
}
