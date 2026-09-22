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
    const string Airport="""{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"Hub"}""";
    static DataRow Row(params (string,string)[] fields)=>new(){Fields=fields.ToDictionary(p=>p.Item1,p=>p.Item2)};
    static DataRow NewFleet(string name)=>Row(("Name",name),("Type Code","B738"),("Type (pax/cargo/...)","pax"));
    static ChangeItem Create(ResourceKind kind,DataRow row)=>new(){Resource=kind,Kind=ChangeKind.Create,After=row,Fields=row.Fields.ToDictionary(p=>p.Key,p=>new FieldChange(FieldIntent.Set,p.Value))};
    static DataRow Parse(OperationsAdapter api,ResourceKind kind,string raw){using var doc=JsonDocument.Parse(raw);return api.FromJson(kind,doc.RootElement);}
    static OperationsAdapter Api(Workspace workspace,AuditHttp http,TestTime clock)=>new(new OperationsTransport(new HttpClient(http),OperationsAdapter.BaseUri,"indexes",new TokenProvider(_=>Task.FromResult(new AccessToken("test",clock.GetUtcNow().AddDays(1))),clock),new RequestCoordinator(clock)),workspace);
    static async Task<List<DataRow>> Candidates(OperationsAdapter api,ChangeItem item,CandidateQuerySession session)
    {var result=new List<DataRow>();await foreach(var row in api.ReadDuplicateCandidatesAsync(item,session,default)) result.Add(row);return result;}
    static async Task RunJob(OperationsAdapter api,Workspace w,BatchJob job,TestTime clock,CancellationToken ct=default)
    {await clock.Drive(new OperationsBatchExecutor(api,w).ExecuteAsync(job,()=>Task.CompletedTask,ct));}
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("Planner create-update-create synchronizes names without another full list",async()=>
        {
            foreach(var candidate in new[]{"Renamed","Old"})
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var clock=new TestTime();var api=Api(w,h,clock);
                var data=w.Resources[ResourceKind.Fleets];data.Draft.Add(NewFleet("Seed"));
                SnapshotMerger.Merge(ResourceKind.Fleets,data,[Parse(api,ResourceKind.Fleets,Fleet(4,"Old"))]);
                data.Draft.Single(r=>r.Identity?.RemoteId=="4").Fields["Name"]="Renamed";data.Draft.Add(NewFleet(candidate));
                var remote=new Dictionary<int,string>{{4,"Old"}};int next=10,lists=0,posts=0;
                h.Next=(r,ct)=>{
                    var path=r.RequestUri!.AbsolutePath;
                    if(r.Method==HttpMethod.Post){posts++;using var body=JsonDocument.Parse(r.Content!.ReadAsStringAsync(ct).Result);int id=next++;remote[id]=body.RootElement.GetProperty("name").GetString()!;return Json(Fleet(id,remote[id]),HttpStatusCode.Created);}
                    if(path.EndsWith("/fleet")){lists++;return Json(Page(remote.Select(p=>Fleet(p.Key,p.Value))));}
                    int row=int.Parse(path.Split('/')[^1]);
                    if(r.Method==HttpMethod.Put){using var body=JsonDocument.Parse(r.Content!.ReadAsStringAsync(ct).Result);remote[row]=body.RootElement.GetProperty("name").GetString()!;}
                    return Json("{\"data\":"+Fleet(row,remote[row])+"}");
                };
                var job=new BatchJob{Items=new ChangePlanner().PlanWorkspace(w)};
                Check(job.Items.Select(i=>i.Kind).SequenceEqual(new[]{ChangeKind.Create,ChangeKind.Update,ChangeKind.Create}));
                await RunJob(api,w,job,clock);
                Check(job.Items[0].State==ItemState.Succeeded && job.Items[1].State==ItemState.Succeeded);
                Check(job.Items[2].State==(candidate=="Old"?ItemState.Succeeded:ItemState.Failed),job.Items[2].Message);
                Check(posts==(candidate=="Old"?2:1) && lists==1 && remote.Values.Count(v=>v=="Renamed")==1);
            }
        });
        await test("Unknown rename reserves both names across restart and releases after recovery",async()=>
        {
            foreach(var applied in new[]{false,true})
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var clock=new TestTime();var api=Api(w,h,clock);
                var data=w.Resources[ResourceKind.Fleets];data.Draft.Add(NewFleet("Seed"));
                SnapshotMerger.Merge(ResourceKind.Fleets,data,[Parse(api,ResourceKind.Fleets,Fleet(4,"Old"))]);
                data.Draft.Single(r=>r.Identity?.RemoteId=="4").Fields["Name"]="Renamed";
                data.Draft.Add(NewFleet("Old"));data.Draft.Add(NewFleet("Renamed"));data.Draft.Add(NewFleet("Independent"));
                var remote=new Dictionary<int,string>{{4,"Old"}};int next=10,posts=0,puts=0;bool fault=true;
                h.Next=(r,ct)=>{
                    if(r.Method==HttpMethod.Post){posts++;using var body=JsonDocument.Parse(r.Content!.ReadAsStringAsync(ct).Result);int id=next++;remote[id]=body.RootElement.GetProperty("name").GetString()!;return Json(Fleet(id,remote[id]),HttpStatusCode.Created);}
                    if(r.RequestUri!.AbsolutePath.EndsWith("/fleet"))return Json(Page(remote.Select(p=>Fleet(p.Key,p.Value))));
                    int idRow=int.Parse(r.RequestUri.AbsolutePath.Split('/')[^1]);
                    if(r.Method==HttpMethod.Put){puts++;if(applied)remote[idRow]="Renamed";throw new TimeoutException();}
                    if(!fault && idRow==4)remote[4]="Renamed"; // user resolves the uncertain update externally
                    return Json("{\"data\":"+Fleet(idRow,remote[idRow])+"}");
                };
                var job=new BatchJob{Items=new ChangePlanner().PlanWorkspace(w)};w.Jobs.Add(job);
                await RunJob(api,w,job,clock);
                Check(job.Items[1].State==ItemState.Unknown && job.Items[2].State==ItemState.Pending && job.Items[3].State==ItemState.Pending);
                Check(job.Items[4].State==ItemState.Succeeded && posts==2 && puts==1);
                w=Workspace.Deserialize(w.Serialize());job=w.Jobs.Single();api=Api(w,h,clock);
                if(!applied){await RunJob(api,w,job,clock);Check(posts==2&&puts==1&&job.Items[1].State==ItemState.Unknown);}
                fault=false;await RunJob(api,w,job,clock);
                Check(job.Items[1].State==ItemState.Succeeded && job.Items[2].State==ItemState.Succeeded && job.Items[3].State==ItemState.Failed);
                Check(posts==3&&puts==1);
            }
        });
        await test("Unknown airport aliases block later creation with or without IDs and survive restart",async()=>
        {
            foreach(var reverse in new[]{false,true})
            foreach(var returnedId in new[]{false,true})
            foreach(var visible in new[]{false,true})
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var clock=new TestTime();var api=Api(w,h,clock);
                Parse(api,ResourceKind.Fleets,Fleet(4,"Connected"));
                w.Resources[ResourceKind.Airports].Draft.Add(Row(("ICAO/IATA",reverse?"EGLL":"LHR"),("Name","Hub")));
                w.Resources[ResourceKind.Airports].Draft.Add(Row(("ICAO/IATA",reverse?"LHR":"EGLL"),("Name","Hub")));
                w.Resources[ResourceKind.Fleets].Draft.Add(NewFleet("Independent"));
                int posts=0;bool fault=true,created=false;
                h.Next=(r,ct)=>{
                    var path=r.RequestUri!.AbsolutePath;
                    if(path.Contains("/fleet"))return r.Method==HttpMethod.Post?Json(Fleet(10,"Independent"),HttpStatusCode.Created):path.EndsWith("/fleet")?Json(Page([Fleet(4,"Connected")])):Json("{\"data\":"+Fleet(10,"Independent")+"}");
                    if(r.Method==HttpMethod.Post){posts++;created=true;if(!returnedId)throw new TimeoutException();return Json(Airport,HttpStatusCode.Created);}
                    if(r.Method==HttpMethod.Put)return Json("{}");
                    if(path.EndsWith("/airports"))return Json(Page(visible&&created?[Airport]:[]));
                    if(fault)throw new TimeoutException();
                    return Json("{\"data\":"+(path.EndsWith("/102")?Airport.Replace("101","102").Replace("EGLL","KJFK").Replace("LHR","JFK"):Airport)+"}");
                };
                var job=new BatchJob{Items=new ChangePlanner().PlanWorkspace(w)};w.Jobs.Add(job);
                await RunJob(api,w,job,clock);
                Check(posts==1&&job.Items[0].State==ItemState.Unknown&&job.Items[1].State==ItemState.Pending&&job.Items[2].State==ItemState.Succeeded);
                Check(job.Items[1].Description?.Code=="ApiAirportCreatePending");
                w=Workspace.Deserialize(w.Serialize());job=w.Jobs.Single();api=Api(w,h,clock);
                await RunJob(api,w,job,clock);Check(posts==1&&job.Items[0].State==ItemState.Unknown&&job.Items[1].State==ItemState.Pending);
                fault=false;
                if(!returnedId){
                    job.Items[0].ProposeRecoveryId("102");await RunJob(api,w,job,clock);
                    Check(posts==1&&job.Items[0].State==ItemState.Unknown&&job.Items[0].After.Identity==null&&job.Items[1].State==ItemState.Pending);
                    job.Items[0].ProposeRecoveryId("101");
                }
                await RunJob(api,w,job,clock);
                Check(posts==1&&job.Items[0].State==ItemState.Succeeded&&job.Items[1].State==ItemState.Failed,job.Items[0].Message+" / "+job.Items[1].Message);
            }
        });
        await test("Unknown recovery faults never release creation reservations or repeat writes",async()=>
        {
            foreach(var mode in new[]{"timeout","cancel","401","403","429","500","json"})
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var clock=new TestTime();var api=Api(w,h,clock);
                var unknown=Create(ResourceKind.Airports,Row(("ICAO/IATA","LHR"),("Name","Hub")));unknown.State=ItemState.Unknown;unknown.ProposeRecoveryId("101");
                var next=Create(ResourceKind.Airports,Row(("ICAO/IATA","EGLL"),("Name","Hub")));
                var job=new BatchJob{Items=[unknown,next]};w.Jobs.Add(job);
                h.Next=(r,ct)=>mode switch{
                    "timeout"=>throw new TimeoutException(),"cancel"=>throw new OperationCanceledException(),
                    "json"=>Json("invalid"),_=>Json("{}",(HttpStatusCode)int.Parse(mode))
                };
                for(int n=0;n<2;n++){
                    await RunJob(api,w,job,clock);Check(unknown.State==ItemState.Unknown && next.State==ItemState.Pending);
                    Check(h.Methods.All(m=>m==HttpMethod.Get));
                    if(mode is "401" or "403")Check(job.StatusCode==JobStatus.Paused);
                    w=Workspace.Deserialize(w.Serialize());job=w.Jobs.Single();unknown=job.Items[0];next=job.Items[1];api=Api(w,h,clock);
                }
                using var canceled=new CancellationTokenSource();canceled.Cancel();int calls=h.Methods.Count;
                await RunJob(api,w,job,clock,canceled.Token);Check(h.Methods.Count==calls&&unknown.State==ItemState.Unknown&&job.StatusCode==JobStatus.Canceled);
            }
        });
        await test("Confirmed index preserves colliding entities and releases only changed or deleted IDs",async()=>
        {
            var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var clock=new TestTime();var api=Api(w,h,clock);var session=new CandidateQuerySession();
            h.Next=(r,ct)=>Json(Page([Fleet(4,"Same"),Fleet(5,"SAME")]));
            var query=Create(ResourceKind.Fleets,NewFleet("same"));
            var read=Candidates(api,query,session);await clock.Drive(read);Check(read.Result.Count==2);
            var first=read.Result.Single(r=>r.Identity!.RemoteId=="4");
            var update=new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Update,Before=first,After=first.Copy()};update.After.Fields["Name"]="Renamed";
            api.ReserveCandidate(update,session);Check(api.CandidateBlock(query,session)=="ApiIdentityPending");
            api.ConfirmCandidate(update,Parse(api,ResourceKind.Fleets,Fleet(4,"Renamed")),session);
            Check((await Candidates(api,query,session)).Single().Identity!.RemoteId=="5");
            var deletion=new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Delete,Before=read.Result.Single(r=>r.Identity!.RemoteId=="5"),After=read.Result[1].Copy(),RemoteId="5"};
            api.ReserveCandidate(deletion,session);Check(api.CandidateBlock(query,session)=="ApiIdentityPending");
            api.ConfirmCandidate(deletion,null,session);Check((await Candidates(api,query,session)).Count==0);
            var renamed=Create(ResourceKind.Fleets,NewFleet("RENAMED"));Check((await Candidates(api,renamed,session)).Count==1&&h.Methods.Count==1);
            // Move a confirmed creation's reservation too; the original name is now free.
            var created=Create(ResourceKind.Fleets,NewFleet("Fresh"));
            api.ConfirmCandidate(created,Parse(api,ResourceKind.Fleets,Fleet(6,"Fresh")),session);
            api.ConfirmCandidate(new(){Resource=ResourceKind.Fleets,Kind=ChangeKind.Update,RemoteId="6"},Parse(api,ResourceKind.Fleets,Fleet(6,"Newer")),session);
            Check((await Candidates(api,created,session)).Count==0);
        });
        await test("Signatures preserve airport aliases, route endpoints and workspace isolation",async()=>
        {
            var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var clock=new TestTime();var api=Api(w,h,clock);var session=new CandidateQuerySession();
            var airport=Parse(api,ResourceKind.Airports,Airport);w.Resources[ResourceKind.Airports].Snapshot.Add(airport);
            h.Next=(r,ct)=>Json(Page([Airport]));
            foreach(var code in new[]{"lhr","egll"}){
                var read=Candidates(api,Create(ResourceKind.Airports,Row(("ICAO/IATA",code))),session);await clock.Drive(read);Check(read.Result.Count==1);
            }
            foreach(var kind in new[]{ResourceKind.Routes,ResourceKind.Routings}){
                var field=kind==ResourceKind.Routes?"Flight Number":"Route String";var suffix=kind==ResourceKind.Routes?"_id":"_airport_id";
                var raw="{\"id\":9,\"airline_id\":7,\"departure"+suffix+"\":101,\"arrival"+suffix+"\":101,\""+(kind==ResourceKind.Routes?"flight_number":"route")+"\":\"OLD\"}";
                var before=Parse(api,kind,raw);var after=before.Copy();after.Fields[field]="NEW";
                var unknown=new ChangeItem{Resource=kind,Kind=ChangeKind.Update,Before=before,After=after};api.ReserveCandidate(unknown,session);
                foreach(var code in new[]{"LHR","egll","101"})
                foreach(var value in new[]{"old","new"}){
                    var query=Create(kind,Row(("Departure Airport (ICAO/IATA)",code),("Arrival Airport (ICAO/IATA)","101"),(field,value)));
                    Check(api.CandidateBlock(query,session)=="ApiIdentityPending");
                }
                api.ConfirmCandidate(unknown,Parse(api,kind,raw.Replace("OLD","NEW")),session);
            }
            var other=Api(new(){AirlineId="8"},h,clock);
            try{await Candidates(other,Create(ResourceKind.Airports,Row(("ICAO/IATA","LHR"))),session);throw new Exception("Cross-VA index accepted");}
            catch(InvalidOperationException e){Check(MessageErrors.Describe(e).Code=="ApiWrongVa");}
            Check(h.Methods.Count==1);
        });
        await test("Index building failure is atomic and aircraft registration spans fleets",async()=>
        {
            var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var clock=new TestTime();var api=Api(w,h,clock);var session=new CandidateQuerySession();
            string Aircraft(int id,int fleet)=>JsonSerializer.Serialize(new{id,airline_id=7,fleet_id=fleet,registration="G-TEST",name="Plane"});
            bool fail=true;h.Next=(r,ct)=>{
                if(r.RequestUri!.AbsolutePath.EndsWith("/fleet"))return Json(Page([Fleet(4,"First"),Fleet(5,"Second")]));
                if(r.RequestUri.AbsolutePath.Contains("/5/")&&fail)throw new TimeoutException();
                return Json(Page([Aircraft(r.RequestUri.AbsolutePath.Contains("/4/")?10:11,r.RequestUri.AbsolutePath.Contains("/4/")?4:5)]));
            };
            var query=Create(ResourceKind.Aircraft,Row(("Fleet ID","4"),("Registration","g-test")));
            using var metrics=PerformanceRun.Begin(w.Id,Guid.NewGuid());
            try{await clock.Drive(Candidates(api,query,session));throw new Exception("Failure missing");}catch(TimeoutException){}
            Check(metrics.IndexBuilds==0);
            fail=false;var read=Candidates(api,query,session);await clock.Drive(read);
            Check(read.Result.Count==2&&metrics.IndexBuilds==1);
            int count=h.Methods.Count;Check((await Candidates(api,query,session)).Count==2&&h.Methods.Count==count);
        });
        await test("New pending reasons localize and legacy task fields still deserialize",()=>
        {
            var zh=new LocalizationService();var en=new LocalizationService();en.SetLanguage("en-US");
            foreach(var key in new[]{"ApiAirportCreatePending","ApiIdentityPending"}){
                var descriptor=Messages.Define(key);Check(zh.Format(descriptor)!=en.Format(descriptor));
                var item=JsonSerializer.Deserialize<ChangeItem>(JsonSerializer.Serialize(new ChangeItem{State=ItemState.Pending,Description=descriptor}))!;
                Check(item.State==ItemState.Pending&&en.Format(item.Description!)==en.Format(descriptor));
            }
            var old=JsonSerializer.Deserialize<BatchJob>("""{"Status":"Old external status","Items":[{"State":4,"Message":"Old diagnostic"}]}""")!;
            Check(old.Items.Single().State==ItemState.Unknown&&old.Items.Single().Message=="Old diagnostic");
            return Task.CompletedTask;
        });
        await test("Legacy completed rows need no raw snapshot and recovery starts a fresh index",async()=>
        {
            var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var clock=new TestTime();var api=Api(w,h,clock);
            var old=Row(("ICAO/IATA","LHR"));old.Identity=new("101","7");
            w.Resources[ResourceKind.Airports].Snapshot.Add(old);
            var completed=Create(ResourceKind.Airports,old);completed.State=ItemState.Succeeded;completed.CreateCompleted=true;completed.RemoteId="101";
            var next=Create(ResourceKind.Fleets,NewFleet("New"));
            var job=new BatchJob{Items=[completed,next]};
            h.Next=(r,ct)=>r.Method==HttpMethod.Post?Json(Fleet(10,"New"),HttpStatusCode.Created):r.RequestUri!.AbsolutePath.EndsWith("/fleet")?Json(Page([Fleet(4,"Connected")])):Json("{\"data\":"+Fleet(10,"New")+"}");
            await RunJob(api,w,job,clock);Check(next.State==ItemState.Succeeded&&h.Methods.Count(m=>m==HttpMethod.Post)==1);
            // A missing legacy identity cannot silently free an unknown deletion's signature.
            var unknown=new ChangeItem{Resource=ResourceKind.Airports,Kind=ChangeKind.Delete,Before=old,After=old.Copy(),State=ItemState.Unknown};
            var session=new CandidateQuerySession();api.SeedCandidates(new(){Items=[unknown]},session);
            Check(api.CandidateBlock(Create(ResourceKind.Airports,Row(("ICAO/IATA","EGLL"))),session)=="ApiIdentityPending");
            // A legacy response with no usable aliases is opaque too, not an empty reservation.
            unknown.Before.RawApiJson="{}";
            session=new CandidateQuerySession();api.SeedCandidates(new(){Items=[unknown]},session);
            Check(api.CandidateBlock(Create(ResourceKind.Airports,Row(("ICAO/IATA","EGLL"))),session)=="ApiIdentityPending");
        });

        await test("20k index queries and confirmed renames use one build and bounded candidates",async()=>{
            var report=await Benchmark();Check(report.Builds==1&&report.Lists==1&&report.Candidates==200&&report.Queries==400);
            Console.WriteLine("  Index: "+JsonSerializer.Serialize(report));
        });
    }
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
