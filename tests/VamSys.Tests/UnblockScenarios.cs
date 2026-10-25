using System.Text.Json;
using VamSys.App;
using VamSys.Core;
using VamSys.Infrastructure;
using Fixture = VerifiedScopeScenarios.Fixture;
using FaultWriter = VerifiedScopeScenarios.FaultWriter;

// Roadmap phase 1 (2026-10-20): every Pending or Unknown item has a way out that never writes.
static class UnblockScenarios
{
    static void Check(bool value,string message="Unblock assertion failed"){if(!value)throw new Exception(message);}
    static string Json(object? value)=>JsonSerializer.Serialize(value);
    static async Task Blocked(Func<Task> action,string code)
    {try{await action();}catch(InvalidOperationException e){Check(MessageErrors.Describe(e).Code==code,e.Message);return;}throw new Exception("Expected "+code);}

    static DataRow Parse(Fixture f,ResourceKind kind,string raw){using var json=JsonDocument.Parse(raw);return f.Api.FromJson(kind,json.RootElement);}
    static Dictionary<string,JsonElement> Values(params (string Wire,object Value)[] values)=>values.ToDictionary(p=>p.Wire,p=>JsonSerializer.SerializeToElement(p.Value));
    // One verdict: Matches fails exactly when the recorded evidence lists a reason, and names the expected one.
    static void Verdict(Fixture f,ChangeItem item,DataRow? current,string? reason,string label)
    {
        f.Api.RecordReadback(item,current);var fields=string.Join(",",item.VerificationDifferences.Select(d=>d.Field));
        var passed=current!=null&&f.Api.Matches(item,current,item.After,false);
        Check(passed==(item.VerificationDifferences.Count==0),$"{label}: verdict {passed} disagrees with evidence [{fields}]");
        Check(reason==null?passed:item.VerificationDifferences.Any(d=>d.Field==reason),$"{label}: expected {reason ?? "pass"}, evidence [{fields}]");
    }

    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("F1 discarding never-sent Pending items unblocks the workspace, keeps drafts and is never sent on resume",async()=>
        {
            using var f=new Fixture();
            var route=f.Delete(ResourceKind.Routes);
            var airport=f.Delete(ResourceKind.Airports);airport.ApiSteps=[new(){Method="DELETE",Path="airports/101"}];
            var job=f.Job(route,airport);
            Check(ApiOperationPolicy.HasOpenOnlineItems(f.W));
            await Blocked(()=>{f.Store.SaveConnectionSettings(f.W,"123","secret","",false);return Task.CompletedTask;},"ApiPendingJob");
            var drafts=f.W.Resources[ResourceKind.Routes].Draft.Count;
            Check(await OperationsPendingDiscard.DiscardAsync(f.W,job,f.Store.CheckpointWriter(f.W))==2,"Not every never-sent item was discarded");
            Check(job.Items.All(i=>i.State==ItemState.Failed&&ApiOperationPolicy.IsDiscardedUnsent(i)),"Items were not marked as discarded");
            Check(!ApiOperationPolicy.HasOpenOnlineItems(f.W),"Discarded items still block refresh and submission");
            Check(f.W.Resources[ResourceKind.Routes].Draft.Count==drafts&&Schemas.Deleted(f.W.Resources[ResourceKind.Routes].Draft.Single(r=>r.Identity?.RemoteId=="201")),"Draft was not kept");
            var disk=f.Store.Load(f.W.Id);
            Check(disk.Jobs.Single().Items.All(ApiOperationPolicy.IsDiscardedUnsent)&&!ApiOperationPolicy.HasOpenOnlineItems(disk),"Discard was not durable");
            await f.Run(job);
            Check(f.Remote.Requests.All(r=>r.Method=="GET")&&job.Items.All(ApiOperationPolicy.IsDiscardedUnsent),"Resume sent a discarded item");
            f.Store.SaveConnectionSettings(f.W,"123","secret","",false);
        });
        await test("F1 only items that never reached the server can be discarded, and a failed save changes nothing",async()=>
        {
            using var f=new Fixture();
            ChangeItem Rename(BatchStepState? step,bool accepted=false,bool created=false,ItemState state=ItemState.Pending)
            {
                var before=f.W.Resources[ResourceKind.Fleets].Snapshot[0].Dup();var after=before.With("Name","Renamed");
                return new(){Resource=ResourceKind.Fleets,Kind=ChangeKind.Update,State=state,Before=before,After=after,Fields=new(){["Name"]=new(FieldIntent.Set,"Renamed")},
                    WriteAccepted=accepted,CreateCompleted=created,ApiSteps=step is {} s?[new(){Method="PUT",Path="fleet/4",State=s}]:[]};
            }
            var refused=new[]{Rename(BatchStepState.InFlight),Rename(BatchStepState.ResponseReceived),Rename(BatchStepState.Prepared,accepted:true),
                Rename(BatchStepState.Prepared,created:true),Rename(null,state:ItemState.Unknown),Rename(BatchStepState.Prepared,state:ItemState.Running)};
            var eligible=Rename(BatchStepState.Prepared);
            Check(refused.All(i=>!ApiOperationPolicy.CanDiscardUnsent(i))&&ApiOperationPolicy.CanDiscardUnsent(eligible));
            var job=f.Job([..refused,eligible]);var before=Json(job.Items);
            try{await OperationsPendingDiscard.DiscardAsync(f.W,job,new FaultWriter(f.Store.CheckpointWriter(f.W),_=>true));throw new Exception("Expected persistence failure");}
            catch(PersistenceException){}
            Check(Json(job.Items)==before&&Json(f.Store.Load(f.W.Id).Jobs.Single().Items)==before,"A failed save changed task state");
            Check(await OperationsPendingDiscard.DiscardAsync(f.W,job,f.Store.CheckpointWriter(f.W))==1);
            Check(Json(job.Items.Take(refused.Length))==Json(refused)&&JsonSerializer.Deserialize<List<ChangeItem>>(before)!.Take(refused.Length).Select(Json).SequenceEqual(refused.Select(Json)),"A sent item was discarded");
            Check(ApiOperationPolicy.IsDiscardedUnsent(job.Items[^1])&&Json(f.Store.Load(f.W.Id).Jobs.Single().Items)==Json(job.Items));
            Check(await OperationsPendingDiscard.DiscardAsync(f.W,job,f.Store.CheckpointWriter(f.W))==0&&f.Remote.Requests.Count==0);
            var demo=new BatchJob{Mode=RunMode.Demo,Items=[Rename(null)]};f.W.Jobs.Add(demo);
            await Blocked(()=>OperationsPendingDiscard.DiscardAsync(f.W,demo,f.Store.CheckpointWriter(f.W)),"ApiUnknown");
        });
        await test("F1 task page separates discarded items from real failures and offers discarding only for never-sent items",()=>
        {
            var language=new LocalizationService();
            var pending=new ChangeItem{Resource=ResourceKind.Fleets,State=ItemState.Pending};
            var failed=new ChangeItem{Resource=ResourceKind.Fleets,State=ItemState.Failed};failed.SetMessage(Messages.Define("ApiHttp",("status",422)));
            var job=new BatchJob{Mode=RunMode.Online,Items=[pending,failed]};
            var model=new TaskJobViewModel(job,language,()=>true,()=>RunMode.Offline,()=>Task.CompletedTask,()=>Task.CompletedTask,()=>Task.CompletedTask,()=>Task.CompletedTask,discard:()=>Task.CompletedTask);
            Check(model.Discard.CanExecute(null)&&!model.Summary.Contains("已放弃"),"Discard unavailable for a never-sent item");
            pending.State=ItemState.Failed;pending.SetMessage(Messages.Define("ApiDiscardedUnsent"));model.Update(pending.Id);
            var texts=model.Items.Select(i=>i.Text).ToArray();
            Check(!model.Discard.CanExecute(null)&&model.Summary.Contains("已放弃 1"),model.Summary);
            Check(texts[0].Contains("· 已放弃 ·")&&!texts[0].Contains("· 失败 ·")&&texts[1].Contains("· 失败 ·"),texts[0]);
            language.SetLanguage("en-US");model.RefreshLanguage();
            Check(model.Items[0].Text.Contains("Discarded")&&model.Summary.Contains("Discarded 1"),model.Summary);
            var demo=new TaskJobViewModel(new(){Mode=RunMode.Demo,Items=[new(){State=ItemState.Pending}]},language,()=>true,()=>RunMode.Demo,
                ()=>Task.CompletedTask,()=>Task.CompletedTask,()=>Task.CompletedTask,()=>Task.CompletedTask,discard:()=>Task.CompletedTask);
            Check(!demo.Discard.CanExecute(null),"Demo tasks offered discarding");
            return Task.CompletedTask;
        });
        await test("G6 read-back verdict and evidence come from one check that names every failure reason",()=>
        {
            using var f=new Fixture();
            // Field values, missing records and the aircraft parent fleet.
            var plane=new ChangeItem{Resource=ResourceKind.Aircraft,Kind=ChangeKind.Create,After=new(){Fields = new Dictionary<string, string> {["Fleet ID"]="4",["Registration"]="B-TEST"}},
                ApiExpectedValues=Values(("registration","B-TEST"),("name","Plane"))};
            var aircraft=f.W.Resources[ResourceKind.Aircraft].Snapshot.Single();
            Verdict(f,plane,aircraft,null,"aircraft matches");
            plane.ApiExpectedValues=Values(("registration","B-TEST"),("name","Renamed"));Verdict(f,plane,aircraft,"name","field difference");
            plane.ApiExpectedValues=Values(("registration","B-TEST"));plane.After=plane.After.With("Fleet ID","5");Verdict(f,plane,aircraft,"fleet_id","parent fleet");
            Verdict(f,plane,null,"record","missing record");
            // Airport identity is matched through its ICAO/IATA aliases, not a wire field.
            var airport=new ChangeItem{Resource=ResourceKind.Airports,Kind=ChangeKind.Create,After=new(){Fields = new Dictionary<string, string> {["ICAO/IATA"]="EGLL",["Name"]="London"}},
                ApiExpectedValues=Values(("icao_iata","EGLL"),("name","London"))};
            var london=f.W.Resources[ResourceKind.Airports].Snapshot[0];
            Verdict(f,airport,london,null,"airport alias matches");
            airport.After=airport.After.With("ICAO/IATA","EGSS");airport.ApiExpectedValues=Values(("icao_iata","EGSS"),("name","London"));
            Verdict(f,airport,london,"ICAO/IATA","airport alias");
            // A recovered jumpseat create must be new and created within the request window.
            var now=f.Time.Clock.GetUtcNow();
            ChangeItem Jumpseat()=>new(){Resource=ResourceKind.Routes,Kind=ChangeKind.Create,State=ItemState.Unknown,PreCreateCandidateIds=["300"],
                After=new(){Fields = new Dictionary<string, string> {["Type"]="jumpseat",["Departure Airport (ICAO/IATA)"]="EGLL",["Arrival Airport (ICAO/IATA)"]="EGKK"}},
                ApiExpectedValues=Values(("type","jumpseat"),("departure_id",101),("arrival_id",102),("callsign","TST301")),
                ApiSteps=[new(){Method="POST",Path="routes",State=BatchStepState.InFlight,StartedAt=now,FinishedAt=now.AddSeconds(1)}]};
            DataRow Route(string id,DateTimeOffset created)=>Parse(f,ResourceKind.Routes,
                $$"""{"id":{{id}},"departure_id":101,"arrival_id":102,"type":"jumpseat","callsign":null,"flight_number":null,"created_at":"{{created:O}}"}""");
            Verdict(f,Jumpseat(),Route("301",now),null,"jumpseat in window");
            Verdict(f,Jumpseat(),Route("301",now.AddMinutes(10)),"created_at","jumpseat window");
            Verdict(f,Jumpseat(),Route("300",now),"pre_create_candidates","pre-existing jumpseat");
            var unrecorded=Jumpseat();unrecorded.PreCreateCandidateIds=null;Verdict(f,unrecorded,Route("301",now),"pre_create_candidates","unrecorded candidates");
            var unstarted=Jumpseat();unstarted.ApiSteps[0].StartedAt=null;Verdict(f,unstarted,Route("301",now),"request_window","unrecorded request window");
            // A resumed legacy create compares only its POST values and leaves the saved expectation untouched.
            var created=new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=new(){Fields = new Dictionary<string, string> {["Name"]="Fleet 4"}},
                ApiSteps=[new(){Method="POST",Path="fleet",Body=Values(("name","Fleet 4"))},new(){Method="PUT",Path="{created}",Body=Values(("max_cargo",9))}]};
            var expected=Values(("name","Fleet 4"),("max_cargo",9));created.ApiExpectedValues=expected;
            var fleet=f.W.Resources[ResourceKind.Fleets].Snapshot[0];
            Check(f.Api.MatchesCreated(created,fleet)&&!f.Api.Matches(created,fleet,created.After,false)&&ReferenceEquals(created.ApiExpectedValues,expected),"Resumed create verification changed");
            return Task.CompletedTask;
        });
        await test("G1 accepted writes whose read-back differs can accept the server's current value without writing",async()=>
        {
            foreach(var variant in new[]{"update","create","delete"})
            {
                using var f=new Fixture();var item=Accepted(f,variant);var job=f.Job(item);var local=item.After.LocalId;
                // Before: every resume reads again, still differs and stays Unknown, blocking the workspace.
                await f.Run(job);item=job.Items.Single();
                Check(item.State==ItemState.Unknown&&ApiOperationPolicy.HasOpenOnlineItems(f.W),variant+": not stuck as expected");
                Check(OperationsServerValueResolver.CanAccept(item),variant+": no way out offered");
                var resolver=new OperationsServerValueResolver(f.Api,f.W);
                var review=await resolver.InspectAsync(job,item,default);
                Check(review.CanAccept&&review.Differences.Any(d=>d.Field==(variant=="delete"?"record":"name")),variant+": differences "+string.Join(",",review.Differences.Select(d=>d.Field)));
                var before=Json(job.Items);var data=f.W.Resources[item.Resource];var rows=Json(new{data.Snapshot,data.Draft,data.Conflicts});
                try{await resolver.AcceptAsync(job,review,new FaultWriter(f.Store.CheckpointWriter(f.W),_=>true));throw new Exception("Expected persistence failure");}catch(PersistenceException){}
                Check(Json(job.Items)==before&&Json(new{data.Snapshot,data.Draft,data.Conflicts})==rows&&Json(f.Store.Load(f.W.Id).Jobs.Single().Items)==before,variant+": failed save changed state");
                await resolver.AcceptAsync(job,review,f.Store.CheckpointWriter(f.W));
                var done=job.Items.Single();
                Check(done.State==ItemState.ManuallyConfirmed&&done.Rebased&&done.ManualResolution is {Source:"AcceptServerValue",WriteReceipt:true,ReadStatus:200},variant+": not manually confirmed");
                Check(done.VerificationDifferences.Count>0&&done.Description?.Code==(variant=="delete"?"ApiAcceptedServerRecord":"ApiAcceptedServerValue"),variant+": evidence lost");
                Check(!ApiOperationPolicy.HasOpenOnlineItems(f.W)&&!WorkspaceStore.HasUnresolvedWrites(f.W)&&f.W.VerifiedOperations.Count==0,variant+": still blocked or verified");
                var snapshot=data.Snapshot.Single(r=>r.LocalId==local);var draft=data.FindDraftRow(local)!;
                if(variant=="delete")Check(!Schemas.Deleted(draft)&&snapshot.Identity!.RemoteId=="5"&&data.Conflicts.Count==0,"delete: record not kept");
                else Check(snapshot.Get("Name")=="SERVER NAME"&&draft.Get("Name")=="Local name"&&draft.Identity==snapshot.Identity
                    &&data.Conflicts.Single() is {Field:"Name",Local:"Local name",Remote:"SERVER NAME"},variant+": unapplied value not shown as conflict");
                var disk=f.Store.Load(f.W.Id);var saved=disk.Resources[item.Resource];
                Check(Json(disk.Jobs.Single().Items)==Json(job.Items)&&Json(saved.Conflicts)==Json(data.Conflicts)&&Json(saved.Snapshot.Single(r=>r.LocalId==local))==Json(snapshot)
                    &&Json(saved.Draft.Single(r=>r.LocalId==local))==Json(draft),variant+": not durable");
                await f.Run(job);
                Check(job.Items.Single().State==ItemState.ManuallyConfirmed&&f.Remote.Requests.All(r=>r.Method=="GET"),variant+": wrote or reopened");
            }
        });
        await test("G1 server value is accepted only after a successful read that still differs",async()=>
        {
            foreach(var variant in new[]{"read-failure","missing","matches"})
            {
                using var f=new Fixture();var item=Accepted(f,"update");var job=f.Job(item);var before=Json(job.Items);
                if(variant=="read-failure")f.Remote.ReadFailure=500;
                if(variant=="missing")f.Remote.Rows.Remove("fleet/4");
                if(variant=="matches")f.Remote.Rows["fleet/4"]["name"]="Local name";
                var resolver=new OperationsServerValueResolver(f.Api,f.W);
                if(variant=="read-failure")
                {
                    try{await resolver.InspectAsync(job,item,default);throw new Exception("Read failure ignored");}catch(ApiResponseException){}
                }
                else
                {
                    var review=await resolver.InspectAsync(job,item,default);Check(!review.CanAccept,variant+": accepted");
                    await Blocked(()=>resolver.AcceptAsync(job,review,f.Store.CheckpointWriter(f.W)),"ApiUnknown");
                }
                Check(Json(job.Items)==before&&f.Remote.Requests.All(r=>r.Method=="GET"),variant+": state changed");
            }
            using var g=new Fixture();
            var soft=g.Delete(ResourceKind.Routes);g.Receipt(soft);var move=g.Move();move.State=ItemState.Unknown;move.WriteAccepted=true;
            var unaccepted=Accepted(g,"update");unaccepted.WriteAccepted=false;var resolved=Accepted(g,"update");resolved.State=ItemState.Succeeded;
            foreach(var item in new[]{soft,move,unaccepted,resolved})
            {
                Check(!OperationsServerValueResolver.CanAccept(item),"Offered for "+item.Resource+" "+item.Kind);
                var job=g.Job(item);await Blocked(()=>new OperationsServerValueResolver(g.Api,g.W).InspectAsync(job,item,default),"ApiUnknown");
            }
        });
    }

    public static async Task RunSoftDeletes(Func<string,Func<Task>,Task> test)
    {
        // The DELETE was sent but never answered (timeout or 5xx): there is no write receipt.
        static ChangeItem Unanswered(Fixture f,ResourceKind kind)
        {
            var item=f.Delete(kind);var path=OperationsAdapter.Collection(kind)+"/"+item.Before!.Identity!.RemoteId;
            item.State=ItemState.Unknown;item.RemoteId=item.Before.Identity.RemoteId;item.SetMessage(Messages.Define("ApiUnknown"));
            item.ApiSteps=[new(){Method="DELETE",Path=path,State=BatchStepState.InFlight,StartedAt=f.Time.Clock.GetUtcNow().AddSeconds(-30)}];
            return item;
        }
        await test("G2b unanswered soft deletes that read 404 can be confirmed manually, marked as having no write receipt",async()=>
        {
            foreach(var kind in new[]{ResourceKind.Routes,ResourceKind.Airports})
            {
                using var f=new Fixture();var item=Unanswered(f,kind);var job=f.Job(item);var path=item.ApiSteps[0].Path;var local=item.After.LocalId;
                f.Remote.Rows.Remove(path);
                // Before: resume, the deletion check and the existing manual confirmation all leave it Unknown.
                await f.Run(job);await new OperationsDeletionVerifier(f.Api,f.W).VerifyAsync(job,f.Store.CheckpointWriter(f.W),default);item=job.Items.Single();
                Check(item.State==ItemState.Unknown&&!OperationsUnknownResolver.CanCheck(item)&&ApiOperationPolicy.HasOpenOnlineItems(f.W),kind+": not stuck as expected");
                await Blocked(()=>new OperationsDeletionVerifier(f.Api,f.W).ConfirmManuallyAsync(job,item,f.Store.CheckpointWriter(f.W),default),"ApiUnknown");
                Check(OperationsDeletionVerifier.CanConfirmWithoutReceipt(item),kind+": no way out offered");
                var verifier=new OperationsDeletionVerifier(f.Api,f.W);var before=Json(job.Items);var rows=f.W.Resources[kind].Draft.Count;
                try{await verifier.ConfirmWithoutReceiptAsync(job,item,new FaultWriter(f.Store.CheckpointWriter(f.W),_=>true),default);throw new Exception("Expected persistence failure");}catch(PersistenceException){}
                Check(Json(job.Items)==before&&f.W.Resources[kind].Draft.Count==rows&&Json(f.Store.Load(f.W.Id).Jobs.Single().Items)==before,kind+": failed save changed state");
                Check(await verifier.ConfirmWithoutReceiptAsync(job,item,f.Store.CheckpointWriter(f.W),default),kind+": 404 not confirmed");
                var done=job.Items.Single();
                Check(done.State==ItemState.ManuallyConfirmed&&done.Description?.Code=="ApiManualDeleteNoReceipt"&&done.ManualResolution is {Source:"SoftDeleteWithoutReceipt",WriteReceipt:false,ReadStatus:404},kind+": not marked as no receipt");
                Check(done.DeletionVerification is {Outcome:"ManualWithoutWriteReceipt",WasManuallyConfirmed:true,ReadStatus:404},kind+": deletion evidence "+done.DeletionVerification?.Outcome);
                Check(f.W.Resources[kind].FindDraftRow(local)==null&&f.W.Resources[kind].Snapshot.All(r=>r.LocalId!=local),kind+": deletion draft not archived");
                Check(!ApiOperationPolicy.HasOpenOnlineItems(f.W)&&!WorkspaceStore.HasUnresolvedWrites(f.W)&&f.W.VerifiedOperations.Count==0,kind+": still blocked or verified");
                Check(Json(f.Store.Load(f.W.Id).Jobs.Single().Items)==Json(job.Items)&&f.Remote.Requests.All(r=>r.Method=="GET"),kind+": not durable or wrote");
                // Distinguishable from the full proof, also after a later read-only deletion check or resume.
                await new OperationsDeletionVerifier(f.Api,f.W).VerifyAsync(job,f.Store.CheckpointWriter(f.W),default);await f.Run(job);
                var later=job.Items.Single();
                Check(later.State==ItemState.ManuallyConfirmed&&later.ManualResolution?.WriteReceipt==false&&later.Description?.Code=="ApiManualDeleteNoReceipt"&&f.W.VerifiedOperations.Count==0,kind+": upgraded without receipt");
                Check(f.Remote.Requests.All(r=>r.Method=="GET"),kind+": resent the delete");
            }
        });
        await test("G2b a record that is still readable or a failed read leaves the unanswered soft delete Unknown",async()=>
        {
            foreach(var variant in new[]{"readable","read-failure","accepted"})
            {
                using var f=new Fixture();var item=Unanswered(f,ResourceKind.Routes);
                if(variant=="read-failure"){f.Remote.Rows.Remove(item.ApiSteps[0].Path);f.Remote.ReadFailure=500;}
                if(variant=="accepted")f.Receipt(item);
                var job=f.Job(item);var before=Json(job.Items);var verifier=new OperationsDeletionVerifier(f.Api,f.W);
                if(variant=="accepted"){Check(!OperationsDeletionVerifier.CanConfirmWithoutReceipt(item));await Blocked(()=>verifier.ConfirmWithoutReceiptAsync(job,item,f.Store.CheckpointWriter(f.W),default),"ApiUnknown");}
                else if(variant=="read-failure"){try{await verifier.ConfirmWithoutReceiptAsync(job,item,f.Store.CheckpointWriter(f.W),default);throw new Exception("Read failure ignored");}catch(ApiResponseException){}}
                else Check(!await verifier.ConfirmWithoutReceiptAsync(job,item,f.Store.CheckpointWriter(f.W),default),"Readable record confirmed");
                Check(Json(job.Items)==before&&Json(f.Store.Load(f.W.Id).Jobs.Single().Items)==before&&f.Remote.Requests.All(r=>r.Method=="GET"),variant+": state changed");
            }
            using var g=new Fixture();var hard=Accepted(g,"delete");hard.WriteAccepted=false;
            Check(!OperationsDeletionVerifier.CanConfirmWithoutReceipt(hard),"Hard delete offered soft-delete confirmation");
        });
        await test("G2a unanswered soft deletes that still read exactly as before can be marked not applied after a reminder",async()=>
        {
            foreach(var kind in new[]{ResourceKind.Routes,ResourceKind.Airports})
            {
                using var f=new Fixture();var item=Unanswered(f,kind);var job=f.Job(item);var local=item.After.LocalId;
                // Before: resume and the deletion check read 200 and leave it Unknown, blocking the workspace.
                await f.Run(job);await new OperationsDeletionVerifier(f.Api,f.W).VerifyAsync(job,f.Store.CheckpointWriter(f.W),default);item=job.Items.Single();
                Check(item.State==ItemState.Unknown&&ApiOperationPolicy.HasOpenOnlineItems(f.W),kind+": not stuck as expected");
                var verifier=new OperationsDeletionVerifier(f.Api,f.W);
                var review=await verifier.InspectWithoutReceiptAsync(job,item,default);
                Check(review.CanMarkUnchanged&&!review.CanConfirmDeleted&&review.Differences.Count==0,kind+": unchanged record not recognized");
                Check(!await verifier.ConfirmWithoutReceiptAsync(job,item,f.Store.CheckpointWriter(f.W),default),kind+": readable record confirmed as deleted");
                var before=Json(job.Items);var data=f.W.Resources[kind];var rows=Json(new{data.Snapshot,data.Draft});
                try{await verifier.MarkUnchangedAsync(job,review,new FaultWriter(f.Store.CheckpointWriter(f.W),_=>true));throw new Exception("Expected persistence failure");}catch(PersistenceException){}
                Check(Json(job.Items)==before&&Json(f.Store.Load(f.W.Id).Jobs.Single().Items)==before,kind+": failed save changed state");
                await verifier.MarkUnchangedAsync(job,review,f.Store.CheckpointWriter(f.W));
                var done=job.Items.Single();
                Check(done.State==ItemState.Failed&&done.Description?.Code=="ApiSoftDeleteUnchanged"&&done.ManualResolution is {Source:"SoftDeleteUnchanged",WriteReceipt:false,ReadStatus:200},kind+": not marked as unchanged");
                Check(done.DeletionVerification is {Outcome:"UnchangedSinceBefore",ReadStatus:200},kind+": deletion evidence "+done.DeletionVerification?.Outcome);
                Check(Json(new{data.Snapshot,data.Draft})==rows&&Schemas.Deleted(data.FindDraftRow(local)!),kind+": rows or deletion draft changed");
                Check(!ApiOperationPolicy.HasOpenOnlineItems(f.W)&&!WorkspaceStore.HasUnresolvedWrites(f.W)&&f.W.VerifiedOperations.Count==0,kind+": still blocked or verified");
                Check(Json(f.Store.Load(f.W.Id).Jobs.Single().Items)==Json(job.Items),kind+": not durable");
                await f.Run(job);
                Check(job.Items.Single().State==ItemState.Failed&&f.Remote.Requests.All(r=>r.Method=="GET"),kind+": resent the delete");
            }
        });
        await test("G2a a record that differs from before, a 404, a failed read or a receipt is never marked unchanged",async()=>
        {
            foreach(var variant in new[]{"changed","missing","read-failure","accepted"})
            {
                using var f=new Fixture();var item=Unanswered(f,ResourceKind.Routes);var path=item.ApiSteps[0].Path;
                if(variant=="changed")f.Remote.Rows[path]["remarks"]="Edited elsewhere";
                if(variant=="missing")f.Remote.Rows.Remove(path);
                if(variant=="read-failure")f.Remote.ReadFailure=500;
                if(variant=="accepted")f.Receipt(item);
                var job=f.Job(item);var before=Json(job.Items);var verifier=new OperationsDeletionVerifier(f.Api,f.W);
                if(variant=="accepted")await Blocked(()=>verifier.InspectWithoutReceiptAsync(job,item,default),"ApiUnknown");
                else if(variant=="read-failure"){try{await verifier.InspectWithoutReceiptAsync(job,item,default);throw new Exception("Read failure ignored");}catch(ApiResponseException){}}
                else
                {
                    var review=await verifier.InspectWithoutReceiptAsync(job,item,default);
                    Check(!review.CanMarkUnchanged,variant+": offered");
                    if(variant=="changed")Check(review.Differences.Any(d=>d.Field=="Remarks"&&d.Actual=="Edited elsewhere"),"changed: difference not shown");
                    await Blocked(()=>verifier.MarkUnchangedAsync(job,review,f.Store.CheckpointWriter(f.W)),"ApiUnknown");
                }
                Check(Json(job.Items)==before&&Json(f.Store.Load(f.W.Id).Jobs.Single().Items)==before&&f.Remote.Requests.All(r=>r.Method=="GET"),variant+": state changed");
            }
        });
    }

    public static async Task RunRefresh(Func<string,Func<Task>,Task> test)
    {
        await test("A1 refresh succeeds when two remote airports share an IATA code, and the ambiguous code matches no local row",async()=>
        {
            using var f=new Fixture();f.Remote.ListRows=true;
            foreach(var path in f.Remote.Rows.Keys.Where(k=>!k.StartsWith("airports/")).ToList())f.Remote.Rows.Remove(path);
            System.Text.Json.Nodes.JsonObject Airport(int id,string icao,string iata)=>System.Text.Json.Nodes.JsonNode.Parse($$"""{"id":{{id}},"airline_id":7,"icao":"{{icao}}","iata":"{{iata}}","name":"Remote {{id}}"}""")!.AsObject();
            // 101 also shares LHR while it has a local identity row; 103 and 105 share LCY and have none.
            f.Remote.Rows["airports/101"]["iata"]="LHR";f.Remote.Rows["airports/106"]=Airport(106,"EGLX","LHR");
            f.Remote.Rows["airports/103"]=Airport(103,"EGLC","LCY");f.Remote.Rows["airports/105"]=Airport(105,"EGLY","LCY");f.Remote.Rows["airports/104"]=Airport(104,"EGSS","STN");
            var ambiguous=new DataRow{Fields = new Dictionary<string, string> {["ICAO/IATA"]="lcy",["Name"]="Local"}};var unique=new DataRow{Fields = new Dictionary<string, string> {["ICAO/IATA"]="stn",["Name"]="Stansted"}};
            f.W.Resources[ResourceKind.Airports].Draft.AddRange([ambiguous,unique]);f.Store.Save(f.W);
            await f.Api.RefreshSnapshotsAsync(staged=>f.Store.SaveAsync(staged),default);
            var data=f.W.Resources[ResourceKind.Airports];
            Check(data.Snapshot.Select(r=>r.Identity!.RemoteId).Order().SequenceEqual(["101","102","103","104","105","106"]),"Remote airports not refreshed");
            Check(data.FindDraftRow(ambiguous.LocalId) is {Identity:null} local&&local.Get("Name")=="Local","An ambiguous alias matched a local row");
            Check(data.FindDraftRow(unique.LocalId)?.Identity?.RemoteId=="104","A unique alias no longer matches");
            Check(data.Draft.Count(r=>r.Identity?.RemoteId is "103" or "105")==2&&data.Draft.Count(r=>r.Identity?.RemoteId is "101" or "106")==2);
            Check(f.Remote.Requests.All(r=>r.Method=="GET")&&f.Store.Load(f.W.Id).Resources[ResourceKind.Airports].Snapshot.Count==6,"Refresh was not saved");
        });
    }

    // An accepted write whose server value differs from what was sent: a PUT, a create with its ID, or a hard delete.
    static ChangeItem Accepted(Fixture f,string variant)
    {
        var fleets=f.W.Resources[ResourceKind.Fleets];
        if(variant=="delete")
        {
            var row=fleets.Snapshot.Single(r=>r.Identity!.RemoteId=="5").Dup();var marked=row.With("_delete","TRUE");fleets.ReplaceDraftRows([marked.Dup()]);
            var removal=new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Delete,State=ItemState.Unknown,Before=row,After=marked,WriteAccepted=true,RemoteId="5",
                ApiSteps=[new(){Method="DELETE",Path="fleet/5",State=BatchStepState.ResponseReceived}]};
            removal.SetMessage(Messages.Define("ApiUnknown"));return removal;
        }
        if(variant=="create")
        {
            f.Remote.Rows["fleet/9"]=System.Text.Json.Nodes.JsonNode.Parse("{\"id\":9,\"airline_id\":7,\"name\":\"SERVER NAME\",\"code\":\"A320\",\"type\":\"cargo\"}")!.AsObject();
            var row=new DataRow{Fields = new Dictionary<string, string> {["Name"]="Local name",["Type Code"]="A320",["Type (pax/cargo/...)"]="cargo"}};fleets.Draft.Add(row.Dup());
            var after=row.WithIdentity(new("9","7"));
            var create=new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,State=ItemState.Unknown,After=after,RemoteId="9",CreateCompleted=true,WriteAccepted=true,
                Fields=row.Fields.ToDictionary(p=>p.Key,p=>new FieldChange(FieldIntent.Set,p.Value)),ApiExpectedValues=Values(("name","Local name"),("code","A320"),("type","cargo")),
                ApiSteps=[new(){Method="POST",Path="fleet",State=BatchStepState.ResponseReceived,RemoteId="9"}]};
            create.SetMessage(Messages.Define("ApiReadbackMismatch"));return create;
        }
        var before=fleets.Snapshot.Single(r=>r.Identity!.RemoteId=="4").Dup();var renamed=before.With("Name","Local name");fleets.ReplaceDraftRows([renamed.Dup()]);
        f.Remote.Rows["fleet/4"]["name"]="SERVER NAME";
        var update=new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Update,State=ItemState.Unknown,Before=before,After=renamed,WriteAccepted=true,
            Fields=new(){["Name"]=new(FieldIntent.Set,"Local name")},ApiExpectedValues=Values(("name","Local name")),
            ApiSteps=[new(){Method="PUT",Path="fleet/4",State=BatchStepState.ResponseReceived,Body=Values(("name","Local name"))}]};
        update.SetMessage(Messages.Define("ApiReadbackMismatch"));return update;
    }
}
