using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VamSys.Core;
using VamSys.Infrastructure;
static class CredentialSaveScenarios
{
    static void Check(bool value){if(!value)throw new Exception("Credential save assertion failed");}
    static WorkspaceStore Store(out string directory){directory=Path.Combine(Path.GetTempPath(),"vamsys-save-"+Guid.NewGuid());return new(directory);}
    static void Save(WorkspaceStore s,Workspace w,bool only,string id,string secret)
    {if(only)s.UpdateOperationsCredentials(w,id,secret);else s.SaveConnectionSettings(w,id,secret,OperationsAdapter.BaseUri.AbsoluteUri,true);}
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        foreach(var only in new[]{false,true})
            await test("Both credential entries normalize OAuth ID and roll back failed saves: "+only,async()=>
            {
                var s=Store(out var directory);var w=new Workspace{AirlineId="7"};s.Save(w);
                Save(s,w,only," 123 "," secret ");
                var c=JsonSerializer.Deserialize<OperationsCredentials>(s.LoadSecret(w.Id)!)!;
                Check(w.ClientId=="123" && s.Load(w.Id).ClientId=="123" && c.ClientId=="123" && c.Secret==" secret ");
                var h=new AuditHttp();h.Next=(r,ct)=>
                {
                    var fields=r.Content!.ReadAsStringAsync().Result.Split('&');Check(fields.Contains("client_id=123") && fields.Contains("client_secret=+secret+"));
                    return new(HttpStatusCode.OK){Content=new StringContent("""{"access_token":"test","token_type":"Bearer","expires_in":60}""")};
                };
                await TokenProvider.ClientCredentials(new HttpClient(h),c).GetAsync(default);
                var before=w.Serialize();var secret=s.LoadSecret(w.Id);
                using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"workspaces.db")}.ToString()))
                {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="CREATE TRIGGER fail_save BEFORE UPDATE ON workspaces BEGIN SELECT RAISE(ABORT,'injected failure'); END;";cmd.ExecuteNonQuery();}
                try{Save(s,w,only,"456","new-secret");throw new Exception("Failure not injected");}catch(SqliteException){}
                Check(w.Serialize()==before && s.Load(w.Id).Serialize()==before && s.LoadSecret(w.Id)==secret);
            });
        await test("Ordinary save retains Secret and repairs legacy padded client IDs",()=>
        {
            var s=Store(out _);var w=new Workspace{ClientId="123"};s.Save(w);
            s.SaveSecret(w.Id,JsonSerializer.Serialize(new OperationsCredentials(" 123 "," original ")));
            s.SaveConnectionSettings(w," 123 ","",OperationsAdapter.BaseUri.AbsoluteUri,false);
            var creds=JsonSerializer.Deserialize<OperationsCredentials>(s.LoadSecret(w.Id)!)!;Check(creds.ClientId=="123" && creds.Secret==" original ");
            var before=w.Serialize();var secret=s.LoadSecret(w.Id);
            try{s.SaveConnectionSettings(w,"456","",OperationsAdapter.BaseUri.AbsoluteUri,true);throw new Exception("Changed ID accepted");}catch(InvalidOperationException){}
            Check(w.Serialize()==before && s.Load(w.Id).Serialize()==before && s.LoadSecret(w.Id)==secret);
            try{s.UpdateOperationsCredentials(w,"123","");throw new Exception("Rotation accepted empty Secret");}catch(InvalidOperationException){}
            return Task.CompletedTask;
        });
        await test("Full settings stay blocked during a task while credentials-only save preserves it",()=>
        {
            var s=Store(out _);var w=new Workspace{AirlineId="7"};w.Jobs.Add(new(){Mode=RunMode.Online,Items=[new(){State=ItemState.Unknown}]});s.Save(w);
            var jobs=JsonSerializer.Serialize(w.Jobs);
            try{s.SaveConnectionSettings(w,"123","secret",OperationsAdapter.BaseUri.AbsoluteUri,true);throw new Exception("Task settings changed");}catch(InvalidOperationException e){Check(MessageErrors.Describe(e).Code=="ApiPendingJob");}
            s.UpdateOperationsCredentials(w," 123 ","secret");Check(w.AirlineId=="7" && !w.LocalRouteTimes && JsonSerializer.Serialize(w.Jobs)==jobs);return Task.CompletedTask;
        });
    }
}
