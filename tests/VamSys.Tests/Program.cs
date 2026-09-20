using System.Diagnostics;
using System.Net;
using System.Text;
using VamSys.Core;
using VamSys.Infrastructure;

int passed = 0;
async Task Test(string name, Func<Task> test)
{
    await test(); Console.WriteLine("PASS " + name); passed++;
}
Task Sync(Action a) { a(); return Task.CompletedTask; }
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
DataRow Row(params (string, string)[] fields) => new() { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };
var csv = new CsvAdapter(); var planner = new ChangePlanner();
if (args.Length == 2 && args[0] == "--seed-ui")
{
    var w = new Workspace { Name = "QA 20000 航线", Mode = RunMode.Demo };
    CsvAdapter.Import(ResourceKind.Airports,w.Resources[ResourceKind.Airports],[Row(("ICAO/IATA","ZBAA"),("Name","北京")),Row(("ICAO/IATA","ZSPD"),("Name","上海"))],true);
    CsvAdapter.Import(ResourceKind.Routes,w.Resources[ResourceKind.Routes],Enumerable.Range(0,20000).Select(i=>Row(("ID",i.ToString()),("Departure Airport (ICAO/IATA)","ZBAA"),("Arrival Airport (ICAO/IATA)","ZSPD"),("Type","jumpseat"),("Tags","性能测试"))).ToList(),true);
    new WorkspaceStore(args[1]).Save(w); Console.WriteLine("Seeded isolated UI workspace"); return;
}

await Test("CSV quoting, Unicode, embedded newline and leading zeros", () => Sync(() =>
{
    var t = csv.Parse("\uFEFFID,Name,Extra\r\n001,\"中文,机场\",\"a\"\"b\r\nc\"\r\n");
    var rows = csv.Map(t, new Dictionary<string,string>());
    Check(rows[0].Get("ID") == "001"); Check(rows[0].Get("Extra") == "a\"b\r\nc");
    var data = new ResourceData { Draft = rows }; var files = csv.Export(ResourceKind.Fleets, planner.Plan(ResourceKind.Fleets, data));
    var round = csv.Map(csv.Parse(Encoding.UTF8.GetString(files[0])), new Dictionary<string,string>());
    Check(round[0].Get("Extra") == rows[0].Get("Extra"));
}));
await Test("CSV rejects malformed quotes and duplicate headers", () => Sync(() =>
{
    foreach (var text in new[] { "A,A\n1,2", "A\n\"abc", "A,B\n1", "A\n\"a\"b" })
    { try { csv.Parse(text); throw new Exception("Accepted bad CSV"); } catch (FormatException) { } }
}));
await Test("Patch import preserves missing columns and rows, explicit empty clears", () => Sync(() =>
{
    var data = new ResourceData(); CsvAdapter.Import(ResourceKind.Airports, data, [Row(("ICAO/IATA","ZBAA"),("Name","old"),("Unknown","retain")), Row(("ICAO/IATA","ZSPD"),("Name","second"))], true);
    CsvAdapter.Import(ResourceKind.Airports, data, [Row(("ICAO/IATA","ZBAA"),("Name",""))], false);
    Check(data.Draft.Count == 2); Check(data.Draft[0].Get("Unknown") == "retain");
    var changes = planner.Plan(ResourceKind.Airports, data); Check(changes.Count == 1); Check(changes[0].Fields["Name"].Intent == FieldIntent.Clear);
    data.UndoEdit(); Check(data.Draft[0].Get("Name") == "old"); data.RedoEdit(); Check(data.Draft[0].Get("Name") == "");
}));
await Test("Explicit deletion only; omitted rows are not deletions", () => Sync(() =>
{
    var data = new ResourceData(); CsvAdapter.Import(ResourceKind.Airports, data, [Row(("ICAO/IATA","ZBAA"),("Name","Beijing"))], true);
    Check(planner.Plan(ResourceKind.Airports, data).Count == 0); data.Draft[0].Fields["_delete"] = "TRUE";
    Check(planner.Plan(ResourceKind.Airports, data).Single().Kind == ChangeKind.Delete);
}));
await Test("File splitting respects UTF-8 byte limits", () => Sync(() =>
{
    var data = new ResourceData { Draft = Enumerable.Range(0,40).Select(i => Row(("ICAO/IATA",$"X{i:000}"),("Name",new string('中', 30)))).ToList() };
    var files = csv.Export(ResourceKind.Airports, planner.Plan(ResourceKind.Airports,data), 300);
    Check(files.Count > 1 && files.All(f => f.Length <= 300)); Check(files.Sum(f => csv.Parse(Encoding.UTF8.GetString(f)).Rows.Count) == 40);
}));
await Test("Rule preview is nonmutating; midnight and date shifts", () => Sync(() =>
{
    var row = Row(("Time","23:45"),("Date","2026-12-31 12:00:00"));
    Check(RuleEngine.Preview([row], new(RuleKind.ShiftMinutes,"Time","30"))[0].Get("Time") == "00:15"); Check(row.Get("Time") == "23:45");
    Check(RuleEngine.Preview([row], new(RuleKind.ShiftDays,"Date","1"))[0].Get("Date") == "2027-01-01 12:00:00");
}));
await Test("Validation detects references, duplicate IDs and immutable routing endpoints", () => Sync(() =>
{
    var w = new Workspace(); CsvAdapter.Import(ResourceKind.Routings, w.Resources[ResourceKind.Routings], [Row(("ID","1"),("Departure Airport (ICAO/IATA)","ZBAA"),("Arrival Airport (ICAO/IATA)","ZSPD"),("Route String","DCT"))], true);
    w.Resources[ResourceKind.Routings].Draft[0].Fields["Arrival Airport (ICAO/IATA)"] = "EGLL";
    var issues = Schemas.Validate(w, ResourceKind.Routings); Check(issues.Any(i => i.Message.Contains("不可修改"))); Check(issues.Any(i => i.Message.Contains("引用")));
    w.Resources[ResourceKind.Routings].Draft.Add(Row(("ID","1"))); Check(Schemas.Validate(w,ResourceKind.Routings).Any(i => i.Message.Contains("重复")));
}));
await Test("SQLite workspace isolation, persisted undo, and DPAPI", () => Sync(() =>
{
    var folder = Path.Combine(Path.GetTempPath(),"vamsys-test-"+Guid.NewGuid());
    var store = new WorkspaceStore(folder); var a = new Workspace { Name = "A" }; var b = new Workspace { Name = "B" };
    a.Resources[ResourceKind.Airports].Draft.Add(Row(("ICAO/IATA","ZBAA"))); a.Resources[ResourceKind.Airports].Checkpoint(); store.Save(a); store.Save(b);
    Check(store.Load(a.Id).Resources[ResourceKind.Airports].Undo.Count == 1); Check(store.Load(b.Id).Resources[ResourceKind.Airports].Draft.Count == 0);
    if (OperatingSystem.IsWindows()) { store.SaveSecret(a.Id,"secret"); Check(store.LoadSecret(a.Id)=="secret"); Check(store.LoadSecret(b.Id)==null); }
}));
await Test("Batch success and resume do not duplicate writes", async () =>
{
    var w = new Workspace(); var before = Row(("ICAO/IATA","ZBAA"),("Name","old")); CsvAdapter.Import(ResourceKind.Airports,w.Resources[ResourceKind.Airports],[before],true);
    var fake = new CountingService(new DemoService(w)); w.Resources[ResourceKind.Airports].Draft[0].Fields["Name"]="new";
    var job = new BatchJob { Items = planner.Plan(ResourceKind.Airports,w.Resources[ResourceKind.Airports]) };
    var engine = new BatchExecutor(fake,fake); await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None); await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None);
    Check(fake.Writes==1); Check(job.Items[0].State==ItemState.Succeeded);
});
await Test("Timeout after create stays unknown and never blindly retries", async () =>
{
    var fake = new CountingService(new DemoService(new Workspace())) { TimeoutWrites = true };
    var item = new ChangeItem { Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=Row(("Name","new")) }; var job=new BatchJob { Items=[item] };
    var engine = new BatchExecutor(fake,fake); await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None); await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None);
    Check(item.State==ItemState.Unknown); Check(fake.Writes==1);
});
await Test("Remote conflict blocks overwrite", async () =>
{
    var w = new Workspace(); CsvAdapter.Import(ResourceKind.Airports,w.Resources[ResourceKind.Airports],[Row(("ICAO/IATA","ZBAA"),("Name","remote"))],true);
    var fake=new CountingService(new DemoService(w)); var item=new ChangeItem { Resource=ResourceKind.Airports,Kind=ChangeKind.Update,Before=Row(("ICAO/IATA","ZBAA"),("Name","old")),After=Row(("ICAO/IATA","ZBAA"),("Name","new")) };
    await new BatchExecutor(fake,fake).ExecuteAsync(new BatchJob { Items=[item] },()=>Task.CompletedTask,CancellationToken.None); Check(item.State==ItemState.Conflict && fake.Writes==0);
});
await Test("Transport follows cursor pages, protects origin and reports auth", async () =>
{
    var handler = new FakeHttp(); using var http=new HttpClient(handler); var token=new TokenProvider(_=>Task.FromResult(new AccessToken("fake",DateTimeOffset.UtcNow.AddHours(1))));
    var transport=new OperationsTransport(http,new Uri("https://example.test"),Guid.NewGuid().ToString(),token);
    int count=0; await foreach(var r in transport.ReadPagesAsync(new Uri("https://example.test/routes"),CancellationToken.None)) count++;
    Check(count==2 && handler.Calls==2);
    try { await transport.GetAsync(new Uri("https://elsewhere.test/routes"),CancellationToken.None); throw new Exception("Origin accepted"); } catch(InvalidOperationException) { }
    handler.AuthFail=true; try { await transport.GetAsync(new Uri("https://example.test/routes"),CancellationToken.None); throw new Exception("Auth accepted"); } catch(ApiAccessException) { }
});
await Test("20,000-route planning and validation performance", () => Sync(() =>
{
    var w=new Workspace(); var d=w.Resources[ResourceKind.Routes];
    CsvAdapter.Import(ResourceKind.Airports,w.Resources[ResourceKind.Airports],[Row(("ICAO/IATA","ZBAA"),("Name","A")),Row(("ICAO/IATA","ZSPD"),("Name","B"))],true);
    d.Draft=Enumerable.Range(0,20000).Select(i=>Row(("ID",i.ToString()),("Departure Airport (ICAO/IATA)","ZBAA"),("Arrival Airport (ICAO/IATA)","ZSPD"),("Type","jumpseat"))).ToList(); d.Snapshot=d.Draft.Select(r=>r.Copy()).ToList();
    var sw=Stopwatch.StartNew(); Check(planner.Plan(ResourceKind.Routes,d).Count==0); Check(Schemas.Validate(w,ResourceKind.Routes).Count==0);
    Console.WriteLine($"  20k plan+validate: {sw.ElapsedMilliseconds} ms"); Check(sw.Elapsed < TimeSpan.FromSeconds(10));
}));
await Test("Baseline rejects missing IDs, and mixed CSV columns preserve omissions", () => Sync(() =>
{
    try { CsvAdapter.Import(ResourceKind.Fleets,new ResourceData(),[Row(("Name","new"))],true); throw new Exception("Invalid baseline accepted"); } catch(FormatException) { }
    var d = new ResourceData { Draft=[Row(("ICAO/IATA","ZBAA"),("Name","A"),("Unknown","x")),Row(("ICAO/IATA","ZSPD"),("Name","B"))] };
    var files=csv.Export(ResourceKind.Airports,planner.Plan(ResourceKind.Airports,d)); Check(files.Count==2);
    Check(!csv.Parse(Encoding.UTF8.GetString(files[1])).Headers.Contains("Unknown"));
}));
await Test("Cross-resource creates resolve returned IDs before dependent writes", async () =>
{
    var w=new Workspace(); var fleet=Row(("Name","New fleet")); var plane=Row(("Name","New plane"),("Fleet ID","local:"+fleet.LocalId));
    w.Resources[ResourceKind.Fleets].Draft.Add(fleet); w.Resources[ResourceKind.Aircraft].Draft.Add(plane);
    var job=new BatchJob { Items=planner.PlanWorkspace(w) }; var fake=new CountingService(new DemoService(w));
    await new BatchExecutor(fake,fake).ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None);
    Check(job.Items.All(i=>i.State==ItemState.Succeeded));
    Check(job.Items.Single(i=>i.Resource==ResourceKind.Aircraft).After.Get("Fleet ID")==job.Items.Single(i=>i.Resource==ResourceKind.Fleets).RemoteId);
});
await Test("Persist failure stops all writes", async () =>
{
    var fake=new CountingService(new DemoService(new Workspace())); var job=new BatchJob { Items=[new ChangeItem { Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=Row(("Name","x")) }] };
    try { await new BatchExecutor(fake,fake).ExecuteAsync(job,()=>throw new IOException("disk"),CancellationToken.None); throw new Exception("Persist failure swallowed"); } catch(PersistenceException) { }
    Check(fake.Writes==0);
});
await Test("Cancellation stops pending writes, failed dependency skips child", async () =>
{
    var parent=new ChangeItem { Resource=ResourceKind.Fleets,State=ItemState.Failed }; var child=new ChangeItem { Resource=ResourceKind.Aircraft,Dependencies=[parent.Id] };
    var fake=new CountingService(new DemoService(new Workspace())); var job=new BatchJob { Items=[parent,child] };
    await new BatchExecutor(fake,fake).ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None); Check(child.State==ItemState.Skipped && fake.Writes==0);
    var pending=new ChangeItem { Resource=ResourceKind.Fleets }; var cancelled=new BatchJob { Items=[pending] };
    await new BatchExecutor(fake,fake).ExecuteAsync(cancelled,()=>Task.CompletedTask,new CancellationToken(true)); Check(pending.State==ItemState.Pending && fake.Writes==0);
});
await Test("401/403 pauses batch without sending later rows", async () =>
{
    var fake=new AccessDeniedService(); var job=new BatchJob { Items=[new ChangeItem { Resource=ResourceKind.Fleets },new ChangeItem { Resource=ResourceKind.Fleets }] };
    await new BatchExecutor(fake,fake).ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None); Check(fake.Calls==1 && job.Status.Contains("暂停"));
});
await Test("429 honors Retry-After and retries without losing page", async () =>
{
    using var http = new HttpClient(new ThrottledHttp());
    var transport=new OperationsTransport(http,new Uri("https://example.test"),Guid.NewGuid().ToString(),new TokenProvider(_=>Task.FromResult(new AccessToken("fake",DateTimeOffset.UtcNow.AddHours(1)))));
    using var result=await transport.GetAsync(new Uri("https://example.test/routes"),CancellationToken.None); Check(result.RootElement.GetProperty("data").GetArrayLength()==0);
});
await Test("Recovered create with persisted remote ID verifies without repeat", async () =>
{
    var w=new Workspace(); var after=Row(("ID",""),("Name","new")); var item=new ChangeItem { Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=after,State=ItemState.Running,RemoteId="123" };
    var remote=after.Copy(); remote.Fields["ID"]="123"; w.Resources[ResourceKind.Fleets].Snapshot.Add(remote);
    var fake=new CountingService(new DemoService(w)); await new BatchExecutor(fake,fake).ExecuteAsync(new BatchJob { Items=[item] },()=>Task.CompletedTask,CancellationToken.None);
    Check(item.State==ItemState.Succeeded && fake.Writes==0);
});
await Test("Known local references resolve and unrelated remark text stays untouched", () => Sync(() =>
{
    var w=new Workspace(); var fleet=Row(("ID","1"),("Name","fleet")); CsvAdapter.Import(ResourceKind.Fleets,w.Resources[ResourceKind.Fleets],[fleet],true);
    w.Resources[ResourceKind.Aircraft].Draft.Add(Row(("Fleet ID","local:"+fleet.LocalId),("Internal Remarks","local: some note")));
    var item=planner.PlanWorkspace(w).Single(); Check(item.After.Get("Fleet ID")=="1" && item.After.Get("Internal Remarks")=="local: some note");
}));
await Test("Partial failure permits independent work; repaired dependency resumes child only", async () =>
{
    var fake=new CountingService(new DemoService(new Workspace())) { FailOnce=true };
    var parent=new ChangeItem { Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=Row(("Name","parent")) };
    var child=new ChangeItem { Resource=ResourceKind.Aircraft,Kind=ChangeKind.Create,After=Row(("Name","child")),Dependencies=[parent.Id] };
    var independent=new ChangeItem { Resource=ResourceKind.Routes,Kind=ChangeKind.Create,After=Row(("Name","independent")) };
    var job=new BatchJob { Items=[parent,child,independent] }; var engine=new BatchExecutor(fake,fake);
    await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None);
    Check(parent.State==ItemState.Failed && child.State==ItemState.Skipped && independent.State==ItemState.Succeeded);
    parent.State=ItemState.Pending; await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None);
    Check(job.Items.All(i=>i.State==ItemState.Succeeded) && fake.Writes==4);
});
await Test("Fleet sets preserve unresolved values and no-op formatting", () => Sync(() =>
{
    var w = new Workspace(); var service = new FleetAssignmentService();
    w.Resources[ResourceKind.Fleets].Draft.Add(Row(("ID","02"),("Name","A320"),("Type Code","A320")));
    var route = Row(("ID","1"),("Type","scheduled"),("Fleet IDs","missing, 01"));
    var add = service.Preview(w,ResourceKind.Routes,[route],["02"],FleetAssignmentMode.Add);
    Check(add.Issues.Count==0 && add.Rows.Single().Get("Fleet IDs")=="missing,01,02");
    Check(service.Preview(w,ResourceKind.Routes,[route],["01","missing"],FleetAssignmentMode.Replace).Rows.Count==0);
    Check(service.Options(w,["missing"]).Single(o=>o.Token=="missing").Available==false);
    var remove=service.Preview(w,ResourceKind.Routes,add.Rows,["missing"],FleetAssignmentMode.Remove);
    Check(remove.Rows.Single().Get("Fleet IDs")=="01,02");
}));
await Test("Fleet cardinality, unknown association and deleted row guards", () => Sync(() =>
{
    var w=new Workspace(); var s=new FleetAssignmentService();
    w.Resources[ResourceKind.Fleets].Draft.AddRange([Row(("ID","1")),Row(("ID","2"))]);
    Check(s.Preview(w,ResourceKind.Aircraft,[Row(("Fleet ID","1"))],["1","2"],FleetAssignmentMode.Replace).Issues.Count>0);
    Check(s.Preview(w,ResourceKind.Routes,[Row(("Type","scheduled"),("Fleet IDs","1"))],[],FleetAssignmentMode.Replace).Issues.Count>0);
    Check(s.Preview(w,ResourceKind.Routes,[Row(("Type","jumpseat"),("Fleet IDs","1"))],[],FleetAssignmentMode.Replace).Issues.Count==0);
    Check(s.Preview(w,ResourceKind.Routes,[Row(("Type","jumpseat"))],["missing"],FleetAssignmentMode.Add).Issues.Count>0);
    Check(s.Preview(w,ResourceKind.Routes,[Row(("_delete","TRUE"))],["1"],FleetAssignmentMode.Replace).Issues.Count>0);
}));
await Test("Fleet selection undo and CSV comma-list roundtrip", () => Sync(() =>
{
    var w=new Workspace(); var s=new FleetAssignmentService();
    w.Resources[ResourceKind.Fleets].Draft.AddRange([Row(("ID","001")),Row(("ID","002"))]);
    var d=w.Resources[ResourceKind.Routes]; CsvAdapter.Import(ResourceKind.Routes,d,[Row(("ID","01"),("Type","scheduled"),("Fleet IDs","001"))],true);
    s.Apply(d,s.Preview(w,ResourceKind.Routes,d.Draft,["002"],FleetAssignmentMode.Add));
    var exported=csv.Map(csv.Parse(Encoding.UTF8.GetString(csv.Export(ResourceKind.Routes,planner.Plan(ResourceKind.Routes,d)).Single())),new Dictionary<string,string>());
    Check(exported.Single().Get("Fleet IDs")=="001,002");
    d.UndoEdit(); Check(d.Draft.Single().Get("Fleet IDs")=="001"); d.RedoEdit(); Check(d.Draft.Single().Get("Fleet IDs")=="001,002");
}));
await Test("Local new fleet selection cannot leak into CSV", () => Sync(() =>
{
    var w=new Workspace(); var fleet=Row(("Name","New")); w.Resources[ResourceKind.Fleets].Draft.Add(fleet);
    var s=new FleetAssignmentService(); var d=w.Resources[ResourceKind.Aircraft]; d.Draft.Add(Row(("Name","Plane")));
    s.Apply(d,s.Preview(w,ResourceKind.Aircraft,d.Draft,["local:"+fleet.LocalId],FleetAssignmentMode.Replace));
    try { csv.Export(ResourceKind.Aircraft,planner.Plan(ResourceKind.Aircraft,d)); throw new Exception("Local reference leaked"); } catch(InvalidOperationException) { }
}));
await Test("Delete removes new drafts locally, marks existing, restores edits and undo", () => Sync(() =>
{
    var w=new Workspace(); var s=new DeletionService(); var d=w.Resources[ResourceKind.Fleets];
    CsvAdapter.Import(ResourceKind.Fleets,d,[Row(("ID","001"),("Name","before"))],true);
    var existing=d.Draft.Single(); existing.Fields["Name"]="edited"; var added=Row(("Name","new")); d.Draft.Add(added);
    s.Apply(w,s.Preview(w,ResourceKind.Fleets,d.Draft.Select(r=>r.LocalId)));
    Check(d.Draft.Count==1 && Schemas.Deleted(d.Draft.Single()));
    var changes=planner.Plan(ResourceKind.Fleets,d); Check(changes.Count==1 && changes[0].Kind==ChangeKind.Delete);
    var file=csv.Map(csv.Parse(Encoding.UTF8.GetString(csv.Export(ResourceKind.Fleets,changes).Single())),new Dictionary<string,string>()).Single();
    Check(file.Get("ID")=="001" && file.Get("_delete")=="TRUE");
    s.Restore(d,[existing.LocalId]); Check(d.Draft.Single().Get("Name")=="edited" && !Schemas.Deleted(d.Draft.Single()));
    d.UndoEdit(); Check(Schemas.Deleted(d.Draft.Single())); d.UndoEdit(); Check(d.Draft.Count==2);
}));
await Test("Deletion dependencies include local aliases, imported flags and stale preview", () => Sync(() =>
{
    var w=new Workspace(); var s=new DeletionService(); var fleet=Row(("ID","1"),("Name","fleet"));
    CsvAdapter.Import(ResourceKind.Fleets,w.Resources[ResourceKind.Fleets],[fleet],true);
    var plan=s.Preview(w,ResourceKind.Fleets,[fleet.LocalId]);
    var plane=Row(("ID","2"),("Fleet ID","local:"+fleet.LocalId)); w.Resources[ResourceKind.Aircraft].Draft.Add(plane);
    Check(s.Preview(w,ResourceKind.Fleets,[fleet.LocalId]).Issues.Count==1);
    try { s.Apply(w,plan); throw new Exception("Stale preview bypassed dependency"); } catch(InvalidOperationException) { }
    w.Resources[ResourceKind.Fleets].Draft.Single().Fields["_delete"]="TRUE";
    Check(Schemas.Validate(w,ResourceKind.Fleets).Any(i=>i.Field=="_delete"));
    plane.Fields["_delete"]="TRUE"; Check(s.CheckReferences(w,ResourceKind.Fleets,new HashSet<Guid>{fleet.LocalId}).Count==0);
}));
await Test("All five CSV deletion identifiers follow resource adapter", () => Sync(() =>
{
    foreach(var kind in Enum.GetValues<ResourceKind>())
    {
        var w=new Workspace(); var d=w.Resources[kind]; var key=Schemas.All[kind].Key;
        CsvAdapter.Import(kind,d,[Row((key,kind==ResourceKind.Airports?"ZBAA":"001"),("Name","original"))],true);
        var service=new DeletionService(); service.Apply(w,service.Preview(w,kind,d.Draft.Select(r=>r.LocalId)));
        var output=csv.Map(csv.Parse(Encoding.UTF8.GetString(csv.Export(kind,planner.Plan(kind,d)).Single())),new Dictionary<string,string>()).Single();
        Check(output.Get(key)==d.Draft.Single().Get(key) && output.Get("_delete")=="TRUE");
    }
}));
await Test("Documented capabilities available; legacy unverified adapter rejects writes", async () =>
{
    Check(ApiContracts.Capabilities.All(c=>c.CanRead && c.CanWrite));
    foreach(var kind in Enum.GetValues<ResourceKind>())
    {
        try { await new UnverifiedOperationsAdapter().WriteAsync(new ChangeItem { Resource=kind,Kind=ChangeKind.Delete },CancellationToken.None); throw new Exception("Unverified write accepted"); }
        catch(NotSupportedException) { }
    }
});
await Test("Localization catalogs have matching keys and named placeholders", () => Sync(() =>
{
    Dictionary<string,string> Catalog(System.Globalization.CultureInfo culture) => LocalizationService.Resources.GetResourceSet(culture,true,false)!.Cast<System.Collections.DictionaryEntry>().ToDictionary(p=>(string)p.Key,p=>(string)p.Value!);
    var zh=Catalog(System.Globalization.CultureInfo.InvariantCulture); var en=Catalog(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
    Check(zh.Keys.ToHashSet().SetEquals(en.Keys)); Check(zh.Count>=250);
    string[] Slots(string s)=>System.Text.RegularExpressions.Regex.Matches(s,@"\{\w+(?::[^}]+)?\}").Select(m=>m.Value).Order().ToArray();
    foreach(var key in zh.Keys) { Check(Slots(zh[key]).SequenceEqual(Slots(en[key])),key); Check(!string.IsNullOrWhiteSpace(en[key]),key); Check(!en[key].Any(c=>c>='\u4e00'&&c<='\u9fff'),key); }
}));
await Test("Language defaults, persistence, events and missing-key diagnostics", () => Sync(() =>
{
    var loc=new LocalizationService(); Check(loc.Language=="zh-CN"); var events=0; loc.LanguageChanged+=(_,_)=>events++;
    var originalCulture=System.Globalization.CultureInfo.CurrentCulture;
    loc.SetLanguage("en-US"); loc.SetLanguage("en-US"); Check(events==1 && loc.Get("Text_3797982942")=="Workspaces");
    Check(System.Globalization.CultureInfo.CurrentCulture==originalCulture);
    string? missing=null; loc.MissingResource+=key=>missing=key; Check(loc.Get("MissingTest")=="[MissingTest]" && missing=="MissingTest");
    loc.SetLanguage("invalid"); Check(loc.Language=="zh-CN" && events==2);
    var dir=Path.Combine(Path.GetTempPath(),"vamsys-language-"+Guid.NewGuid()); var s=new WorkspaceStore(dir);
    Check(s.LoadLanguage()=="zh-CN"); s.SaveLanguage("en-US"); Check(new WorkspaceStore(dir).LoadLanguage()=="en-US");
    s.SaveLanguage("invalid"); Check(s.LoadLanguage()=="zh-CN");
}));
await Test("Structured messages survive JSON and preserve literal user data", () => Sync(() =>
{
    var m=Messages.Define("Text_CCEF575C0D",("arg0",Schemas.All[ResourceKind.Routes].Name),("arg1","001"),("arg2","我的航空公司 {arg5}"),("arg3",""),("arg4","abcd"),("arg5","Fleet IDs"));
    var json=System.Text.Json.JsonSerializer.Serialize(m); var restored=System.Text.Json.JsonSerializer.Deserialize<MessageDescriptor>(json)!;
    var loc=new LocalizationService(); loc.SetLanguage("en-US"); var text=loc.Format(restored);
    Check(text.Contains("Routes 001") && text.Contains("我的航空公司 {arg5}") && text.EndsWith("Fleet IDs"));
    Check(loc.Legacy("已回读核对")=="Verified by read-back"); Check(loc.Legacy("历史外部错误 123")=="历史外部错误 123");
    var old=Workspace.Deserialize("{\"Name\":\"用户名称\",\"Jobs\":[{\"Status\":\"旧的动态状态 42\",\"Items\":[{\"Message\":\"待执行\"}]}]}");
    Check(old.Name=="用户名称" && old.Jobs[0].Description==null && loc.Legacy(old.Jobs[0].Status)=="旧的动态状态 42");
}));
await Test("CSV, validation, fleet assignment and deletion are language invariant", () => Sync(() =>
{
    var w=new Workspace(); CsvAdapter.Import(ResourceKind.Fleets,w.Resources[ResourceKind.Fleets],[Row(("ID","01"),("Name","用户机型")),Row(("ID","02"),("Name","Other"))],true);
    var d=w.Resources[ResourceKind.Routes]; CsvAdapter.Import(ResourceKind.Routes,d,[Row(("ID","001"),("Type","scheduled"),("Fleet IDs","01"),("Unknown","中文 {arg0},0001"))],true);
    var loc=new LocalizationService(); var before=w.Serialize();
    var service=new FleetAssignmentService(); var p1=service.Preview(w,ResourceKind.Routes,d.Draft,["02"],FleetAssignmentMode.Add); var issues1=Schemas.Validate(w,ResourceKind.Routes);
    loc.SetLanguage("en-US"); var p2=service.Preview(w,ResourceKind.Routes,d.Draft,["02"],FleetAssignmentMode.Add); var issues2=Schemas.Validate(w,ResourceKind.Routes);
    Check(w.Serialize()==before && p1.Rows[0].Get("Fleet IDs")==p2.Rows[0].Get("Fleet IDs"));
    Check(issues1.Select(i=>i.Description.Code).SequenceEqual(issues2.Select(i=>i.Description.Code)));
    service.Apply(d,p2); var english=csv.Export(ResourceKind.Routes,planner.Plan(ResourceKind.Routes,d)).Single(); loc.SetLanguage("zh-CN");
    Check(english.SequenceEqual(csv.Export(ResourceKind.Routes,planner.Plan(ResourceKind.Routes,d)).Single()));
    var ds=new DeletionService(); var a=ds.Preview(w,ResourceKind.Fleets,w.Resources[ResourceKind.Fleets].Draft.Select(r=>r.LocalId)); loc.SetLanguage("en-US");
    var b=ds.Preview(w,ResourceKind.Fleets,w.Resources[ResourceKind.Fleets].Draft.Select(r=>r.LocalId)); Check(a.Issues.Count==b.Issues.Count && a.Issues.Count==2);
    Check(!loc.Format(b.Issues[0].Description).Contains("仍被"));
}));
await Test("Language switch during execution does not resend writes or change task state", async () =>
{
    var job=new BatchJob{Items=[new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=Row(("Name","fleet"))}]};
    var loc=new LocalizationService();var fake=new CountingService(new DemoService(new Workspace()));
    await new BatchExecutor(fake,fake).ExecuteAsync(job,()=>{ loc.SetLanguage(loc.Language=="en-US"?"zh-CN":"en-US"); return Task.CompletedTask; },CancellationToken.None);
    Check(fake.Writes==1 && job.StatusCode==JobStatus.Completed && job.Description?.Code=="Text_140197D868");
    var restored=Workspace.Deserialize(new Workspace{Jobs=[job]}.Serialize()).Jobs.Single(); loc.SetLanguage("en-US");
    Check(loc.Format(restored.Description!)=="Completed and verified" && loc.Format(restored.Items[0].Description!)=="Verified by read-back");
    await new BatchExecutor(fake,fake).ExecuteAsync(restored,()=>Task.CompletedTask,CancellationToken.None); Check(fake.Writes==1);
});
await Test("Fleet labels translate generated annotations without translating names", () => Sync(() =>
{
    var w=new Workspace(); w.Resources[ResourceKind.Fleets].Draft.Add(Row(("Name","待删除"),("Type Code","A320")));
    var options=new FleetAssignmentService().Options(w,["missing"]); var loc=new LocalizationService(); loc.SetLanguage("en-US");
    Check(options[0].Describe(loc).Contains("待删除") && options[0].Describe(loc).Contains("Pending creation"));
    Check(options[1].Describe(loc).Contains("Unresolved fleet") && options[1].Describe(loc).Contains("Reference needs verification"));
}));
await ApiScenarios.Run(Test);
Console.WriteLine($"{passed} scenarios passed.");

sealed class CountingService(DemoService inner) : IResourceReader,IResourceWriter
{
    public int Writes; public bool TimeoutWrites; public bool FailOnce;
    public IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind k,CancellationToken ct)=>inner.ReadAllAsync(k,ct);
    public Task<DataRow?> FindAsync(ResourceKind k,string id,CancellationToken ct)=>inner.FindAsync(k,id,ct);
    public Task<string?> WriteAsync(ChangeItem c,CancellationToken ct) { Writes++; if(FailOnce) { FailOnce=false; throw new FormatException("invalid"); } if(TimeoutWrites) throw new TimeoutException(); return inner.WriteAsync(c,ct); }
}
sealed class FakeHttp : HttpMessageHandler
{
    public int Calls; public bool AuthFail;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Calls++; if(AuthFail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(Calls==1 ? "{\"data\":[{\"id\":1}],\"meta\":{\"next_cursor_url\":\"https://example.test/routes?cursor=2\"}}" : "{\"data\":[{\"id\":2}],\"meta\":{\"next_cursor_url\":null}}") });
    }
}
sealed class AccessDeniedService : IResourceReader,IResourceWriter
{
    public int Calls;
    public async IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind k,[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
    public Task<DataRow?> FindAsync(ResourceKind k,string id,CancellationToken ct)=>throw new ApiAccessException("denied");
    public Task<string?> WriteAsync(ChangeItem c,CancellationToken ct) { Calls++; throw new ApiAccessException("denied"); }
}
sealed class ThrottledHttp : HttpMessageHandler
{
    int calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var response=new HttpResponseMessage(++calls==1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) { Content=new StringContent("{\"data\":[]}") };
        response.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1)); return Task.FromResult(response);
    }
}
