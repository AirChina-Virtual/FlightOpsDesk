using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

static class ApiScenarios
{
    static void Check(bool value,string message="API assertion failed") { if(!value) throw new Exception(message); }
    static DataRow Row(params (string,string)[] fields)=>new(){Fields=fields.ToDictionary(p=>p.Item1,p=>p.Item2)};
    static OperationsAdapter Adapter(Workspace w, HttpMessageHandler? handler=null) => new(new OperationsTransport(new HttpClient(handler??new ScriptedHttp([])),OperationsAdapter.BaseUri,Guid.NewGuid().ToString(),new TokenProvider(_=>Task.FromResult(new AccessToken("test-token",DateTimeOffset.UtcNow.AddHours(1))))),w);
    static DataRow Parse(OperationsAdapter api,ResourceKind kind,string json) { using var d=JsonDocument.Parse(json); return api.FromJson(kind,d.RootElement); }
    static ChangeItem Edit(ResourceKind kind,DataRow before,string field,string value)
    { var after=before.Copy(); after.Fields[field]=value; return new(){ Resource=kind,Kind=ChangeKind.Update,Before=before.Copy(),After=after,Fields=new(){[field]=new(value==""?FieldIntent.Clear:FieldIntent.Set,value)} }; }
    static ChangeItem Create(ResourceKind kind,DataRow row)=>new(){ Resource=kind,Kind=ChangeKind.Create,After=row,Fields=row.Fields.ToDictionary(p=>p.Key,p=>new FieldChange(FieldIntent.Set,p.Value)) };
    static void Blocked(Action action,string code) { try{action();throw new Exception("Expected block: "+code);}catch(InvalidOperationException e){Check(MessageErrors.Describe(e).Code==code,"Wrong block: "+MessageErrors.Describe(e).Code);} }
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        Task Sync(Action action){action();return Task.CompletedTask;}
        await test("API airport aliases match IATA imports and reject duplicate aliases",()=>Sync(()=>
        {
            var w=new Workspace{AirlineId="7"};var api=Adapter(w);
            var airport=Parse(api,ResourceKind.Airports,"{\"id\":101,\"airline_id\":7,\"icao\":\"EGLL\",\"iata\":\"LHR\",\"name\":\"London\"}");
            var data=new ResourceData{Snapshot=[airport.Copy()],Draft=[airport.Copy()]};
            CsvAdapter.Import(ResourceKind.Airports,data,[Row(("ICAO/IATA","LHR"),("Name","Updated"))],false);
            Check(data.Draft.Count==1&&data.Draft[0].Get("ICAO/IATA")=="EGLL"&&data.Draft[0].Get("Name")=="Updated");
            var undo=data.Undo.Count;
            try { CsvAdapter.Import(ResourceKind.Airports,data,[Row(("ICAO/IATA","LHR")),Row(("ICAO/IATA","EGLL"))],false);throw new Exception("Duplicate accepted"); }
            catch(FormatException){Check(data.Undo.Count==undo);}
            var old=Row(("ICAO/IATA","LHR"),("Name","London"));var legacy=new ResourceData{Snapshot=[old.Copy()],Draft=[old.Copy()]};
            SnapshotMerger.Merge(ResourceKind.Airports,legacy,[airport.Copy()]);Check(legacy.Draft.Count==1&&legacy.Conflicts.Count==0&&legacy.Draft[0].Identity!.RemoteId=="101");
        }));
        await test("Frozen official schema hash and route create integer associations",()=>Sync(()=>
        {
            using var stream=typeof(OperationsAdapter).Assembly.GetManifestResourceStream("Operations.OpenApi.json")!;
            Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))==ApiContracts.Sha256);
            var w=new Workspace{AirlineId="7"};var api=Adapter(w);
            Parse(api,ResourceKind.Airports,"{\"id\":101,\"airline_id\":7,\"icao\":\"EGLL\",\"iata\":\"LHR\",\"name\":\"London\"}");
            var route=Create(ResourceKind.Routes,Row(("Departure Airport (ICAO/IATA)","EGLL"),("Arrival Airport (ICAO/IATA)","LHR"),("Type","scheduled"),("Callsign","ACA123"),("Flight Number","AC123"),("Fleet IDs","4,5")));
            var step=api.Plan(route).Single();Check(step.Path=="routes"&&step.Method==HttpMethod.Post&&((long[])step.Body["fleet_ids"]!).SequenceEqual([4L,5L]));
            var fleet=Row(("Name","New"),("Type Code","B738"),("Type (pax/cargo/...)","pax"));w.Resources[ResourceKind.Fleets].Draft.Add(fleet);
            var aircraft=Create(ResourceKind.Aircraft,Row(("Name","Plane"),("Registration","G-TEST"),("Fleet ID","local:"+fleet.LocalId)));
            Check(api.PreviewPlan(aircraft).Count==1);Blocked(()=>api.Plan(aircraft),"ApiInvalid");
            var wrong=Row(("ID","1"));wrong.Identity=new("1","other");Blocked(()=>api.Plan(Edit(ResourceKind.Fleets,wrong,"Name","x")),"ApiWrongVa");
        }));
        await test("20,000 API snapshots merge without losing row identity or sparse edits",()=>Sync(()=>
        {
            var rows=Enumerable.Range(1,20000).Select(i=>new DataRow{Identity=new(i.ToString(),"7"),Fields=new(){["ID"]=i.ToString(),["Callsign"]="ACA"+i,["Remarks"]="old"}}).ToList();
            var data=new ResourceData{Snapshot=rows.Select(r=>r.Copy()).ToList(),Draft=rows.Select(r=>r.Copy()).ToList()};
            data.Draft[100].Fields["Remarks"]="local";var incoming=rows.Select(r=>r.Copy()).ToList();incoming[200].Fields["Remarks"]="remote";
            var watch=System.Diagnostics.Stopwatch.StartNew();SnapshotMerger.Merge(ResourceKind.Routes,data,incoming);watch.Stop();
            Check(data.Draft.Count==20000&&data.Draft[100].Get("Remarks")=="local"&&data.Draft[200].Get("Remarks")=="remote"&&data.Conflicts.Count==0);
            Console.WriteLine($"  20k API merge: {watch.ElapsedMilliseconds} ms");
        }));
        await test("Official requests split creation and use sparse API fields",()=>Sync(()=>
        {
            var w=new Workspace{AirlineId="7"};var api=Adapter(w);
            var item=Create(ResourceKind.Fleets,Row(("Name","Fleet"),("Type Code","B738"),("Type (pax/cargo/...)","pax"),("Max Passengers","189"),("unknown","keep")));
            var steps=api.Plan(item);Check(steps.Count==2 && steps[0].Path=="fleet" && steps[0].Method==HttpMethod.Post);
            Check(steps[0].Body.Keys.ToHashSet().SetEquals(["name","code","type"]) && (long)steps[1].Body["max_pax"]! == 189);
            var fleet=Parse(api,ResourceKind.Fleets,"{\"id\":4,\"airline_id\":7,\"name\":\"Old\",\"code\":\"B738\",\"type\":\"pax\"}");
            var update=api.Plan(Edit(ResourceKind.Fleets,fleet,"Name","New")).Single();Check(update.Path=="fleet/4"&&update.Body.Count==1&&update.Body["name"]!.ToString()=="New");
            Blocked(()=>api.Plan(Edit(ResourceKind.Fleets,fleet,"Type Code","BAD")),"ApiInvalid");
            Check(Workspace.Deserialize(new Workspace{Resources={[ResourceKind.Fleets]=new(){Draft=[fleet]}}}.Serialize()).Resources[ResourceKind.Fleets].Draft[0].RawApiJson==fleet.RawApiJson);
        }));
        await test("Airport addressing, documented clearing, routing immutability and response precision",()=>Sync(()=>
        {
            var w=new Workspace{AirlineId="7"};var api=Adapter(w);
            var airport=Parse(api,ResourceKind.Airports,"{\"id\":101,\"airline_id\":7,\"icao\":\"EGLL\",\"iata\":\"LHR\",\"name\":\"London\",\"container_ids\":[1]}");
            w.Resources[ResourceKind.Airports].Snapshot.Add(airport);
            Check(api.Plan(Edit(ResourceKind.Airports,airport,"Name","LHR")).Single().Path=="airports/101");
            Check(((long[])api.Plan(Edit(ResourceKind.Airports,airport,"Container IDs","")).Single().Body["container_ids"]!).Length==0);
            var item=Create(ResourceKind.Routings,Row(("Departure Airport (ICAO/IATA)","LHR"),("Arrival Airport (ICAO/IATA)","EGLL"),("Route String","DCT"),("Tags","a,b")));
            var steps=api.Plan(item);Check(steps.Count==2&&(long)steps[0].Body["departure_airport_id"]! ==101);
            var routing=Parse(api,ResourceKind.Routings,"{\"id\":9,\"airline_id\":7,\"departure_airport_id\":101,\"arrival_airport_id\":101,\"route\":\"DCT\"}");
            Blocked(()=>api.Plan(Edit(ResourceKind.Routings,routing,"Departure Airport (ICAO/IATA)","ZZZZ")),"ApiImmutable");
            var route=Parse(api,ResourceKind.Routes,"{\"id\":5,\"airline_id\":7,\"departure_id\":101,\"arrival_id\":101,\"type\":\"scheduled\",\"departure_time\":\"23:59:37\",\"fleet_ids\":[1,2]}");
            var time=Edit(ResourceKind.Routes,route,"Departure Time (HH:MM)","00:10");Check(api.Plan(time).Single().Body["departure_time"]!.ToString()=="00:10:37");
            Blocked(()=>api.Plan(Edit(ResourceKind.Routes,route,"Fleet IDs","1")),"ApiFleetSet");
            Blocked(()=>api.Plan(Edit(ResourceKind.Routes,route,"Type","jumpseat")),"ApiJumpseat");
            Blocked(()=>api.Plan(Edit(ResourceKind.Routes,route,"Departure Airport (ICAO/IATA)","EGKK")),"ApiImmutable");
            var aircraft=Parse(api,ResourceKind.Aircraft,"{\"id\":8,\"airline_id\":7,\"fleet_id\":4,\"name\":\"Plane\"}");
            Check(api.Plan(Edit(ResourceKind.Aircraft,aircraft,"Name","New")).Single().Path=="fleet/4/aircraft/8");
            Blocked(()=>api.Plan(Edit(ResourceKind.Aircraft,aircraft,"Fleet ID","5")),"ApiMoveFleet");
        }));
        await test("Three-way refresh preserves independent edits, CSV identity and sticky conflicts",()=>Sync(()=>
        {
            var baseline=Row(("ID","1"),("Name","Original"),("Type Code","B738"));var draft=baseline.Copy();draft.Fields["Name"]="Mine";
            var data=new ResourceData{Snapshot=[baseline],Draft=[draft]};
            var fresh=Row(("ID","1"),("Name","Original"),("Type Code","B739"));fresh.Identity=new("1","7");
            SnapshotMerger.Merge(ResourceKind.Fleets,data,[fresh]);
            Check(data.Draft.Single().LocalId==draft.LocalId && data.Draft[0].Get("Name")=="Mine" && data.Draft[0].Get("Type Code")=="B739" && data.Conflicts.Count==0);
            fresh=fresh.Copy();fresh.Fields["Name"]="Theirs";SnapshotMerger.Merge(ResourceKind.Fleets,data,[fresh]);Check(data.Conflicts.Count==1);
            SnapshotMerger.Merge(ResourceKind.Fleets,data,[fresh.Copy()]);Check(data.Conflicts.Count==1);
            var a=Row(("ICAO/IATA","EGLL"),("Name","London"));var d=new ResourceData{Snapshot=[a],Draft=[a.Copy()]};var b=a.Copy();b.Identity=new("101","7");
            SnapshotMerger.Merge(ResourceKind.Airports,d,[b]);Check(d.Draft.Count==1&&d.Draft[0].Identity!.RemoteId=="101");
        }));
        await test("OAuth uses form Client Credentials and actual expiry with cached token",async()=>
        {
            var handler=new ScriptedHttp([async r=>{Check(r.RequestUri!.AbsoluteUri=="https://vamsys.io/oauth/token");var form=await r.Content!.ReadAsStringAsync();Check(form.Contains("grant_type=client_credentials")&&form.Contains("client_secret=s%26x")&&form.Contains("scope=%2A"));return ScriptedHttp.Json("{\"access_token\":\"token\",\"token_type\":\"Bearer\",\"expires_in\":3600}");}]);
            var tokens=TokenProvider.ClientCredentials(new HttpClient(handler),new("123","s&x"));Check(await tokens.GetAsync(default)=="token"&&await tokens.GetAsync(default)=="token"&&handler.Calls==1);
        });
        await test("Aircraft reading enumerates fleets and single responses use documented envelopes",async()=>
        {
            var h=new ScriptedHttp([
                r=>Task.FromResult(ScriptedHttp.Json("{\"data\":[{\"id\":4,\"airline_id\":7,\"name\":\"Fleet\"}],\"meta\":{\"next_cursor_url\":null}}")),
                r=>{Check(r.RequestUri!.AbsolutePath.EndsWith("fleet/4/aircraft"));return Task.FromResult(ScriptedHttp.Json("{\"data\":[{\"id\":8,\"airline_id\":7,\"fleet_id\":4,\"name\":\"Plane\"}],\"meta\":{\"next_cursor_url\":null}}"));},
                r=>Task.FromResult(ScriptedHttp.Json("{\"data\":{\"id\":8,\"airline_id\":7,\"fleet_id\":4,\"name\":\"Plane\"}}"))]);
            var api=Adapter(new Workspace{AirlineId="7"},h);var rows=new List<DataRow>();await foreach(var row in api.ReadAllAsync(ResourceKind.Aircraft,default))rows.Add(row);
            Check(rows.Count==1&&rows[0].Identity!.ParentFleetId=="4");Check((await api.FindAsync(ResourceKind.Aircraft,"8",default))!.Get("Name")=="Plane");
        });
        await test("Creation persists ID before PUT; interrupted writes never repeat POST",async()=>
        {
            bool persisted=false;var w=new Workspace{AirlineId="7",Mode=RunMode.Online};
            var h=new ScriptedHttp([
                r=>Task.FromResult(ScriptedHttp.Json("{\"data\":[{\"id\":99,\"airline_id\":7,\"name\":\"Other\"}],\"meta\":{\"next_cursor_url\":null}}")),
                r=>Task.FromResult(ScriptedHttp.Json("{\"id\":4,\"airline_id\":7,\"name\":\"Fleet\",\"code\":\"B738\",\"type\":\"pax\"}",HttpStatusCode.Created)),
                r=>{Check(persisted&&r.Method==HttpMethod.Put&&r.RequestUri!.AbsolutePath.EndsWith("fleet/4"));throw new TimeoutException();},
                r=>Task.FromResult(ScriptedHttp.Json("{\"data\":{\"id\":4,\"airline_id\":7,\"name\":\"Fleet\",\"code\":\"B738\",\"type\":\"pax\",\"max_pax\":189}}"))]);
            var api=Adapter(w,h);var item=Create(ResourceKind.Fleets,Row(("Name","Fleet"),("Type Code","B738"),("Type (pax/cargo/...)","pax"),("Max Passengers","189")));
            w.Resources[ResourceKind.Fleets].Draft.Add(item.After.Copy());var job=new BatchJob{Mode=RunMode.Online,Items=[item]};var executor=new OperationsBatchExecutor(api,w);
            await executor.ExecuteAsync(job,()=>{persisted=item.CreateCompleted&&item.RemoteId=="4";return Task.CompletedTask;},default);
            Check(item.State==ItemState.Unknown&&persisted&&h.Calls==3);
            await executor.ExecuteAsync(job,()=>Task.CompletedTask,default);Check(item.State==ItemState.Succeeded&&h.Calls==4&&w.Resources[ResourceKind.Fleets].Draft[0].Get("ID")=="4");
        });
        await test("Soft delete accepted stays unknown even if GET returns 404",async()=>
        {
            var w=new Workspace{AirlineId="7"};
            var h=new ScriptedHttp([
                r=>Task.FromResult(ScriptedHttp.Json("{\"data\":{\"id\":101,\"airline_id\":7,\"icao\":\"EGLL\",\"iata\":\"LHR\",\"name\":\"London\"}}")),
                r=>Task.FromResult(ScriptedHttp.Json("{\"data\":[],\"meta\":{\"next_cursor_url\":null}}")),
                r=>Task.FromResult(ScriptedHttp.Json("{\"data\":[],\"meta\":{\"next_cursor_url\":null}}")),
                r=>Task.FromResult(ScriptedHttp.Json("{\"data\":[],\"meta\":{\"next_cursor_url\":null}}")),
                r=>Task.FromResult(ScriptedHttp.Json("{\"data\":[],\"meta\":{\"next_cursor_url\":null}}")),
                r=>{Check(r.Method==HttpMethod.Delete);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));},
                r=>Task.FromResult(ScriptedHttp.Json("{}",HttpStatusCode.NotFound))]);
            var api=Adapter(w,h);var row=Parse(api,ResourceKind.Airports,"{\"id\":101,\"airline_id\":7,\"icao\":\"EGLL\",\"iata\":\"LHR\",\"name\":\"London\"}");
            w.Resources[ResourceKind.Airports].Draft.Add(row.Copy());var item=new ChangeItem{Resource=ResourceKind.Airports,Kind=ChangeKind.Delete,Before=row.Copy(),After=row.Copy()};
            await new OperationsBatchExecutor(api,w).ExecuteAsync(new(){Items=[item]},()=>Task.CompletedTask,default);Check(item.State==ItemState.Unknown&&item.WriteAccepted&&!w.VerifiedOperations.Contains("Airports:Delete"));
        });
        await test("Online update conflict sends no PUT and persists structured status",async()=>
        {
            var w=new Workspace{AirlineId="7"};var h=new ScriptedHttp([r=>Task.FromResult(ScriptedHttp.Json("{\"data\":{\"id\":4,\"airline_id\":7,\"name\":\"Other\"}}"))]);var api=Adapter(w,h);
            var row=Parse(api,ResourceKind.Fleets,"{\"id\":4,\"airline_id\":7,\"name\":\"Old\"}");var item=Edit(ResourceKind.Fleets,row,"Name","New");
            await new OperationsBatchExecutor(api,w).ExecuteAsync(new(){Items=[item]},()=>Task.CompletedTask,default);Check(item.State==ItemState.Conflict&&h.Calls==1&&item.Description!.Code=="ApiConflict");
        });
        await test("Write 500 is not retried and response diagnostics redact secrets",async()=>
        {
            var h=new ScriptedHttp([r=>Task.FromResult(ScriptedHttp.Json("{\"access_token\":\"secret-value\",\"error\":\"failed\"}",HttpStatusCode.InternalServerError))]);
            var transport=new OperationsTransport(new HttpClient(h),OperationsAdapter.BaseUri,Guid.NewGuid().ToString(),new TokenProvider(_=>Task.FromResult(new AccessToken("token",DateTimeOffset.UtcNow.AddHours(1)))));
            try{await transport.SendAsync(HttpMethod.Post,new(OperationsAdapter.BaseUri,"fleet"),new{name="x"},default);throw new Exception();}
            catch(ApiResponseException ex){Check(!ex.Diagnostic.Contains("secret-value")&&h.Calls==1);}
        });
        await test("API messages translate; CSV output remains byte-identical with metadata",()=>Sync(()=>
        {
            var w=new Workspace{AirlineId="7"};var api=Adapter(w);var row=Parse(api,ResourceKind.Fleets,"{\"id\":4,\"airline_id\":7,\"name\":\"机型\",\"code\":\"B738\",\"type\":\"pax\"}");
            var change=Edit(ResourceKind.Fleets,row,"Name","测试");var csv=new CsvAdapter();var before=csv.Export(ResourceKind.Fleets,[change]).Single();
            var loc=new LocalizationService();loc.SetLanguage("en-US");Check(loc.Get("ApiMoveFleet").Contains("disabled"));Check(before.SequenceEqual(csv.Export(ResourceKind.Fleets,[change]).Single()));
            change.After.Identity=null;change.After.RawApiJson=null;Check(before.SequenceEqual(csv.Export(ResourceKind.Fleets,[change]).Single()));
        }));
    }
}
sealed class ScriptedHttp(IEnumerable<Func<HttpRequestMessage,Task<HttpResponseMessage>>> scripts):HttpMessageHandler
{
    readonly Queue<Func<HttpRequestMessage,Task<HttpResponseMessage>>> queue=new(scripts);
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return queue.Dequeue()(request);}
    public static HttpResponseMessage Json(string value,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(value,System.Text.Encoding.UTF8,"application/json")};
}
