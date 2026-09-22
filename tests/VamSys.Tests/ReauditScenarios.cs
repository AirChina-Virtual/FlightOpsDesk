using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

static class ReauditScenarios
{
    static void Check(bool v,string message="Reaudit assertion failed") {if(!v) throw new Exception(message);}
    static DataRow Row(params (string,string)[] f)=>new(){Fields=f.ToDictionary(x=>x.Item1,x=>x.Item2)};
    static ChangeItem Create(ResourceKind kind,DataRow r)=>new(){Resource=kind,Kind=ChangeKind.Create,After=r,Fields=r.Fields.ToDictionary(x=>x.Key,x=>new FieldChange(x.Value==""?FieldIntent.Clear:FieldIntent.Set,x.Value))};
    static OperationsAdapter Api(Workspace w,AuditHttp h)=>new(new OperationsTransport(new HttpClient(h),OperationsAdapter.BaseUri,Guid.NewGuid().ToString(),new TokenProvider(_=>Task.FromResult(new AccessToken("test",DateTimeOffset.UtcNow.AddHours(1))))),w);
    static DataRow Parse(OperationsAdapter a,ResourceKind k,string raw){using var d=JsonDocument.Parse(raw);return a.FromJson(k,d.RootElement);}
    static HttpResponseMessage Json(string s,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(s)};
    const string Fleet="""{"id":4,"airline_id":7,"name":"New","code":"B738","type":"pax","max_pax":189,"hide_in_phoenix":true}""";
    const string Page="""{"data":[{"id":99,"airline_id":7,"name":"Other"}],"meta":{"next_cursor_url":null}}""";
    static ChangeItem NewFleet()=>Create(ResourceKind.Fleets,Row(("Name","New"),("Type Code","B738"),("Type (pax/cargo/...)","pax")));
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        foreach(var mode in new[]{"boolean","numeric","unsent-default"})
            await test("Typed create readback verifies "+mode,async()=>
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h);var item=NewFleet();
                var field=mode=="boolean"?"Hide in Phoenix":"Max Passengers";
                var value=mode=="boolean"?"true":mode=="numeric"?"0189":"";
                item.After.Fields[field]=value;item.Fields[field]=new(value==""?FieldIntent.Clear:FieldIntent.Set,value);
                var raw=mode=="unsent-default"?Fleet.Replace("189","0"):Fleet;
                h.Next=(r,ct)=>r.Method==HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/fleet")?Json(Page)
                    :r.Method==HttpMethod.Post?Json(raw,HttpStatusCode.Created):Json("{\"data\":"+raw+"}");
                w.Resources[item.Resource].Draft.Add(item.After.Copy());var job=new BatchJob{Items=[item]};w.Jobs.Add(job);
                await new OperationsBatchExecutor(a,w).ExecuteAsync(job,()=>Task.CompletedTask,default);
                Check(item.State==ItemState.Succeeded && item.ApiExpectedValues!=null);
                if(mode=="unsent-default") Check(!item.ApiExpectedValues!.ContainsKey("max_pax") && !h.Methods.Contains(HttpMethod.Put));
                else Check(item.ApiExpectedValues![mode=="boolean"?"hide_in_phoenix":"max_pax"].ValueKind==(mode=="boolean"?JsonValueKind.True:JsonValueKind.Number));
                var restored=Workspace.Deserialize(w.Serialize()).Jobs.Single().Items.Single();
                Check(a.Matches(restored,Parse(a,item.Resource,raw),restored.After,false));
            });
        await test("Sparse typed update ignores unspecified fields but rejects different values",async()=>
        {
            foreach(var mode in new[]{"boolean","numeric"})
            {
                var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h);
                var old=Fleet.Replace("true","false").Replace("189","0");var before=Parse(a,ResourceKind.Fleets,old);
                var after=before.Copy();var field=mode=="boolean"?"Hide in Phoenix":"Max Passengers";var value=mode=="boolean"?"true":"0189";
                after.Fields[field]=value;
                var item=new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Update,Before=before,After=after,
                    Fields=new(){[field]=new(FieldIntent.Set,value),["Name"]=new(FieldIntent.Unspecified,"ignored")}};
                var wrote=false;
                h.Next=(r,ct)=>{if(r.Method==HttpMethod.Put)wrote=true;return Json("{\"data\":"+(wrote?Fleet:old)+"}");};
                await new OperationsBatchExecutor(a,w).ExecuteAsync(new(){Items=[item]},()=>Task.CompletedTask,default);
                Check(item.State==ItemState.Succeeded && item.ApiExpectedValues!.Count==1);
                Check(!a.Matches(item,Parse(a,item.Resource,old),item.After,false));
            }
        });
        await test("Explicit zero and empty collection are not omitted defaults",()=>
        {
            var w=new Workspace{AirlineId="7"};var a=Api(w,new());var item=NewFleet();
            item.After.Fields["Max Passengers"]="0";item.Fields["Max Passengers"]=new(FieldIntent.Set,"0");
            Check(!a.Matches(item,Parse(a,ResourceKind.Fleets,Fleet),item.After,false));
            var before=Parse(a,ResourceKind.Airports,"""{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","container_ids":[1]}""");
            var after=before.Copy();after.Fields["Container IDs"]="";
            var clear=new ChangeItem{Resource=ResourceKind.Airports,Kind=ChangeKind.Update,Before=before,After=after,Fields=new(){["Container IDs"]=new(FieldIntent.Clear,"")}};
            Check(!a.Matches(clear,before,after,false));
            Check(a.Matches(clear,Parse(a,ResourceKind.Airports,before.RawApiJson!.Replace("[1]","[]")),after,false));return Task.CompletedTask;
        });
        await test("Preflight 500 is retryable without inventing an unknown write",async()=>
        {
            var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h);var item=NewFleet();var job=new BatchJob{Items=[item]};w.Jobs.Add(job);
            h.Next=(r,ct)=>Json("{}",HttpStatusCode.InternalServerError);
            await new OperationsBatchExecutor(a,w).ExecuteAsync(job,()=>Task.CompletedTask,default);
            Check(item.State==ItemState.Pending && item.RemoteId==null && h.Methods.All(m=>m==HttpMethod.Get) && item.ApiExpectedValues==null);
            w=Workspace.Deserialize(w.Serialize());job=w.Jobs[0];item=job.Items[0];
            h.Next=(r,ct)=>r.Method==HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/fleet")?Json(Page)
                :r.Method==HttpMethod.Post?Json(Fleet,HttpStatusCode.Created):Json("{\"data\":"+Fleet+"}");
            await new OperationsBatchExecutor(Api(w,h),w).ExecuteAsync(job,()=>Task.CompletedTask,default);
            Check(item.State==ItemState.Succeeded && h.Methods.Count(m=>m==HttpMethod.Post)==1);
        });
        await test("Post-write verification errors stay unknown with durable typed expectations",async()=>
        {
            var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h);var item=NewFleet();var job=new BatchJob{Items=[item]};w.Jobs.Add(job);
            var persisted=false;
            h.Next=(r,ct)=>r.Method==HttpMethod.Post ? (persisted?Json(Fleet,HttpStatusCode.Created):throw new Exception("Expectations not persisted before write"))
                :r.RequestUri!.AbsolutePath.EndsWith("/fleet")?Json(Page):Json("{}",HttpStatusCode.UnprocessableEntity);
            await new OperationsBatchExecutor(a,w).ExecuteAsync(job,()=>{persisted=item.State==ItemState.Running&&item.ApiExpectedValues?.Count==3;return Task.CompletedTask;},default);
            Check(item.State==ItemState.Unknown && item.RemoteId=="4");
            w=Workspace.Deserialize(w.Serialize());job=w.Jobs[0];item=job.Items[0];
            h.Next=(r,ct)=>Json("{\"data\":"+Fleet+"}");
            await new OperationsBatchExecutor(Api(w,h),w).ExecuteAsync(job,()=>Task.CompletedTask,default);
            Check(item.State==ItemState.Succeeded && h.Methods.Count(m=>m==HttpMethod.Post)==1);
        });
        await test("Recovery candidate can be corrected after mismatch and restart without rebinding",async()=>
        {
            var w=new Workspace{AirlineId="7"};var h=new AuditHttp();var a=Api(w,h);
            var item=Create(ResourceKind.Airports,Row(("ICAO/IATA","EGLL"),("Name","Hub")));item.State=ItemState.Unknown;item.RemoteId="102";
            var job=new BatchJob{Items=[item]};w.Jobs.Add(job);w.Resources[item.Resource].Draft.Add(item.After.Copy());
            Check(item.CanProposeRecoveryId && item.EditableRecoveryId=="102");item.ProposeRecoveryId("102");
            h.Next=(r,ct)=>Json("""{"data":{"id":102,"airline_id":7,"icao":"EGKK","iata":"LGW","name":"Hub"}}""");
            await new OperationsBatchExecutor(a,w).ExecuteAsync(job,()=>Task.CompletedTask,default);
            Check(item.State==ItemState.Unknown && item.RemoteId==null && item.After.Identity==null && item.CanProposeRecoveryId);
            w=Workspace.Deserialize(w.Serialize());job=w.Jobs[0];item=job.Items[0];
            Check(item.EditableRecoveryId=="102");item.ProposeRecoveryId("101");
            h.Next=(r,ct)=>Json("""{"data":{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"Hub"}}""");
            await new OperationsBatchExecutor(Api(w,h),w).ExecuteAsync(job,()=>Task.CompletedTask,default);
            Check(item.State==ItemState.Succeeded && item.RemoteId=="101" && item.After.Identity!.RemoteId=="101" && item.RecoveryCandidateId==null);
            Check(!item.CanProposeRecoveryId && item.RecoveryCandidates.Any(x=>x.RemoteId=="102") && item.RecoveryCandidates.Any(x=>x.RemoteId=="101") && h.Methods.All(m=>m==HttpMethod.Get));
            item.State=ItemState.Unknown;Check(!item.CanProposeRecoveryId);
            try{item.ProposeRecoveryId("102");throw new Exception("Confirmed ID changed");}catch(InvalidOperationException){}
        });
    }
}
