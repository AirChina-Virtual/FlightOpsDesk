using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

// Regressions for the OpenAPI contract audit: documented response shapes, paging, throttling and field formats.
static class ContractScenarios
{
    const string London="""{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"Hub"}""";
    const string Gatwick="""{"id":102,"airline_id":7,"icao":"EGKK","iata":"LGW","name":"Hub"}""";
    const string Fleet="""{"id":4,"airline_id":7,"name":"Fleet","code":"B738","type":"pax"}""";
    // RouteData in the specification has no airline_id.
    const string SpecRoute="""{"id":9,"type":"scheduled","departure_id":101,"arrival_id":102,"callsign":"ACA123","flight_number":"AC123","fleet_ids":[4]}""";
    static void Check(bool ok,string why="Contract assertion failed"){if(!ok)throw new Exception(why);}
    static HttpResponseMessage Json(string s,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(s)};
    static string Page(string rows)=>"{\"data\":["+rows+"],\"meta\":{\"next_cursor_url\":null}}";
    static OperationsTransport Transport(AuditHttp h,TestTime time)=>new(new HttpClient(h),OperationsAdapter.BaseUri,Guid.NewGuid().ToString(),new TokenProvider(_=>Task.FromResult(new AccessToken("test",time.GetUtcNow().AddHours(1))),time),new(time));
    static DataRow Parse(OperationsAdapter a,ResourceKind k,string raw){using var d=JsonDocument.Parse(raw);return a.FromJson(k,d.RootElement);}
    static ChangeItem Create(ResourceKind k,params (string,string)[] fields)
    {var row=new DataRow{Fields=fields.ToDictionary(p=>p.Item1,p=>p.Item2)};return new(){Resource=k,Kind=ChangeKind.Create,After=row,Fields=row.Fields.ToDictionary(p=>p.Key,p=>new FieldChange(FieldIntent.Set,p.Value))};}
    static ChangeItem Route(params (string,string)[] extra)=>Create(ResourceKind.Routes,new[]{("Departure Airport (ICAO/IATA)","LHR"),("Arrival Airport (ICAO/IATA)","LGW"),("Type","scheduled"),("Callsign","ACA123"),("Flight Number","AC123"),("Fleet IDs","4")}.Concat(extra).ToArray());
    static OperationsAdapter WithAirports(Workspace w,OperationsTransport t)
    {var a=new OperationsAdapter(t,w);foreach(var raw in new[]{London,Gatwick})w.Resources[ResourceKind.Airports].Snapshot.Add(Parse(a,ResourceKind.Airports,raw));return a;}
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("Spec-shaped routes without airline_id refresh under the connected VA with larger pages",async()=>
        {
            var clock=new TestTime();var w=new Workspace();var h=new AuditHttp();var a=new OperationsAdapter(Transport(h,clock),w);
            h.Next=(r,ct)=>
            {
                Check(Uri.UnescapeDataString(r.RequestUri!.Query).Contains("page[size]=100"),"Page size missing");
                var path=r.RequestUri.AbsolutePath;
                return Json(Page(path.EndsWith("/airports")?London+","+Gatwick:path.EndsWith("/fleet")?Fleet:path.EndsWith("/routes")?SpecRoute:""));
            };
            await clock.Drive(a.RefreshSnapshotsAsync(_=>Task.CompletedTask,default));
            var route=w.Resources[ResourceKind.Routes].Snapshot.Single();
            Check(route.Identity!.ConnectionId=="7"&&route.Get("Departure Airport (ICAO/IATA)")=="EGLL"&&route.Get("Arrival Airport (ICAO/IATA)")=="EGKK");
            Check(a.SessionAirlineId=="7"&&h.Methods.All(m=>m==HttpMethod.Get));
        });
        await test("Only routes may omit airline_id, and never before the VA is known",()=>
        {
            var a=new OperationsAdapter(Transport(new(),new()),new Workspace());
            foreach(var (kind,raw) in new[]{(ResourceKind.Routes,SpecRoute),(ResourceKind.Fleets,"""{"id":4,"name":"Fleet"}""")})
            {try{Parse(a,kind,raw);throw new Exception(kind+" accepted without airline");}catch(InvalidOperationException){} }
            var known=new OperationsAdapter(Transport(new(),new()),new Workspace{AirlineId="7"});
            Check(Parse(known,ResourceKind.Routes,SpecRoute).Identity!.ConnectionId=="7"&&known.SessionAirlineId==null);
            return Task.CompletedTask;
        });
        await test("Created route ID is saved before a full parse can fail",async()=>
        {
            var clock=new TestTime();var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=WithAirports(w,Transport(h,clock));
            var item=Route();var job=new BatchJob{Items=[item]};w.Jobs.Add(job);
            h.Next=(r,ct)=>r.Method==HttpMethod.Post?Json("""{"id":9}""",HttpStatusCode.Created):Json(Page(""));
            string saved="";await clock.Drive(new OperationsBatchExecutor(a,w).ExecuteAsync(job,()=>{saved=w.Serialize();return Task.CompletedTask;},default));
            Check(item.State==ItemState.Unknown&&item.RemoteId=="9"&&item.CreateCompleted,"Created ID lost: "+item.State);
            w=Workspace.Deserialize(saved);job=w.Jobs.Single();item=job.Items.Single();Check(item.RemoteId=="9");
            h.Next=(r,ct)=>{Check(r.Method==HttpMethod.Get&&r.RequestUri!.AbsolutePath.EndsWith("routes/9"));return Json("{\"data\":"+SpecRoute+"}");};
            await clock.Drive(new OperationsBatchExecutor(WithAirports(w,Transport(h,clock)),w).ExecuteAsync(job,()=>Task.CompletedTask,default));
            Check(item.State==ItemState.Succeeded&&h.Methods.Count(m=>m==HttpMethod.Post)==1);
        });
        await test("Throttled writes return to Pending and succeed on resume",async()=>
        {
            var clock=new TestTime();var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=new OperationsAdapter(Transport(h,clock),w);Parse(a,ResourceKind.Fleets,Fleet);
            var item=Create(ResourceKind.Fleets,("Name","New"),("Type Code","B738"),("Type (pax/cargo/...)","pax"));var job=new BatchJob{Items=[item]};
            var throttle=true;var created=Fleet.Replace("\"id\":4","\"id\":5").Replace("\"Fleet\"","\"New\"");
            h.Next=(r,ct)=>
            {
                if(r.Method==HttpMethod.Post)
                {if(!throttle)return Json(created,HttpStatusCode.Created);var limited=Json("{}",HttpStatusCode.TooManyRequests);limited.Headers.RetryAfter=new(TimeSpan.FromSeconds(5));return limited;}
                return Json(r.RequestUri!.AbsolutePath.EndsWith("fleet/5")?"{\"data\":"+created+"}":Page(Fleet));
            };
            await clock.Drive(new OperationsBatchExecutor(a,w).ExecuteAsync(job,()=>Task.CompletedTask,default));
            Check(item.State==ItemState.Pending&&item.Description?.Code=="ApiRateLimited"&&!item.CreateCompleted,"429 state: "+item.State);
            Check(item.ApiSteps.All(s=>s.State==BatchStepState.Prepared));
            throttle=false;await clock.Drive(new OperationsBatchExecutor(a,w).ExecuteAsync(job,()=>Task.CompletedTask,default));
            Check(item.State==ItemState.Succeeded&&item.RemoteId=="5"&&h.Methods.Count(m=>m==HttpMethod.Post)==2);
        });
        await test("Rejected page size falls back to the documented default",async()=>
        {
            var clock=new TestTime();var h=new AuditHttp();var sizes=new List<bool>();
            h.Next=(r,ct)=>{var sized=Uri.UnescapeDataString(r.RequestUri!.Query).Contains("page[size]");sizes.Add(sized);return sized?Json("{}",HttpStatusCode.UnprocessableEntity):Json(Page(Fleet));};
            var rows=0;async Task Read(){await foreach(var _ in Transport(h,clock).ReadPagesAsync(new(OperationsAdapter.BaseUri,"fleet"),default))rows++;}
            await clock.Drive(Read());Check(rows==1&&sizes.SequenceEqual([true,false]));
        });
        await test("Route fields follow StoreRouteRequest: jumpseat identity, cost index and ISO dates",()=>
        {
            var w=new Workspace();var row=new DataRow{Fields=new(){["Type"]="jumpseat",["Departure Airport (ICAO/IATA)"]="EGLL",["Arrival Airport (ICAO/IATA)"]="EGKK",["Cost Index"]="1000"}};
            w.Resources[ResourceKind.Routes].Draft.Add(row);
            var issues=Schemas.Validate(w,ResourceKind.Routes).Select(i=>i.Field).ToHashSet();
            Check(issues.Contains("Callsign")&&issues.Contains("Flight Number")&&issues.Contains("Cost Index")&&!issues.Contains("Fleet IDs"));
            foreach(var (value,ok) in new[]{("","true"),("AUTO","true"),("0","true"),("999","true"),("auto","false"),("1000","false"),("-1","false"),("5.5","false")})
                Check(Schemas.ValidCostIndex(value)==(ok=="true"),"Cost index "+value);
            var a=WithAirports(new Workspace{AirlineId="7"},Transport(new(),new()));
            var body=a.Plan(Route(("Start Date","2025-01-01 00:00:00"),("Cost Index","AUTO")))[0].Body;
            Check((string?)body["start_date"] == "2025-01-01T00:00:00+00:00" && (string?)body["cost_index"] == "AUTO");
            try{a.Plan(Route(("Cost Index","1000")));throw new Exception("Invalid cost index planned");}catch(InvalidOperationException){}
            return Task.CompletedTask;
        });
    }
}
