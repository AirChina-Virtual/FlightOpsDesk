using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.App;
using VamSys.Core;
using VamSys.Infrastructure;

static class VerifiedScopeScenarios
{
    static void Check(bool value,string message="Verified scope assertion failed"){if(!value)throw new Exception(message);}
    static string Json(object? value)=>JsonSerializer.Serialize(value);
    static ChangeItem Update(ResourceKind kind,string field,string value)=>new(){Resource=kind,Kind=ChangeKind.Update,
        Before=new(){Fields = new Dictionary<string, string> {["Type"]="scheduled"}},After=new(){Fields = new Dictionary<string, string> {["Type"]="scheduled",[field]=value}},Fields=new(){[field]=new(FieldIntent.Set,value)}};
    static async Task Blocked(Action action,string code)
    {try{action();}catch(InvalidOperationException e){Check(MessageErrors.Describe(e).Code==code,e.Message);return;}await Task.CompletedTask;throw new Exception("Expected "+code);}

    internal sealed class Server:HttpMessageHandler
    {
        public readonly Dictionary<string,JsonObject> Rows=[];
        public readonly List<(string Method,string Path,string? Body)> Requests=[];
        public int PatchStatus=200,DeleteStatus=204,ReadFailure;
        public bool CommitFailedPatch,WrongMoveReadback,ListRows;
        public string DeleteBody="{}";
        public Action<string>? OnGet;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.RequestUri!.AbsolutePath=="/oauth/token")return ScriptedHttp.Json("{\"access_token\":\"synthetic-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
            var path=request.RequestUri!.AbsolutePath["/api/v3/operations/".Length..];
            var body=request.Content==null?null:await request.Content.ReadAsStringAsync(ct);Requests.Add((request.Method.Method,path,body));
            if(request.Method==HttpMethod.Get)
            {
                OnGet?.Invoke(path);
                if(ReadFailure!=0)return ScriptedHttp.Json("{}",(HttpStatusCode)ReadFailure);
                if(ListRows&&!path.Contains('/'))
                    return ScriptedHttp.Json(new JsonObject{["data"]=new JsonArray(Rows.Where(p=>p.Key.StartsWith(path+"/")&&p.Key.Count(c=>c=='/')==1).Select(p=>(JsonNode)p.Value.DeepClone()).ToArray()),
                        ["meta"]=new JsonObject{["next_cursor_url"]=null}}.ToJsonString());
                if(Rows.TryGetValue(path,out var row))
                {
                    var copy=row.DeepClone().AsObject();if(WrongMoveReadback&&path=="fleet/5/aircraft/8")copy["fleet_id"]=4;
                    return ScriptedHttp.Json(new JsonObject{["data"]=copy}.ToJsonString());
                }
                if(!path.Contains('/'))return ScriptedHttp.Json("{\"data\":[],\"meta\":{\"next_cursor_url\":null}}");
                return ScriptedHttp.Json("{}",HttpStatusCode.NotFound);
            }
            if(request.Method==HttpMethod.Patch)
            {
                Check(path=="aircraft/8","Unexpected PATCH path");
                using var parsed=JsonDocument.Parse(body!);Check(parsed.RootElement.EnumerateObject().Count()==1,"PATCH included unrelated fields");
                var fleet=parsed.RootElement.GetProperty("fleet_id").GetInt32();
                if(PatchStatus==200||CommitFailedPatch)
                {
                    var aircraft=Rows.Single(p=>p.Key.EndsWith("/aircraft/8"));Rows.Remove(aircraft.Key);aircraft.Value["fleet_id"]=fleet;
                    Rows[$"fleet/{fleet}/aircraft/8"]=aircraft.Value;
                }
                return ScriptedHttp.Json(PatchStatus==200?"{}":"{\"error_id\":\"fixture-patch-500\"}",(HttpStatusCode)PatchStatus);
            }
            if(request.Method==HttpMethod.Put)
            {
                var updated=Rows[path];foreach(var (key,value) in JsonNode.Parse(body!)!.AsObject())updated[key]=value?.DeepClone();
                return ScriptedHttp.Json(new JsonObject{["data"]=updated.DeepClone()}.ToJsonString());
            }
            Check(request.Method==HttpMethod.Delete,"Unexpected write in fixture");
            if(DeleteStatus==204)Rows.Remove(path);
            return ScriptedHttp.Json(DeleteStatus==204?"{}":DeleteBody,(HttpStatusCode)DeleteStatus);
        }
    }
    internal sealed class Fixture:IDisposable
    {
        public readonly Workspace W;
        public readonly Server Remote=new();
        readonly HttpClient client;
        public readonly RequestCoordinator Time=InstantTime.Requests();
        public readonly WorkspaceStore Store;
        public readonly OperationsAdapter Api;
        readonly string? clientId;
        // Short tokens are re-acquired for every request, so a failure can be injected before one write.
        public bool ShortTokens;
        public Exception? TokenFailure;
        public Fixture(string? clientId=null)
        {
            this.clientId=clientId;
            W=new(){AirlineId="7",Mode=RunMode.Online,ContractHash=ApiContracts.Sha256};client=new(Remote);
            Api=Adapter(W);Store=new(Path.Combine(Path.GetTempPath(),"flightops-verified-scope-"+Guid.NewGuid().ToString("N")));
            Add(ResourceKind.Airports,"airports/101","{\"id\":101,\"airline_id\":7,\"icao\":\"EGLL\",\"name\":\"London\"}");
            Add(ResourceKind.Airports,"airports/102","{\"id\":102,\"airline_id\":7,\"icao\":\"EGKK\",\"name\":\"Gatwick\"}");
            foreach(var id in new[]{4,5})Add(ResourceKind.Fleets,"fleet/"+id,$$"""{"id":{{id}},"airline_id":7,"name":"Fleet {{id}}","code":"B738","type":"cargo"}""");
            Add(ResourceKind.Aircraft,"fleet/4/aircraft/8","{\"id\":8,\"airline_id\":7,\"fleet_id\":4,\"registration\":\"B-TEST\",\"name\":\"Plane\",\"cargo\":1000}");
        }
        public OperationsAdapter Adapter(Workspace w)=>new(new OperationsTransport(client,OperationsAdapter.BaseUri,Guid.NewGuid().ToString(),
            clientId==null?new TokenProvider(_=>TokenFailure is {} failure?Task.FromException<AccessToken>(failure)
                    :Task.FromResult(new AccessToken("synthetic-token",Time.Clock.GetUtcNow().Add(ShortTokens?TimeSpan.FromSeconds(30):TimeSpan.FromHours(1)))),Time.Clock)
                :TokenProvider.ClientCredentials(client,new(clientId,"fixture-secret"),Time.Clock),Time),w);
        public DataRow Add(ResourceKind kind,string path,string raw)
        {
            Remote.Rows[path]=JsonNode.Parse(raw)!.AsObject();using var json=JsonDocument.Parse(raw);var row=Api.FromJson(kind,json.RootElement);
            W.Resources[kind].Snapshot.Add(row.Dup());W.Resources[kind].Draft.Add(row.Dup());return row;
        }
        public ChangeItem Move()
        {
            var before=W.Resources[ResourceKind.Aircraft].Snapshot.Single().Dup();var after=before.With("Fleet ID","5");
            W.Resources[ResourceKind.Aircraft].ReplaceDraftRows([after]);
            return new(){Resource=ResourceKind.Aircraft,Kind=ChangeKind.Update,Before=before,After=after,Fields=new(){["Fleet ID"]=new(FieldIntent.Set,"5")}};
        }
        public ChangeItem Delete(ResourceKind kind,string type="scheduled")
        {
            var before=kind==ResourceKind.Airports?W.Resources[kind].Snapshot[0].Dup():Add(kind,"routes/201",$$"""{"id":201,"departure_id":101,"arrival_id":102,"type":"{{type}}","callsign":"TST201","flight_number":"TS201","fleet_ids":[4]}""").Dup();
            var after=before.With("_delete","TRUE");W.Resources[kind].ReplaceDraftRows([after]);
            return new(){Resource=kind,Kind=ChangeKind.Delete,Before=before,After=after};
        }
        public BatchJob Job(params ChangeItem[] items)
        {var job=new BatchJob{Mode=RunMode.Online,Items=[..items]};W.Jobs.Add(job);Store.Save(W);return job;}
        public Task Run(BatchJob job,IBatchCheckpointWriter? writer=null)=>new OperationsBatchExecutor(Api,W).ExecuteAsync(job,writer??Store.CheckpointWriter(W),default);
        public void Receipt(ChangeItem item,bool manual=false)
        {
            var id=item.Before!.Identity!.RemoteId;var path=OperationsAdapter.Collection(item.Resource)+"/"+id;var when=Time.Clock.GetUtcNow().AddSeconds(-2);
            item.WriteAccepted=true;item.State=manual?ItemState.ManuallyConfirmed:ItemState.Unknown;item.Rebased=manual;
            item.DiagnosticDetails=new(){Method="DELETE",Path=OperationsAdapter.BaseUri.AbsolutePath+path,Status=204,StartedAt=when,FinishedAt=when};
            item.ApiSteps=[new(){Method="DELETE",Path=path,State=BatchStepState.ResponseReceived,StartedAt=when,FinishedAt=when}];
            if(manual){W.Resources[item.Resource].Snapshot.Clear();W.Resources[item.Resource].Draft.Clear();item.SetMessage(Messages.Define("ApiManualResult"));}
            Remote.Rows.Remove(path);
        }
        public void Dispose()=>client.Dispose();
    }
    internal sealed class FaultWriter(IBatchCheckpointWriter inner,Func<BatchCheckpoint,bool> fail):IBatchCheckpointWriter
    {public Task WriteAsync(BatchCheckpoint checkpoint,CancellationToken ct)=>fail(checkpoint)?throw new IOException("Injected checkpoint failure"):inner.WriteAsync(checkpoint,ct);}

    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("Independent live evidence opens only covered fields, types and the fixed contract without workspace markers",()=>
        {
            Check(IndependentApiEvidence.ContractHash==ApiContracts.Sha256);
            foreach(var kind in Enum.GetValues<ResourceKind>())
            {
                var field=kind==ResourceKind.Routings||kind==ResourceKind.Routes?"Remarks":"Name";
                var items=Enumerable.Range(0,8).Select(_=>Update(kind,field,"Updated")).ToList();var markers=new HashSet<string>();
                var opened=ReviewPlanBuilder.Build(items,RunMode.Online,true,markers,_=>{},externalEvidenceContractHash:ApiContracts.Sha256);
                Check(opened.SubmitItems.Count==8&&markers.Count==0,"External evidence did not remain independent");
                Check(ReviewPlanBuilder.Build(items,RunMode.Online,true,markers,_=>{},externalEvidenceContractHash:"other-contract").SubmitItems.Count==3);
            }
            var unknown=Enumerable.Range(0,5).Select(_=>Update(ResourceKind.Routings,"Tags","not-tested")).ToList();
            Check(ReviewPlanBuilder.Build(unknown,RunMode.Online,true,new HashSet<string>(),_=>{},externalEvidenceContractHash:ApiContracts.Sha256).SubmitItems.Count==3,"Unverified tag variant was unlocked");
            var route=Update(ResourceKind.Routes,"Remarks","x");route.Before=route.Before!.With("Type","charter");route.After=route.After.With("Type","charter");
            Check(!IndependentApiEvidence.Covers(route,ApiContracts.Sha256));
            var fleet=new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=new(){Fields = new Dictionary<string, string> {["Type (pax/cargo/...)"]="pax"}},Fields=new(){["Type (pax/cargo/...)"]=new(FieldIntent.Set,"pax")}};
            Check(!IndependentApiEvidence.Covers(fleet,ApiContracts.Sha256));
            var jumpseat=new ChangeItem{Resource=ResourceKind.Routes,Kind=ChangeKind.Create,After=new(){Fields = new Dictionary<string, string> {["Type"]="jumpseat",["Fleet IDs"]="4"}},Fields=new(){["Type"]=new(FieldIntent.Set,"jumpseat"),["Fleet IDs"]=new(FieldIntent.Set,"4")}};
            Check(!IndependentApiEvidence.Covers(jumpseat,ApiContracts.Sha256));return Task.CompletedTask;
        });
        await test("External scope never bypasses actual preview validation or pending-job blockers",()=>
        {
            using var f=new Fixture();var item=f.Move();item.Fields["Name"]=new(FieldIntent.Set,"Mixed");
            var blocked=ReviewPlanBuilder.Build([item],RunMode.Online,true,new HashSet<string>(),c=>f.Api.PreviewPlan(c),externalEvidenceContractHash:ApiContracts.Sha256);
            Check(blocked.SubmitItems.Count==0&&blocked.Entries.Single().Reason!.Code=="ApiMoveSeparate");
            var plan=ReviewPlanBuilder.Build([Update(ResourceKind.Fleets,"Name","New")],RunMode.Online,true,new HashSet<string>(),_=>{},[Messages.Define("ApiPendingJob")],ApiContracts.Sha256);
            Check(plan.SubmitItems.Count==0&&f.Remote.Requests.Count==0);return Task.CompletedTask;
        });
        await test("Aircraft move uses one fleet-only PATCH, target-fleet readback and atomic parent rebase",async()=>
        {
            using var f=new Fixture();var item=f.Move();var job=f.Job(item);var local=item.After.LocalId;
            await f.Run(job);Check(item.State==ItemState.Succeeded,item.Message);
            Check(f.Remote.Requests.Where(r=>r.Method!="GET").Select(r=>r.Method+" "+r.Path).SequenceEqual(["PATCH aircraft/8"]));
            Check(f.Remote.Requests.Any(r=>r.Method=="GET"&&r.Path=="fleet/5")&&f.Remote.Requests.Any(r=>r.Method=="GET"&&r.Path=="fleet/5/aircraft/8"));
            var saved=f.Store.Load(f.W.Id);var row=saved.Resources[ResourceKind.Aircraft].Draft.Single();
            Check(row.LocalId==local&&row.Identity!.ParentFleetId=="5"&&row.Get("Fleet ID")=="5");
            Check(saved.VerifiedOperations.Contains("Aircraft:Update:FleetId:Move")&&!saved.VerifiedOperations.Contains("Aircraft:Update"));
        });
        await test("Aircraft move 500 recovery reads the target and never retries PATCH, with or without server commit",async()=>
        {
            foreach(var committed in new[]{false,true})
            {
                using var f=new Fixture();f.Remote.PatchStatus=500;f.Remote.CommitFailedPatch=committed;
                var item=f.Move();var job=f.Job(item);await f.Run(job);Check(item.State==ItemState.Unknown);
                var reload=f.Store.Load(f.W.Id);var recovered=reload.Jobs.Single();
                await new OperationsBatchExecutor(f.Adapter(reload),reload).ExecuteAsync(recovered,f.Store.CheckpointWriter(reload),default);
                Check(recovered.Items.Single().State==(committed?ItemState.Succeeded:ItemState.Unknown));
                Check(f.Remote.Requests.Count(r=>r.Method=="PATCH")==1);
            }
        });
        await test("Aircraft move checkpoint interruption never resubmits and recovers a received server result",async()=>
        {
            foreach(var afterResponse in new[]{false,true})
            {
                using var f=new Fixture();var item=f.Move();var job=f.Job(item);
                var writer=new FaultWriter(f.Store.CheckpointWriter(f.W),c=>c.Kind==CheckpointKind.Step&&c.Item!.ApiSteps[0].State==(afterResponse?BatchStepState.ResponseReceived:BatchStepState.InFlight));
                try{await f.Run(job,writer);throw new Exception("Expected persistence failure");}catch(PersistenceException){}
                // A step is saved as in flight before it is sent: a failed save means nothing left.
                Check(f.Remote.Requests.Count(r=>r.Method=="PATCH")==(afterResponse?1:0));
                var reload=f.Store.Load(f.W.Id);var recovered=reload.Jobs.Single();
                await new OperationsBatchExecutor(f.Adapter(reload),reload).ExecuteAsync(recovered,f.Store.CheckpointWriter(reload),default);
                Check(f.Remote.Requests.Count(r=>r.Method=="PATCH")==1,"PATCH was repeated or never sent");
                Check(recovered.Items.Single().State==ItemState.Succeeded);
            }
        });
        await test("Aircraft move rejects foreign or missing targets, mixed fields and tampered saved steps",async()=>
        {
            foreach(var bad in new[]{"foreign","missing","mixed","steps","locally-deleted"})
            {
                using var f=new Fixture();var item=f.Move();
                if(bad=="foreign")f.Remote.Rows["fleet/5"]["airline_id"]=8;
                if(bad=="missing")f.Remote.Rows.Remove("fleet/5");
                if(bad=="locally-deleted")f.W.Resources[ResourceKind.Fleets].ReplaceDraftRows([f.W.Resources[ResourceKind.Fleets].Draft.Single(r=>r.Identity?.RemoteId=="5").With("_delete","TRUE")]);
                if(bad=="mixed")item.Fields["Name"]=new(FieldIntent.Set,"Other");
                if(bad=="steps")item.ApiSteps=[new(){Method="PUT",Path="aircraft/8",Body=new(){["fleet_id"]=JsonSerializer.SerializeToElement(5)}}];
                var job=f.Job(item);await f.Run(job);Check(item.State==ItemState.Failed&&f.Remote.Requests.All(r=>r.Method=="GET"),bad);
            }
            using var empty=new Fixture();var invalid=empty.Move();invalid.After=invalid.After.With("Fleet ID","");invalid.Fields["Fleet ID"]=new(FieldIntent.Clear,"");
            await Blocked(()=>empty.Api.Plan(invalid),"ApiInvalid");
            using var changed=new Fixture();var moved=changed.Move();changed.Remote.Rows["fleet/4/aircraft/8"]["fleet_id"]=6;
            await changed.Run(changed.Job(moved));Check(moved.State==ItemState.Conflict&&changed.Remote.Requests.All(r=>r.Method=="GET"));
        });
        await test("Aircraft move rejects a target-path response whose fleet_id still names the old fleet",async()=>
        {
            using var f=new Fixture();f.Remote.WrongMoveReadback=true;var item=f.Move();var job=f.Job(item);await f.Run(job);
            Check(item.State==ItemState.Unknown&&f.Remote.Requests.Count(r=>r.Method=="PATCH")==1);
            Check(f.Store.Load(f.W.Id).Resources[ResourceKind.Aircraft].Snapshot.Single().Identity!.ParentFleetId=="4");
        });
        await test("Airport and scheduled/jumpseat deletes verify exact 204/404 proof and persist API invisibility",async()=>
        {
            foreach(var pair in new[]{(ResourceKind.Airports,""),(ResourceKind.Routes,"scheduled"),(ResourceKind.Routes,"jumpseat")})
            {
                using var f=new Fixture();var item=f.Delete(pair.Item1,pair.Item2);var job=f.Job(item);await f.Run(job);
                Check(item.State==ItemState.Succeeded&&item.DeletionVerification?.Outcome=="ApiInvisible",item.Message);
                var disk=f.Store.Load(f.W.Id);Check(disk.Jobs.Single().Items.Single().DeletionVerification?.ReadStatus==404);
                Check(disk.VerifiedOperations.Contains(ApiOperationPolicy.Key(item))&&f.Remote.Requests.Count(r=>r.Method=="DELETE")==1);
            }
        });
        await test("A short numeric Client ID inside the API path or IDs never breaks deletion proof",async()=>
        {
            foreach(var (clientId,kind) in new[]{("3",ResourceKind.Routes),("201",ResourceKind.Routes),("101",ResourceKind.Airports)})
            {
                using var f=new Fixture(clientId);var item=f.Delete(kind);var job=f.Job(item);await f.Run(job);
                Check(item.State==ItemState.Succeeded&&item.DeletionVerification?.Outcome=="ApiInvisible",clientId+": "+item.Message);
                var path=OperationsAdapter.BaseUri.AbsolutePath+OperationsAdapter.Collection(kind)+"/"+item.Before!.Identity!.RemoteId;
                Check(item.DiagnosticDetails!.Path==path&&item.ReadbackDetails!.Path==path,clientId+": diagnostic path was masked");
            }
        });
        await test("Writes that never reach the server or are rejected by authentication return to Pending and run once later",async()=>
        {
            foreach(var failure in new[]{"token-network","token-auth","http-401"})
            {
                using var f=new Fixture{ShortTokens=true};var item=f.Delete(ResourceKind.Routes);var job=f.Job(item);
                if(failure=="http-401")f.Remote.DeleteStatus=401;
                // The failure starts after the preflight GET, so only the DELETE's token request fails.
                else f.Remote.OnGet=path=>{if(path=="routes/201")f.TokenFailure=failure=="token-network"?new HttpRequestException("token endpoint unavailable"):new ApiAccessException("token rejected");};
                await f.Run(job);
                Check(item.State==ItemState.Pending&&item.ApiSteps.All(s=>s.State==BatchStepState.Prepared),failure+": "+item.State+" "+item.Message);
                Check(f.Remote.Requests.Count(r=>r.Method=="DELETE")==(failure=="http-401"?1:0),failure+": write count");
                Check(job.StatusCode==(failure=="token-network"?JobStatus.NeedsAttention:JobStatus.Paused),failure+": job "+job.StatusCode);
                var disk=f.Store.Load(f.W.Id);Check(disk.Jobs.Single().Items.Single().State==ItemState.Pending&&!WorkspaceStore.HasUnresolvedWrites(disk),failure+": durable state");
                f.TokenFailure=null;f.Remote.OnGet=null;f.Remote.DeleteStatus=204;
                await f.Run(job);
                Check(item.State==ItemState.Succeeded&&f.Remote.Requests.Count(r=>r.Method=="DELETE")==(failure=="http-401"?2:1),failure+": resume "+item.State);
            }
        });
        await test("Unknown writes leave the Unknown state only by read-only proof that they did not take effect",async()=>
        {
            async Task<(bool Proven,ChangeItem Current)> Resolve(Fixture f,ChangeItem item)
            {
                var job=f.Job(item);var proven=await new OperationsUnknownResolver(f.Api,f.W).ResolveNotAppliedAsync(job,item,f.Store.CheckpointWriter(f.W),default);
                return (proven,job.Items.Single());
            }
            ChangeItem Rename(Fixture f)
            {
                var before=f.W.Resources[ResourceKind.Airports].Snapshot[0].Dup();var after=before.With("Name","Heathrow");
                return new(){Resource=ResourceKind.Airports,Kind=ChangeKind.Update,State=ItemState.Unknown,Before=before,After=after,Fields=new(){["Name"]=new(FieldIntent.Set,"Heathrow")},
                    ApiSteps=[new(){Method="PUT",Path="airports/101",State=BatchStepState.InFlight}]};
            }
            ChangeItem NewFleet()=>new(){Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,State=ItemState.Unknown,
                After=new(){Fields = new Dictionary<string, string> {["Name"]="Fleet 9",["Type Code"]="A320",["Type (pax/cargo/...)"]="cargo"}},
                Fields=new(){["Name"]=new(FieldIntent.Set,"Fleet 9"),["Type Code"]=new(FieldIntent.Set,"A320"),["Type (pax/cargo/...)"]=new(FieldIntent.Set,"cargo")},
                ApiSteps=[new(){Method="POST",Path="fleet",State=BatchStepState.InFlight}]};
            foreach(var applied in new[]{false,true})
            {
                using var f=new Fixture();f.Remote.ListRows=true;
                if(applied)f.Remote.Rows["airports/101"]["name"]="Heathrow";
                var (proven,current)=await Resolve(f,Rename(f));
                Check(proven==!applied&&current.State==(applied?ItemState.Unknown:ItemState.Failed),"update applied="+applied);
                Check(f.Remote.Requests.All(r=>r.Method=="GET"),"Resolution wrote");
                var disk=f.Store.Load(f.W.Id);Check(disk.Jobs.Single().Items.Single().State==current.State&&WorkspaceStore.HasUnresolvedWrites(disk)==applied,"update durable applied="+applied);
            }
            foreach(var exists in new[]{false,true})
            {
                using var f=new Fixture();f.Remote.ListRows=true;
                if(exists)f.Add(ResourceKind.Fleets,"fleet/9","{\"id\":9,\"airline_id\":7,\"name\":\"fleet 9\",\"code\":\"A320\",\"type\":\"cargo\"}");
                var (proven,current)=await Resolve(f,NewFleet());
                Check(proven==!exists&&current.State==(exists?ItemState.Unknown:ItemState.Failed),"create exists="+exists);
                Check(f.Remote.Requests.All(r=>r.Method=="GET"),"Resolution wrote");
            }
            using var g=new Fixture();var accepted=Rename(g);accepted.WriteAccepted=true;var soft=g.Delete(ResourceKind.Routes);soft.State=ItemState.Unknown;
            Check(!OperationsUnknownResolver.CanCheck(accepted)&&!OperationsUnknownResolver.CanCheck(soft),"Accepted or soft-deleted writes were offered a not-applied check");
            try{await Resolve(g,accepted);throw new Exception("Accepted write was resolved");}catch(InvalidOperationException e){Check(MessageErrors.Describe(e).Code=="ApiUnknown");}
        });
        await test("A legacy two-step create resumes only never-sent steps after verifying the created record",async()=>
        {
            foreach(var variant in new[]{"resume","sent","changed"})
            {
                using var f=new Fixture();var row=new DataRow{Fields = new Dictionary<string, string> {["Name"]="Fleet 9",["Type Code"]="A320",["Type (pax/cargo/...)"]="cargo",["Max Freight"]="1000"}};
                f.W.Resources[ResourceKind.Fleets].Draft.Add(row.Dup());
                var item=new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,State=ItemState.Unknown,After=row,
                    Fields=row.Fields.ToDictionary(p=>p.Key,p=>new FieldChange(FieldIntent.Set,p.Value))};
                var post=new ApiStep(HttpMethod.Post,"fleet",new(){["name"]="Fleet 9",["code"]="A320",["type"]="cargo"});var put=new ApiStep(HttpMethod.Put,"{created}",new(){["max_cargo"]=1000L});
                OperationsAdapter.PrepareSteps(item,[post,put]);item.ApiExpectedValues=OperationsAdapter.ExpectedValues([post,put]);
                item.ApiSteps[0].State=BatchStepState.ResponseReceived;item.ApiSteps[0].RemoteId="9";item.RemoteId="9";item.CreateCompleted=true;item.After=item.After.WithIdentity(new("9","7"));
                if(variant=="sent"){item.ApiSteps[1].State=BatchStepState.InFlight;item.ApiSteps[1].Path="fleet/9";}
                f.Remote.Rows["fleet/9"]=JsonNode.Parse($$"""{"id":9,"airline_id":7,"name":"{{(variant=="changed"?"Renamed":"Fleet 9")}}","code":"A320","type":"cargo","max_cargo":0}""")!.AsObject();
                var job=f.Job(item);await f.Run(job);
                var puts=f.Remote.Requests.Count(r=>r.Method=="PUT");
                Check(f.Remote.Requests.All(r=>r.Method is "GET" or "PUT")&&puts==(variant=="resume"?1:0),variant+": writes");
                Check(item.State==(variant=="resume"?ItemState.Succeeded:ItemState.Unknown),variant+": "+item.State+" "+item.Message);
                if(variant=="resume")Check(f.Store.Load(f.W.Id).Resources[ResourceKind.Fleets].Snapshot.Single(r=>r.Identity?.RemoteId=="9").Get("Max Freight")=="1000");
            }
        });
        await test("Connecting identifies an empty VA from ranks or containers and rejects a different VA",async()=>
        {
            using var f=new Fixture();f.Remote.Rows.Clear();f.Remote.ListRows=true;
            var empty=new Workspace{Mode=RunMode.Online};var api=f.Adapter(empty);
            Check(!await api.IdentifyAirlineAsync(default)&&empty.AirlineId==null,"Identified an airline without evidence");
            f.Remote.Rows["containers/3"]=JsonNode.Parse("{\"id\":3,\"airline_id\":7}")!.AsObject();
            Check(await api.IdentifyAirlineAsync(default)&&empty.AirlineId=="7"&&api.SessionAirlineId=="7","Containers did not identify the VA");
            f.Remote.Rows["ranks/1"]=JsonNode.Parse("{\"id\":1,\"airline_id\":7}")!.AsObject();
            var fresh=new Workspace{Mode=RunMode.Online};Check(await f.Adapter(fresh).IdentifyAirlineAsync(default)&&fresh.AirlineId=="7");
            Check(f.Remote.Requests.All(r=>r.Method=="GET"));
            try{await f.Adapter(new Workspace{Mode=RunMode.Online,AirlineId="8"}).IdentifyAirlineAsync(default);throw new Exception("Different VA accepted");}
            catch(InvalidOperationException e){Check(MessageErrors.Describe(e).Code=="ApiWrongVa");}
        });
        await test("A numeric Client ID never rewrites numbers in diagnostic bodies while the secret stays redacted",async()=>
        {
            using var f=new Fixture("3");f.Remote.DeleteStatus=422;f.Remote.DeleteBody="{\"message\":\"route 3 rejected fixture-secret\",\"error_id\":\"e-13\"}";
            var item=f.Delete(ResourceKind.Routes);await f.Run(f.Job(item));
            var details=item.DiagnosticDetails!;
            Check(item.State==ItemState.Failed&&details.ServerMessage=="route 3 rejected [REDACTED]"&&details.ErrorId=="e-13",details.ServerMessage+" / "+details.ErrorId);
            Check(!details.ResponseBody!.Contains("fixture-secret")&&details.ResponseBody.Contains("e-13"));
        });
        await test("Soft-delete read-only verification requires matching accepted response, steps, identity and timing",async()=>
        {
            foreach(var fault in new[]{"status","method","path","steps","identity","localIdentity","accepted","time","oldjson"})
            {
                using var f=new Fixture();var item=f.Delete(ResourceKind.Routes);f.Receipt(item);
                if(fault=="status")item.DiagnosticDetails=item.DiagnosticDetails! with{Status=500};
                if(fault=="method")item.DiagnosticDetails=item.DiagnosticDetails! with{Method="PUT"};
                if(fault=="path")item.DiagnosticDetails=item.DiagnosticDetails! with{Path="/api/v3/operations/routes/999"};
                if(fault=="steps")item.ApiSteps[0].State=BatchStepState.InFlight;
                if(fault=="identity")item.After=item.After.WithIdentity(new("999","7"));
                if(fault=="localIdentity")item.After=item.After.WithLocalId(Guid.NewGuid());
                if(fault=="accepted")item.WriteAccepted=false;
                if(fault=="time")item.DiagnosticDetails=item.DiagnosticDetails! with{FinishedAt=f.Time.Clock.GetUtcNow().AddYears(1)};
                if(fault=="oldjson"){item.ApiSteps=[];item.DiagnosticDetails=null;}
                var job=f.Job(item);await new OperationsDeletionVerifier(f.Api,f.W).VerifyAsync(job,f.Store.CheckpointWriter(f.W),default);
                Check(job.Items.Single().State==ItemState.Unknown&&f.Remote.Requests.All(r=>r.Method=="GET"),fault);
            }
        });
        await test("Explicit read-only check upgrades manual history while leaving Pending and unrelated drafts/history untouched",async()=>
        {
            using var f=new Fixture();var item=f.Delete(ResourceKind.Routes);f.Receipt(item,true);
            var pending=new ChangeItem{Resource=ResourceKind.Routes,Kind=ChangeKind.Delete};var job=f.Job(item,pending);var pendingBefore=Json(pending);
            var fleets=f.W.Resources[ResourceKind.Fleets];var row=fleets.Draft[0];fleets.Edit([row.LocalId],()=>fleets.ReplaceDraftRows([row.With("Name","Unrelated local edit")]));f.Store.Save(f.W);
            var history=fleets.Undo.Count;
            await new OperationsDeletionVerifier(f.Api,f.W).VerifyAsync(job,f.Store.CheckpointWriter(f.W),default);
            var checkedItem=job.Items[0];Check(checkedItem.State==ItemState.Succeeded&&checkedItem.DeletionVerification!.WasManuallyConfirmed);
            Check(Json(job.Items[1])==pendingBefore&&f.Remote.Requests.Count==1&&f.Remote.Requests[0].Method=="GET");
            var disk=f.Store.Load(f.W.Id);Check(disk.Resources[ResourceKind.Fleets].Undo.Count==history&&disk.Resources[ResourceKind.Fleets].Draft[0].Get("Name")=="Unrelated local edit");
        });
        await test("Read-only 200, 500 and authorization failures retain manual provenance and never write",async()=>
        {
            foreach(var status in new[]{200,500,403})
            {
                using var f=new Fixture();var item=f.Delete(ResourceKind.Routes);var raw=f.Remote.Rows["routes/201"].DeepClone().AsObject();f.Receipt(item,true);
                if(status==200)f.Remote.Rows["routes/201"]=raw;else f.Remote.ReadFailure=status;
                var write=Json(item.DiagnosticDetails);var job=f.Job(item);
                await new OperationsDeletionVerifier(f.Api,f.W).VerifyAsync(job,f.Store.CheckpointWriter(f.W),default);
                Check(job.Items.Single().State==ItemState.ManuallyConfirmed&&Json(job.Items.Single().DiagnosticDetails)==write);
                Check(job.Items.Single().DeletionVerification!.WasManuallyConfirmed&&f.Remote.Requests.All(r=>r.Method=="GET"));
                if(status==403)Check(job.StatusCode==JobStatus.Paused);
            }
        });
        await test("Read-only verification checkpoint failure preserves durable and in-memory task state",async()=>
        {
            using var f=new Fixture();var item=f.Delete(ResourceKind.Routes);f.Receipt(item,true);var job=f.Job(item);var before=Json(item);
            var writer=new FaultWriter(f.Store.CheckpointWriter(f.W),_=>true);
            try{await new OperationsDeletionVerifier(f.Api,f.W).VerifyAsync(job,writer,default);throw new Exception("Expected persistence failure");}catch(PersistenceException){}
            Check(Json(job.Items.Single())==before&&Json(f.Store.Load(f.W.Id).Jobs.Single().Items.Single())==before);
            Check(!f.W.VerifiedOperations.Contains("Routes:Delete")&&f.Remote.Requests.All(r=>r.Method=="GET"));
        });
        await test("New deletion evidence is optional JSON and old tasks retain their steps and state",()=>
        {
            var item=new ChangeItem{State=ItemState.ManuallyConfirmed,ApiSteps=[new(){Method="DELETE",Path="routes/201"}]};var raw=Json(item);
            Check(!raw.Contains("DeletionVerification"));var old=JsonSerializer.Deserialize<ChangeItem>(raw)!;
            Check(old.DeletionVerification==null&&old.ApiSteps.Single().Path=="routes/201"&&old.State==ItemState.ManuallyConfirmed);
            Check(WorkspaceStore.FormatVersion==4);return Task.CompletedTask;
        });
        await test("Manual deletion confirmation commits before row/status changes and never fabricates API verification",async()=>
        {
            using var f=new Fixture();var item=f.Delete(ResourceKind.Routes);f.Receipt(item);var job=f.Job(item);
            var before=Json(item);var rows=f.W.Resources[ResourceKind.Routes].Draft.Count;
            var failed=new FaultWriter(f.Store.CheckpointWriter(f.W),_=>true);
            try{await new OperationsDeletionVerifier(f.Api,f.W).ConfirmManuallyAsync(job,item,failed,default);throw new Exception("Expected persistence failure");}catch(PersistenceException){}
            Check(Json(item)==before&&f.W.Resources[ResourceKind.Routes].Draft.Count==rows&&Json(f.Store.Load(f.W.Id).Jobs.Single().Items.Single())==before);
            await new OperationsDeletionVerifier(f.Api,f.W).ConfirmManuallyAsync(job,item,f.Store.CheckpointWriter(f.W),default);
            Check(job.Items.Single().State==ItemState.ManuallyConfirmed&&f.W.Resources[ResourceKind.Routes].Draft.Count==0&&f.Remote.Requests.Count==0);
            Check(f.Store.Load(f.W.Id).Jobs.Single().Items.Single().State==ItemState.ManuallyConfirmed&&!f.W.VerifiedOperations.Contains("Routes:Delete"));
        });
        await test("Task presentation separates manual counts and only enables explicit deletion checks after committed updates",()=>
        {
            using var f=new Fixture();var item=f.Delete(ResourceKind.Routes);f.Receipt(item,true);var job=f.Job(item);var language=new LocalizationService();var mode=RunMode.Online;
            var model=new TaskJobViewModel(job,language,()=>true,()=>mode,()=>Task.CompletedTask,()=>Task.CompletedTask,()=>Task.CompletedTask,()=>Task.CompletedTask,verifyDeletes:()=>Task.CompletedTask);
            Check(model.Summary.Contains("人工确认 1")&&model.VerifyDeletes.CanExecute(null));var previous=model.Summary;
            item.State=ItemState.Succeeded;Check(model.Summary==previous&&model.VerifyDeletes.CanExecute(null));
            model.Update(item.Id);Check(model.Summary.Contains("1/1")&&!model.VerifyDeletes.CanExecute(null));
            item.State=ItemState.ManuallyConfirmed;model.Update(item.Id);mode=RunMode.Offline;Check(!model.VerifyDeletes.CanExecute(null));return Task.CompletedTask;
        });
    }
}
