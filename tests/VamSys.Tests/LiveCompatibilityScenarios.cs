using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.App;
using VamSys.Core;
using VamSys.Infrastructure;

static class LiveCompatibilityScenarios
{
    static void Check(bool value,string message="Live compatibility assertion failed"){if(!value)throw new Exception(message);}
    static ChangeItem Create(ResourceKind kind,params (string Key,string Value)[] fields)
    {
        var row=new DataRow{Fields=fields.ToDictionary(p=>p.Key,p=>p.Value)};
        return new(){Resource=kind,Kind=ChangeKind.Create,After=row,Fields=row.Fields.ToDictionary(p=>p.Key,p=>new FieldChange(FieldIntent.Set,p.Value))};
    }
    sealed class Server:HttpMessageHandler
    {
        public readonly Workspace Workspace=new(){AirlineId="7",Mode=RunMode.Online};
        public readonly RequestCoordinator Coordinator=InstantTime.Requests();
        public readonly Dictionary<string,JsonObject> Rows=[];
        public readonly List<(string Method,string Path,string? Body)> Requests=[];
        public readonly OperationsAdapter Api;
        public int FailNextWrite,FailNextRead;
        public bool CommitFailedWrite,DenyParameters,MissingApproval,StringFleetIds,WrongJumpseatIdentity,MissingJumpseatIdentity,ForeignParameter;
        public string? FleetReadbackMismatch;
        public Server()
        {
            Api=new(new OperationsTransport(new HttpClient(this),OperationsAdapter.BaseUri,Guid.NewGuid().ToString(),
                new TokenProvider(_=>Task.FromResult(new AccessToken("test-token",Coordinator.Clock.GetUtcNow().AddHours(1))),Coordinator.Clock),Coordinator),Workspace);
            Add(ResourceKind.Airports,"airports/101","""{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"London"}""");
            Add(ResourceKind.Airports,"airports/102","""{"id":102,"airline_id":7,"icao":"EGKK","iata":"LGW","name":"Gatwick"}""");
            foreach(var id in new[]{4,5})Add(ResourceKind.Fleets,"fleet/"+id,$$"""{"id":{{id}},"airline_id":7,"name":"Fleet {{id}}","code":"B738","type":"cargo","max_cargo":10000,"parameter_ids":[1],"updated_at":"2026-01-01T00:00:00Z"}""");
            Add(ResourceKind.Routes,"routes/10","""{"id":10,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO100","flight_number":"CA100","fleet_ids":[4],"hidden":true,"remarks":"original"}""");
        }
        void Add(ResourceKind kind,string path,string json)
        {
            Rows[path]=JsonNode.Parse(json)!.AsObject();using var doc=JsonDocument.Parse(json);var row=Api.FromJson(kind,doc.RootElement);
            Workspace.Resources[kind].Snapshot.Add(row.Dup());Workspace.Resources[kind].Draft.Add(row.Dup());
        }
        HttpResponseMessage Reply(string body,HttpStatusCode status=HttpStatusCode.OK)
        {
            var result=ScriptedHttp.Json(body,status);result.Headers.Date=Coordinator.Clock.GetUtcNow();result.Headers.Add("CF-Ray","fixture-trace");return result;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath["/api/v3/operations/".Length..];
            var body=request.Content==null?null:await request.Content.ReadAsStringAsync(ct);Requests.Add((request.Method.Method,path,body));
            if(request.Method==HttpMethod.Get)
            {
                if(FailNextRead>0){FailNextRead--;return Reply("""{"message":"Read failed","error_id":"fixture-read-error"}""",HttpStatusCode.InternalServerError);}
                if(path=="parameters")
                {
                    if(DenyParameters)return Reply("{}",HttpStatusCode.Forbidden);
                    var ids=MissingApproval?Array.Empty<int>():Rows.Where(p=>p.Key.StartsWith("fleet/")&&p.Key.Count(c=>c=='/')==1).Select(p=>p.Value["id"]!.GetValue<int>()).ToArray();
                    return Reply(JsonSerializer.Serialize(new{data=new[]{new{id=1,airline_id=ForeignParameter?8:7,icao_code="CAO",iata_code="CA",approved=true,restricted=false,fleet_ids=ids}},meta=new{next_cursor_url=(string?)null}}));
                }
                if(Rows.TryGetValue(path,out var row))
                {
                    var copy=row.DeepClone().AsObject();
                    if(StringFleetIds && copy["fleet_ids"] is JsonArray ids)copy["fleet_ids"]=new JsonArray(ids.Select(v=>(JsonNode?)JsonValue.Create(v!.ToString())).ToArray());
                    if(path.StartsWith("routes/") && copy["fleet_ids"] is JsonArray members && FleetReadbackMismatch!=null)
                    {
                        if(FleetReadbackMismatch=="missing")members.Clear();
                        else members.Add(FleetReadbackMismatch=="extra"?99:members[0]!.DeepClone());
                    }
                    return Reply(new JsonObject{["data"]=copy}.ToJsonString());
                }
                if(path is "fleet" or "routes" or "airports" || path.EndsWith("/aircraft"))
                {
                    var rows=Rows.Where(p=>p.Key.StartsWith(path+"/") && p.Key.Count(c=>c=='/')==path.Count(c=>c=='/')+1).Select(p=>p.Value.DeepClone()).ToArray();
                    return Reply(new JsonObject{["data"]=new JsonArray(rows),["meta"]=new JsonObject{["next_cursor_url"]=null}}.ToJsonString());
                }
                return Reply("{}",HttpStatusCode.NotFound);
            }
            var fail=FailNextWrite>0;if(fail)FailNextWrite--;
            if(fail&&!CommitFailedWrite)return Reply("""{"message":"Server Error","error_id":"fixture-server-error"}""",HttpStatusCode.InternalServerError);
            var data=body==null?new JsonObject():JsonNode.Parse(body)!.AsObject();
            JsonObject updated;
            if(request.Method==HttpMethod.Post)
            {
                updated=data.DeepClone().AsObject();updated["id"]=90;updated["created_at"]=Coordinator.Clock.GetUtcNow().ToString("O");
                if(path=="fleet")updated["airline_id"]=7;
                if(path=="routes" && updated["type"]?.ToString()=="jumpseat")
                {updated["callsign"]=WrongJumpseatIdentity?"OTHER":null;updated["flight_number"]=null;}
                if(MissingJumpseatIdentity)updated.Remove("callsign");
                if(updated["parameter_ids"] is JsonArray values)updated["parameter_ids"]=new JsonArray(values.Select(v=>(JsonNode?)JsonValue.Create(int.Parse(v!.ToString()))).ToArray());
                Rows[path+"/90"]=updated;
            }
            else if(request.Method==HttpMethod.Put)
            {updated=Rows[path];foreach(var pair in data)updated[pair.Key]=pair.Value?.DeepClone();}
            else if(request.Method==HttpMethod.Delete)
            {Rows.Remove(path);return Reply("",HttpStatusCode.NoContent);}
            else throw new Exception("Unexpected fixture method");
            updated["updated_at"]=Coordinator.Clock.GetUtcNow().ToString("O");
            if(fail)return Reply("""{"message":"Server Error","error_id":"fixture-server-error"}""",HttpStatusCode.InternalServerError);
            return Reply(request.Method==HttpMethod.Post?updated.ToJsonString():new JsonObject{["data"]=updated.DeepClone()}.ToJsonString(),request.Method==HttpMethod.Post?HttpStatusCode.Created:HttpStatusCode.OK);
        }
        public ChangeItem Edit(ResourceKind kind,string id,string field,string value)
        {
            var before=Workspace.Resources[kind].Snapshot.Single(r=>r.Identity!.RemoteId==id).Dup();var after=before.With(field,value);
            Workspace.Resources[kind].ReplaceDraftRows([after]);
            return new(){Resource=kind,Kind=ChangeKind.Update,Before=before,After=after,Fields=new(){[field]=new(FieldIntent.Set,value)}};
        }
        public Task Run(ChangeItem item)
        {
            var data=Workspace.Resources[item.Resource];if(data.FindDraftRow(item.After.LocalId)==null)data.Draft.Add(item.After);
            var job=new BatchJob{Mode=RunMode.Online,Items=[item]};Workspace.Jobs.Add(job);
            return new OperationsBatchExecutor(Api,Workspace).ExecuteAsync(job,()=>Task.CompletedTask,default);
        }
    }
    static ChangeItem Jumpseat()=>Create(ResourceKind.Routes,("Type","jumpseat"),("Callsign","CAO1234"),("Flight Number","CA1234"),
        ("Departure Airport (ICAO/IATA)","EGLL"),("Arrival Airport (ICAO/IATA)","EGKK"),("Remarks","fixture jumpseat"),("Is Hidden","TRUE"));
    static async Task Blocked(Func<Task> action,string code)
    {try{await action();}catch(InvalidOperationException error){Check(MessageErrors.Describe(error).Code==code,error.Message);return;}throw new Exception("Expected "+code);}

    public static async Task ExportDiagnostics(string path)
    {
        using var server=new Server{FailNextWrite=1};var item=server.Edit(ResourceKind.Fleets,"4","Name","Changed");await server.Run(item);await server.Run(item);
        using var client=new HttpClient(new DiagnosticHttp());var time=InstantTime.Requests();
        var transport=new OperationsTransport(client,OperationsAdapter.BaseUri,"export-fixture",TokenProvider.ClientCredentials(client,new("fixture-client","fixture-secret"),time.Clock),time);
        var rejected=new ChangeItem{Resource=ResourceKind.Routes,State=ItemState.Failed};
        try{await transport.SendAsync(HttpMethod.Put,new(OperationsAdapter.BaseUri,"routes/10"),new{fleet_ids=new[]{4}},default);}
        catch(ApiResponseException error){rejected.DiagnosticDetails=error.Details;rejected.Diagnostic=error.Diagnostic;}
        var json=ApiDiagnosticExport.Serialize(new BatchJob{Mode=RunMode.Online,Items=[item,rejected]});
        Check(!json.Contains("fixture-secret")&&!json.Contains("fixture-client")&&!json.Contains("fixture-token"));
        File.WriteAllText(path,json);File.WriteAllText(path+".sha256",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("Live contract: kg capacities are single POST, sparse, integer and compatibility guarded",async()=>
        {
            using var server=new Server();
            var fleet=Create(ResourceKind.Fleets,("Name","New"),("Type Code","B738"),("Type (pax/cargo/...)","cargo"),("Max Freight","22046"));
            var aircraft=Create(ResourceKind.Aircraft,("Name","Plane"),("Registration","QA-TEST"),("Fleet ID","4"),("Freight","2205"));
            foreach(var item in new[]{fleet,aircraft})
            {
                var steps=server.Api.Plan(item);Check(steps.Count==1&&steps[0].Method==HttpMethod.Post);
                Check(!steps[0].Body.ContainsKey("weight_unit")&&!steps[0].Body.ContainsKey("simbrief_overrides"));
            }
            foreach(var value in new[]{"-1","1.5","9223372036854775808"})
            {aircraft.After=aircraft.After.With("Freight",value);aircraft.Fields["Freight"]=new(FieldIntent.Set,value);await Blocked(()=>{server.Api.Plan(aircraft);return Task.CompletedTask;},"ApiInvalid");}
            aircraft.Fields["Freight"]=new(FieldIntent.Set,"0");Check((long)server.Api.Plan(aircraft)[0].Body["cargo"]! ==0);
            foreach(var field in new[]{"weight_unit","simbrief_overrides"})await Blocked(()=>
            {OperationsAdapter.ValidateBody(ResourceKind.Aircraft,false,new(){[field]=field=="weight_unit"?"kg":new object()});return Task.CompletedTask;},"ApiKnownWeightRestriction");
            var offline=new Workspace();fleet.After=fleet.After.With("Max Freight","1.5");offline.Resources[ResourceKind.Fleets].Draft.Add(fleet.After);
            Check(!Schemas.Validate(offline,ResourceKind.Fleets).Any(i=>i.Field=="Max Freight"));offline.Mode=RunMode.Online;
            Check(Schemas.Validate(offline,ResourceKind.Fleets).Any(i=>i.Field=="Max Freight"));
            var csv=new CsvAdapter();Check(csv.Map(csv.Parse("Name,Max Freight\r\nNew,1.5\r\n"),new Dictionary<string,string>()).Single().Get("Max Freight")=="1.5");
            using var raw=JsonDocument.Parse("""{"id":8,"airline_id":7,"fleet_id":4,"name":"Plane","registration":"QA-TEST","weight_unit":"lbs","simbrief_overrides":{"paxwgt":85}}""");
            var baseline=server.Api.FromJson(ResourceKind.Aircraft,raw.RootElement);var changed=baseline.With("Name","Changed");
            var sparse=new ChangeItem{Resource=ResourceKind.Aircraft,Kind=ChangeKind.Update,Before=baseline,After=changed,Fields=new(){["Name"]=new(FieldIntent.Set,"Changed")}};
            Check(server.Api.Plan(sparse).Single().Body.Count==1&&baseline.RawApiJson!.Contains("simbrief_overrides"));
        });
        await test("Live diagnostics: full 422 fields, error ID, timing, trace headers and credential redaction",async()=>
        {
            var handler=new DiagnosticHttp();using var client=new HttpClient(handler);var time=InstantTime.Requests();
            var tokens=TokenProvider.ClientCredentials(client,new("fixture-client","fixture-secret"),time.Clock);
            var transport=new OperationsTransport(client,OperationsAdapter.BaseUri,"diagnostic",tokens,time);
            try{await transport.SendAsync(HttpMethod.Put,new(OperationsAdapter.BaseUri,"fleet/4"),new{client_secret="fixture-secret"},default);throw new Exception("Expected 422");}
            catch(ApiResponseException error)
            {
                var d=error.Details!;Check(d.Status==422&&d.Method=="PUT"&&d.ErrorId=="fixture-error"&&d.FieldErrors.Count==2);
                Check(d.FieldErrors["late"][0]=="last validation message"&&d.ResponseBody!.Length>8000&&!d.BodyTruncated);
                Check(d.TraceHeaders["cf-ray"]=="fixture-correlation"&&d.ServerDate!=null&&d.FinishedAt>=d.StartedAt);
                var json=JsonSerializer.Serialize(d);Check(!json.Contains("fixture-secret")&&!json.Contains("fixture-client")&&!json.Contains("fixture-token"));
            }
            Check(handler.Methods.SequenceEqual(new[]{"POST","PUT"}),"OAuth must not be a captured business operation");
            var bounded=ApiDiagnosticCapture.Body(new(),JsonSerializer.Serialize(new{message=new string('界',500000),access_token="hidden"}));
            Check(bounded.BodyTruncated&&Encoding.UTF8.GetByteCount(bounded.ResponseBody!)<=1024*1024&&bounded.BodySha256!.Length==64&&!bounded.ResponseBody!.Contains("hidden"));
            var malformed=ApiDiagnosticCapture.Redact("Cookie: session=private-cookie; second=secret\r\nSet-Cookie: jwt=private-set-cookie; HttpOnly\r\nProxy-Authorization: Basic private-proxy\r\n{invalid JSON");
            Check(!malformed.Contains("private-")&&!malformed.Contains("second=secret"),"Non-JSON header-like credentials leaked");
            var echoed=ApiDiagnosticCapture.Redact(JsonSerializer.Serialize(new{message="Proxy-Authorization: Basic private-proxy\nCookie: session=private-cookie"}));
            Check(!echoed.Contains("private-"),"Header echoed in JSON message leaked");
            var saved=ApiDiagnosticCapture.Body(new(),"""{"message":"safe","client_secret":"hidden","errors":{"late":["retain me"]}}""");
            using(var export=JsonDocument.Parse(ApiDiagnosticExport.Serialize(new BatchJob{Items=[new(){DiagnosticDetails=saved}]})))
            {
                var body=export.RootElement.GetProperty("items")[0].GetProperty("DiagnosticDetails").GetProperty("ResponseBody").GetString()!;
                Check(body.Contains("retain me")&&Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))==saved.BodySha256,"Export changed an already sanitized body hash");
            }
            await ReplaySanitizedResponses();
        });
        await test("Live unknown write: unchanged readback never resends and preserves original error evidence",async()=>
        {
            using var server=new Server{FailNextWrite=1};var item=server.Edit(ResourceKind.Fleets,"4","Name","Changed");
            await server.Run(item);Check(item.State==ItemState.Unknown&&item.DiagnosticDetails!.ErrorId=="fixture-server-error");
            await server.Run(item);Check(item.State==ItemState.Unknown&&server.Requests.Count(r=>r.Method=="PUT")==1);
            Check(item.DiagnosticDetails!.ErrorId=="fixture-server-error"&&item.ReadbackDetails!.Method=="GET"&&item.VerificationDifferences.Any(d=>d.Field=="name"));
            server.FailNextRead=4;await server.Run(item);
            Check(item.State==ItemState.Unknown&&server.Requests.Count(r=>r.Method=="PUT")==1);
            Check(item.DiagnosticDetails!.ErrorId=="fixture-server-error"&&item.ReadbackDetails!.ErrorId=="fixture-read-error","Recovery replaced original write evidence");
        });
        await test("Live scheduled fleet sets: A to AB to B to A, normalized readback and distinct verification",async()=>
        {
            using var server=new Server{StringFleetIds=true};
            foreach(var value in new[]{"4,5","5","4"})
            {var item=server.Edit(ResourceKind.Routes,"10","Fleet IDs",value);await server.Run(item);Check(item.State==ItemState.Succeeded,item.Message);}
            Check(server.Requests.Count(r=>r.Method=="PUT")==3&&server.Workspace.VerifiedOperations.Contains("Routes:Update:FleetIds:Scheduled"));
            Check(!server.Workspace.VerifiedOperations.Contains("Routes:Update"));
            var empty=server.Edit(ResourceKind.Routes,"10","Fleet IDs","");await Blocked(()=>{server.Api.Plan(empty);return Task.CompletedTask;},"ApiClear");
        });
        await test("Live parameter approval: denied prefix and 403 block only dependent writes",async()=>
        {
            foreach(var forbidden in new[]{false,true})
            {
                using var server=new Server{MissingApproval=!forbidden,DenyParameters=forbidden};var item=server.Edit(ResourceKind.Routes,"10","Fleet IDs","5");
                await server.Run(item);Check(item.State==ItemState.Failed&&server.Requests.All(r=>r.Method=="GET"),item.Message);
                Check(await server.Api.FindAsync(ResourceKind.Fleets,"4",default)!=null);
            }
            using var stale=new Server();await stale.Api.ReadParametersAsync(default);
            stale.Rows["fleet/4"]["parameter_ids"]=new JsonArray();
            await stale.Api.FindAsync(ResourceKind.Fleets,"4",default);
            await Blocked(()=>{stale.Api.ValidateParameterPreview(stale.Edit(ResourceKind.Routes,"10","Fleet IDs","4"));return Task.CompletedTask;},"ApiPrefixDenied");
        });
        await test("Live scheduled readback rejects missing, extra and duplicate members; cross-VA approval never writes",async()=>
        {
            foreach(var mismatch in new[]{"missing","extra","duplicate"})
            {
                using var server=new Server();var item=server.Edit(ResourceKind.Routes,"10","Fleet IDs","5");
                // Change only the post-write representation; preflight must read an intact baseline.
                server.Rows["routes/10"]["fleet_ids"]=new JsonArray(4);
                var job=new BatchJob{Mode=RunMode.Online,Items=[item]};
                await new OperationsBatchExecutor(server.Api,server.Workspace).ExecuteAsync(job,()=>
                {if(item.WriteAccepted)server.FleetReadbackMismatch=mismatch;return Task.CompletedTask;},default);
                Check(item.State==ItemState.Unknown&&server.Requests.Count(r=>r.Method=="PUT")==1,mismatch);
            }
            using var foreign=new Server{ForeignParameter=true};var denied=foreign.Edit(ResourceKind.Routes,"10","Fleet IDs","5");
            await foreign.Run(denied);Check(denied.State==ItemState.Failed&&foreign.Requests.All(r=>r.Method=="GET"));
        });
        await test("Live newly planned fleets require one parameter while saved legacy tasks preserve omission",async()=>
        {
            using var server=new Server();var draft=Create(ResourceKind.Fleets,("Name","New"),("Type Code","B738"),("Type (pax/cargo/...)","cargo"),("Max Freight","1000"));
            server.Workspace.Resources[ResourceKind.Fleets].Draft.Add(draft.After);
            var planned=new ChangePlanner().PlanWorkspace(server.Workspace).Single(i=>i.After.LocalId==draft.After.LocalId);
            Check(planned.RequiresAirlineParameter);
            await Blocked(()=>{server.Api.Plan(planned);return Task.CompletedTask;},"ApiParameterRequired");
            var legacy=JsonSerializer.Deserialize<ChangeItem>("""{"Resource":1,"Kind":0}""")!;Check(!legacy.RequiresAirlineParameter);
            planned.After=planned.After.With("Parameter IDs","1");planned.Fields["Parameter IDs"]=new(FieldIntent.Set,"1");await server.Run(planned);
            Check(planned.State==ItemState.Succeeded,planned.Message);
        });
        await test("Live new fleet parameters: string request IDs, numeric response IDs and no existing reassignment",async()=>
        {
            using var server=new Server();var item=Create(ResourceKind.Fleets,("Name","New"),("Type Code","B738"),("Type (pax/cargo/...)","cargo"),("Max Freight","1000"),("Parameter IDs","1"));
            await server.Run(item);Check(item.State==ItemState.Succeeded,item.Message);
            var post=server.Requests.Single(r=>r.Method=="POST");using var json=JsonDocument.Parse(post.Body!);
            Check(json.RootElement.GetProperty("parameter_ids")[0].ValueKind==JsonValueKind.String&&server.Requests.All(r=>r.Method!="PUT"));
            await Blocked(()=>{server.Api.Plan(server.Edit(ResourceKind.Fleets,"4","Parameter IDs","1"));return Task.CompletedTask;},"ApiParameterCreateOnly");
            await Blocked(()=>{server.Api.Plan(server.Edit(ResourceKind.Fleets,"4","Allowed Prefix IDs","1"));return Task.CompletedTask;},"ApiUnsupported");
        });
        await test("Live capability gate: generic route updates cannot unlock scheduled fleet-set batches",()=>
        {
            using var server=new Server();var items=Enumerable.Range(0,4).Select(_=>server.Edit(ResourceKind.Routes,"10","Fleet IDs","5")).ToList();
            var plan=ReviewPlanBuilder.Build(items,RunMode.Online,true,new HashSet<string>{"Routes:Update"},_=>{});
            Check(plan.SubmitItems.Count==3&&plan.Entries.Count(e=>e.Disposition==ReviewDisposition.Deferred)==1);return Task.CompletedTask;
        });
        await test("Live jumpseat create: valid sent identity, explicit-null readback, safe persisted baseline",async()=>
        {
            using var server=new Server();var item=Jumpseat();await server.Run(item);Check(item.State==ItemState.Succeeded,item.Message);
            Check(item.ApiExpectedValues!["callsign"].GetString()=="CAO1234");
            using var request=JsonDocument.Parse(server.Requests.Single(r=>r.Method=="POST").Body!);
            Check(request.RootElement.GetProperty("callsign").GetString()=="CAO1234");
            Check(server.Workspace.Resources[ResourceKind.Routes].Draft.Single(r=>r.Identity?.RemoteId=="90").Get("Callsign")=="");
            Check(!Schemas.Validate(server.Workspace,ResourceKind.Routes).Any(i=>i.RowId==item.After.LocalId));
            using var wrong=new Server{WrongJumpseatIdentity=true};var rejected=Jumpseat();await wrong.Run(rejected);Check(rejected.State==ItemState.Unknown);
            using var missing=new Server{MissingJumpseatIdentity=true};var absent=Jumpseat();await missing.Run(absent);Check(absent.State==ItemState.Unknown);
        });
        await test("Live jumpseat unknown POST: manual new-ID recovery requires saved candidate and time evidence",async()=>
        {
            foreach(var invalid in new[]{"none","existing","old","missing"})
            {
                using var server=new Server{FailNextWrite=1,CommitFailedWrite=true};var item=Jumpseat();await server.Run(item);
                Check(item.State==ItemState.Unknown&&item.RemoteId==null&&item.PreCreateCandidateIds!.Contains("10"));
                if(invalid=="existing")item.PreCreateCandidateIds!.Add("90");
                if(invalid=="old")server.Rows["routes/90"]["created_at"]="2020-01-01T00:00:00Z";
                if(invalid=="missing")item.ApiSteps[0].StartedAt=null;
                item.ProposeRecoveryId("90");await server.Run(item);
                Check(item.State==(invalid=="none"?ItemState.Succeeded:ItemState.Unknown),invalid+": "+item.Message);
                Check(server.Requests.Count(r=>r.Method=="POST")==1);
            }
        });
        await test("Live timestamp metadata: updated_at changes do not conflict with independent field edits",async()=>
        {
            using var server=new Server();server.Rows["fleet/4"]["updated_at"]="2026-10-20T12:27:34Z";
            var item=server.Edit(ResourceKind.Fleets,"4","Name","Changed");await server.Run(item);Check(item.State==ItemState.Succeeded);
            Check(item.PreflightComparison is {BusinessFields.Count:0} && item.PreflightComparison.PreviousUpdatedAt!=item.PreflightComparison.CurrentUpdatedAt);
            var row=server.Workspace.Resources[ResourceKind.Fleets].Snapshot.Single(r=>r.Identity!.RemoteId=="4");var data=new ResourceData{Snapshot=[row.Dup()],Draft=[row.Dup()]};
            data.Draft[0]=data.Draft[0].With("Name","Local");using var doc=JsonDocument.Parse(server.Rows["fleet/4"].ToJsonString());var incoming=server.Api.FromJson(ResourceKind.Fleets,doc.RootElement);
            SnapshotMerger.Merge(ResourceKind.Fleets,data,[incoming]);Check(data.Conflicts.Count==0&&data.Draft[0].Get("Name")=="Local");
            var conflict=server.Edit(ResourceKind.Fleets,"4","Name","Local");server.Rows["fleet/4"]["name"]="Remote";
            await server.Run(conflict);Check(conflict.State==ItemState.Conflict&&conflict.PreflightComparison!.BusinessFields.Any(f=>f.Field=="Name"));
        });
        await Maintenance(test);
    }

    static async Task ReplaySanitizedResponses()
    {
        using var file=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","vcca-weight-20261020.json")));
        foreach(var sample in file.RootElement.GetProperty("cases").EnumerateArray())
        {
            var handler=new AuditHttp{Next=(request,ct)=>
            {
                var response=ScriptedHttp.Json(sample.GetProperty("response").GetRawText(),(HttpStatusCode)sample.GetProperty("status").GetInt32());
                response.Headers.Date=DateTimeOffset.Parse(sample.GetProperty("serverDate").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
                response.Headers.Add("CF-Ray",sample.GetProperty("cfRay").GetString());return response;
            }};
            using var client=new HttpClient(handler);var time=InstantTime.Requests();
            var transport=new OperationsTransport(client,OperationsAdapter.BaseUri,"replay",new TokenProvider(_=>Task.FromResult(new AccessToken("fixture",time.Clock.GetUtcNow().AddHours(1))),time.Clock),time);
            try
            {
                using var result=await transport.SendAsync(HttpMethod.Put,new(OperationsAdapter.BaseUri,sample.GetProperty("path").GetString()),sample.GetProperty("request"),default);
                Check(sample.GetProperty("status").GetInt32()==200);
                var data=result.RootElement.TryGetProperty("data",out var wrapped)?wrapped:result.RootElement;
                var kind=sample.GetProperty("path").GetString()!.Contains("aircraft")?ResourceKind.Aircraft:ResourceKind.Fleets;
                var adapter=new OperationsAdapter(transport,new Workspace{AirlineId="7"});var row=adapter.FromJson(kind,data);
                if(kind==ResourceKind.Fleets)Check(row.Get("Max Freight")=="22046"&&data.GetProperty("weight_unit").GetString()=="kg");
            }
            catch(ApiResponseException error)
            {Check((int)error.Status==sample.GetProperty("status").GetInt32()&&error.Details!.ErrorId==sample.GetProperty("response").GetProperty("error_id").GetString());}
            Check(handler.Methods.SequenceEqual(new[]{HttpMethod.Put})&&transport.LastResponse!.ServerDate!=null,"Replay attempted to retry a write");
        }
    }

    sealed class DiagnosticHttp:HttpMessageHandler
    {
        public List<string> Methods=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Methods.Add(request.Method.Method);
            if(request.RequestUri!.AbsolutePath=="/oauth/token")return Task.FromResult(ScriptedHttp.Json("""{"access_token":"fixture-token","token_type":"Bearer","expires_in":3600}"""));
            var response=ScriptedHttp.Json(JsonSerializer.Serialize(new{message="fixture-secret fixture-client fixture-token",error_id="fixture-error",
                errors=new Dictionary<string,string[]>{{"fleet_ids.0",[new string('x',9000)]},{"late",["last validation message"]}},nested=new{access_token="fixture-token",client_secret="fixture-secret"}}),HttpStatusCode.UnprocessableEntity);
            response.Headers.Date=DateTimeOffset.UtcNow;response.Headers.Add("CF-Ray","fixture-correlation");return Task.FromResult(response);
        }
    }

    static async Task Maintenance(Func<string,Func<Task>,Task> test)
    {
        await test("Maintenance backs up WAL, clears one workspace, retains secrets and all other workspace bytes",async()=>
        {
            var directory=StorageScenarios.Temp();var store=new WorkspaceStore(directory);var a=StorageScenarios.Fixture();a.Jobs.Clear();var b=StorageScenarios.Fixture();b.Jobs.Clear();
            store.Save(a);store.Save(b);store.SaveSecret(a.Id,"fixture-protected-secret");var other=store.Load(b.Id).Serialize();
            var preview=store.PreviewMaintenance(a.Id);Check(preview.CanClear&&preview.Drafts==1);
            var result=await store.BackupAndClearAsync(a.Id,preview.Revision);
            Check(result.Workspace.Resources.All(p=>p.Value.Draft.Count==0&&p.Value.Snapshot.Count==0)&&result.Workspace.Mode==RunMode.Offline);
            Check(store.LoadSecret(a.Id)=="fixture-protected-secret"&&store.Load(b.Id).Serialize()==other&&a.Resources[ResourceKind.Fleets].Draft.Count==1);
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.Backup.DatabasePath)))==result.Backup.Sha256&&File.Exists(result.Backup.ManifestPath));
            using var backup=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+result.Backup.DatabasePath);backup.Open();
            Check((string)StorageScenarios.Sql(backup,"PRAGMA integrity_check")! =="ok"&&Convert.ToInt32(StorageScenarios.Sql(backup,"SELECT count(*) FROM resource_rows WHERE workspace_id=$id",("$id",a.Id.ToString())))>0);
        });
        await test("Maintenance failures, stale versions and unresolved writes cannot clear data",async()=>
        {
            foreach(var point in new[]{"Maintenance:BeforeBackup","Maintenance:AfterBackup","Maintenance:BeforeCommit"})
            {
                var store=new WorkspaceStore(StorageScenarios.Temp());var w=StorageScenarios.Fixture();w.Jobs.Clear();store.Save(w);var before=store.Load(w.Id).Serialize();
                store.Barrier=p=>{if(p==point)throw new IOException("injected maintenance failure");};
                try{await store.BackupAndClearAsync(w.Id,w.StorageRevision);throw new Exception("Expected failure");}catch(IOException){}
                Check(store.Load(w.Id).Serialize()==before,point);
            }
            var fresh=new WorkspaceStore(StorageScenarios.Temp());var current=StorageScenarios.Fixture();current.Jobs.Clear();fresh.Save(current);
            await Blocked(async()=>{await fresh.BackupAndClearAsync(current.Id,current.StorageRevision-1);},"StorageStale");
            foreach(var state in new[]{ItemState.Running,ItemState.Unknown})
            {
                current.Jobs=[new(){Mode=RunMode.Online,Items=[new(){State=state}]}];fresh.Save(current);
                Check(!fresh.PreviewMaintenance(current.Id).CanClear);
                await Blocked(async()=>{await fresh.BackupAndClearAsync(current.Id,current.StorageRevision);},"MaintenanceUnsafe");
            }
            current.Jobs[0].Items=[new(){State=ItemState.Failed,CreateCompleted=true}];fresh.Save(current);
            await Blocked(async()=>{await fresh.BackupAndClearAsync(current.Id,current.StorageRevision);},"MaintenanceUnsafe");
            current.Jobs=[new(){Mode=RunMode.Demo,Items=[new(){State=ItemState.Pending,ApiSteps=[new(){State=BatchStepState.InFlight}]}]}];fresh.Save(current);
            await Blocked(async()=>{await fresh.BackupAndClearAsync(current.Id,current.StorageRevision);},"MaintenanceUnsafe");
            current.Jobs.Clear();fresh.Save(current);var revision=current.StorageRevision;
            fresh.Barrier=point=>{if(point=="Maintenance:AfterBackup"){var other=new WorkspaceStore(fresh.DataDirectory);var copy=other.Load(current.Id);copy.Name="Concurrent edit";other.Save(copy);}};
            await Blocked(async()=>{await fresh.BackupAndClearAsync(current.Id,revision);},"StorageStale");
            Check(fresh.Load(current.Id).Name=="Concurrent edit"&&fresh.Load(current.Id).Resources[ResourceKind.Fleets].Draft.Count==1);
        });
    }
}
