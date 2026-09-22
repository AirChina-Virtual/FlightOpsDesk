using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using static StorageScenarios;

static class StorageProcessScenarios
{
    sealed record Wire(string Type,string? Point=null,string? Method=null,string? Path=null,string? Body=null,int Code=200);
    sealed class Remote
    {
        public int Posts,Puts;public bool Created,Completed;
        const string Existing="""{"id":4,"airline_id":7,"name":"Existing","code":"B738","type":"pax"}""";
        public string Record=>"""{"id":10,"airline_id":7,"name":"New","code":"B738","type":"pax","max_pax":"""+(Completed?"180":"0")+"}";
        public Wire Respond(Wire request)
        {
            if(request.Method=="POST"){Posts++;Created=true;return new("response",Body:Record,Code:201);}
            if(request.Method=="PUT"){Puts++;Completed=true;return new("response",Body:"{\"data\":"+Record+"}");}
            StorageScenarios.Check(request.Method=="GET","Unexpected write method");
            if(request.Path!.Split('?')[0].EndsWith("/10"))return Created?new("response",Body:"{\"data\":"+Record+"}"):new("response",Body:"{}",Code:404);
            return new("response",Body:"{\"data\":["+Existing+(Created?","+Record:"")+"],\"meta\":{\"next_cursor_url\":null}}");
        }
    }
    sealed class PipeHttp(StreamReader reader,StreamWriter writer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire("request",Method:request.Method.Method,Path:request.RequestUri!.AbsolutePath,Body:request.Content==null?null:await request.Content.ReadAsStringAsync(ct))));
            var reply=JsonSerializer.Deserialize<Wire>((await reader.ReadLineAsync(ct))!)!;
            return new((HttpStatusCode)reply.Code){Content=new StringContent(reply.Body??"{}")};
        }
    }
    public static async Task Child(string[] args)
    {
        using var pipe=new NamedPipeClientStream(".",args[2],PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(15000);
        using var reader=new StreamReader(pipe);using var writer=new StreamWriter(pipe){AutoFlush=true};
        Workspace? w=null;
        void Barrier(string point)
        {
            var i=w?.Jobs[0].Items[0];
            var detail=i==null?"migration":JsonSerializer.Serialize(new{state=i.State.ToString(),i.RemoteId,i.CreateCompleted,steps=i.ApiSteps.Select(s=>new{method=s.Method,state=s.State.ToString(),s.Path}).ToArray()});
            writer.WriteLine(JsonSerializer.Serialize(new Wire("barrier",Point:point,Body:detail)));
            if(reader.ReadLine()==null)throw new IOException("Parent closed pipe");
        }
        using var lease=new DataDirectoryLease(args[1]);
        var store=new WorkspaceStore(args[1],Barrier);w=store.Load(Guid.Parse(args[3]));
        var clock=new TestTime();using var client=new HttpClient(new PipeHttp(reader,writer));
        var api=new OperationsAdapter(new OperationsTransport(client,OperationsAdapter.BaseUri,"process-test",new TokenProvider(_=>Task.FromResult(new AccessToken("test",clock.GetUtcNow().AddHours(1))),clock),new(clock)),w);
        await clock.Drive(new OperationsBatchExecutor(api,w).ExecuteAsync(w.Jobs[0],store.CheckpointWriter(w),default));
        await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire("done")));
    }
    static async Task Launch(string directory,Guid id,Remote remote,Func<Wire,bool>? killAt=null)
    {
        var name="vamsys-test-"+Guid.NewGuid().ToString("N");
        using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        var psi=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet",StringComparison.OrdinalIgnoreCase))psi.ArgumentList.Add(typeof(StorageProcessScenarios).Assembly.Location);
        foreach(var arg in new[]{"--storage-child",directory,name,id.ToString()})psi.ArgumentList.Add(arg);
        using var process=Process.Start(psi)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        bool killed=false,done=false;
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader=new StreamReader(pipe);using var writer=new StreamWriter(pipe){AutoFlush=true};
            while(await reader.ReadLineAsync(timeout.Token) is string line)
            {
                var message=JsonSerializer.Deserialize<Wire>(line)!;
                if(killAt?.Invoke(message)==true)
                {process.Kill(true);await process.WaitForExitAsync(timeout.Token);killed=true;break;}
                if(message.Type=="done"){done=true;break;}
                var reply=message.Type=="request"?remote.Respond(message):new Wire("ack");
                if(message.Type=="request"&&killAt?.Invoke(new Wire("accepted",Method:message.Method))==true){process.Kill(true);await process.WaitForExitAsync(timeout.Token);killed=true;break;}
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply));
            }
            await process.WaitForExitAsync(timeout.Token);
            StorageScenarios.Check(killAt!=null?killed:done&&process.ExitCode==0,"Child failed: "+await stdout+await stderr);
        }
        finally {if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
    }
    static bool Match(Wire wire,string boundary)
    {
        if(boundary=="PUT:Accepted")return wire.Type=="accepted"&&wire.Method=="PUT";
        if(wire.Type!="barrier")return false;
        if(boundary.StartsWith("Migration")||boundary.StartsWith("Rebase"))return wire.Point==boundary;
        using var body=JsonDocument.Parse(wire.Body!);var root=body.RootElement;
        if(boundary.StartsWith("Task"))return wire.Point==boundary&&root.GetProperty("state").GetString()=="Running";
        if(wire.Point!=boundary.Split('|')[0])return false;
        var steps=root.GetProperty("steps");var method=boundary.Split('|')[1];var state=boundary.Split('|')[2];
        return steps.EnumerateArray().Any(s=>s.GetProperty("method").GetString()==method&&s.GetProperty("state").GetString()==state);
    }
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        foreach(var boundary in new[]{
            "Task:BeforeCommit","Task:AfterCommit",
            "Step:BeforeCommit|POST|InFlight",
            "Step:BeforeCommit|POST|ResponseReceived","Step:AfterCommit|POST|ResponseReceived",
            "Step:BeforeCommit|PUT|InFlight","Step:AfterCommit|PUT|InFlight","PUT:Accepted",
            "Step:BeforeCommit|PUT|ResponseReceived",
            "Rebase:BeforeCommit","Rebase:AfterCommit"})
        await test("Real process termination and read-only recovery: "+boundary,async()=>{
            var dir=Temp();var w=Fixture();var store=new WorkspaceStore(dir);store.Save(w);var remote=new Remote();
            await Launch(dir,w.Id,remote,m=>Match(m,boundary));
            var interrupted=store.Load(w.Id);var item=interrupted.Jobs[0].Items[0];
            var beforeWrite=boundary=="Task:BeforeCommit";
            var durableRebase=boundary=="Rebase:AfterCommit";
            StorageScenarios.Check((item.State==ItemState.Succeeded)==durableRebase);
            StorageScenarios.Check(interrupted.Resources[ResourceKind.Fleets].Snapshot.Count==(durableRebase?1:0));
            StorageScenarios.Check(interrupted.Resources[ResourceKind.Fleets].Undo.Count==(durableRebase?0:1));
            if(boundary.Contains("POST|ResponseReceived"))
            {
                StorageScenarios.Check(remote.Posts==1&&remote.Puts==0);
                StorageScenarios.Check(item.CreateCompleted==boundary.StartsWith("Step:AfterCommit"));
                StorageScenarios.Check((item.RemoteId=="10")==item.CreateCompleted);
            }
            if(boundary.Contains("PUT|InFlight"))
            {
                StorageScenarios.Check(item.RemoteId=="10"&&item.CreateCompleted&&remote.Puts==0);
                StorageScenarios.Check(item.ApiSteps[1].State==(boundary.StartsWith("Step:AfterCommit")?BatchStepState.InFlight:BatchStepState.Prepared));
            }
            var posts=remote.Posts;var puts=remote.Puts;
            await Launch(dir,w.Id,remote);
            var recovered=store.Load(w.Id);item=recovered.Jobs[0].Items[0];
            if(beforeWrite)StorageScenarios.Check(remote.Posts==1&&remote.Puts==1&&item.State==ItemState.Succeeded,$"POST={remote.Posts} PUT={remote.Puts} state={item.State} message={item.Message} diagnostic={item.Diagnostic} expected={JsonSerializer.Serialize(item.ApiExpectedValues)}");
            else
            {
                StorageScenarios.Check(remote.Posts==posts&&remote.Puts==puts,"Recovery repeated an uncertain write");
                StorageScenarios.Check(item.State==(remote.Completed?ItemState.Succeeded:ItemState.Unknown));
                await Launch(dir,w.Id,remote);
                StorageScenarios.Check(remote.Posts==posts&&remote.Puts==puts,"Second recovery repeated a write");
                if(remote.Created&&!remote.Completed)
                {
                    // A separately verified remote repair can make the existing request verifiable.
                    remote.Completed=true;recovered=store.Load(w.Id);item=recovered.Jobs[0].Items[0];
                    if(item.CanProposeRecoveryId)
                    {
                        item.ProposeRecoveryId("10");
                        await store.CheckpointWriter(recovered).WriteAsync(new(CheckpointKind.Task,recovered.Jobs[0],item),default);
                    }
                    await Launch(dir,w.Id,remote);StorageScenarios.Check(store.Load(w.Id).Jobs[0].Items[0].State==ItemState.Succeeded);
                    StorageScenarios.Check(remote.Posts==posts&&remote.Puts==puts);
                }
            }
            var final=store.Load(w.Id);
            if(final.Jobs[0].Items[0].State==ItemState.Succeeded)
            {
                StorageScenarios.Check(final.Resources[ResourceKind.Fleets].Snapshot.Single().Identity!.RemoteId=="10");
                StorageScenarios.Check(final.Resources[ResourceKind.Fleets].Draft.Single().Get("Unknown")=="001");
                StorageScenarios.Check(final.Resources[ResourceKind.Fleets].Undo.Count==0&&final.VerifiedOperations.Contains("Fleets:Create"));
            }
        });
        foreach(var boundary in new[]{"Migration:BeforeCommit","Migration:AfterCommit"})
        await test("Real process termination during SQLite migration: "+boundary,async()=>{
            var dir=Temp();var w=Fixture();using(var legacy=Legacy(dir,w)){}
            var remote=new Remote();await Launch(dir,w.Id,remote,m=>Match(m,boundary));
            using(var c=Open(dir))StorageScenarios.Check(Convert.ToInt32(Sql(c,"PRAGMA user_version"))==(boundary.EndsWith("AfterCommit")?3:0));
            var store=new WorkspaceStore(dir);StorageScenarios.Check(store.Load(w.Id).Jobs[0].Items[0].State==ItemState.Pending&&remote.Posts==0);
            StorageScenarios.Check(Directory.GetFiles(dir,"workspaces-before-v3-*.db").Length>=1);
        });
    }
}