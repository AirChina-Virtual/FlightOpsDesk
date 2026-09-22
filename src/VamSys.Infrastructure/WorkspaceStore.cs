using Microsoft.Data.Sqlite;
using VamSys.Core;
using System.Security.Cryptography;
using System.Text;

namespace VamSys.Infrastructure;

public sealed partial class WorkspaceStore
{
    readonly string connectionString;
    readonly object writeLock;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string,object> locks=new(StringComparer.OrdinalIgnoreCase);
    public WorkspaceStore(string directory) : this(directory,null) {}
    internal WorkspaceStore(string directory,Action<string>? barrier)
    {
        directory=Path.GetFullPath(directory);Directory.CreateDirectory(directory);
        connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"workspaces.db")}.ToString();
        writeLock=locks.GetOrAdd(directory,_=>new());Barrier=barrier;
        lock(writeLock) Initialize(directory);
    }
    SqliteConnection Open()
    {
        var c=new SqliteConnection(connectionString);c.Open();
        Execute(c,null,"PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
        return c;
    }
    public string LoadLanguage()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT value FROM app_settings WHERE key='language'";
        return cmd.ExecuteScalar() as string == "en-US" ? "en-US" : "zh-CN";
    }
    public void SaveLanguage(string language)
    {
        lock(writeLock) {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO app_settings VALUES('language',$value) ON CONFLICT(key) DO UPDATE SET value=$value";
        cmd.Parameters.AddWithValue("$value", language == "en-US" ? language : "zh-CN"); cmd.ExecuteNonQuery(); }
    }
    public List<(Guid Id, string Name)> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,name FROM workspaces ORDER BY name";
        using var r = cmd.ExecuteReader(); var result = new List<(Guid, string)>(); while (r.Read()) result.Add((Guid.Parse(r.GetString(0)), r.GetString(1))); return result;
    }
    public Workspace Load(Guid id)
    {
        using var c=Open();using var tx=c.BeginTransaction(deferred:true);
        var w=Workspace.Deserialize((string)(Scalar(c,tx,"SELECT json FROM workspaces WHERE id=$id",("$id",id.ToString())) ?? throw new KeyNotFoundException()));
        w.StorageRevision=Convert.ToInt64(Scalar(c,tx,"SELECT revision FROM workspace_revisions WHERE workspace_id=$id",("$id",id.ToString())));
        ReadJobs(c,tx,w);ReadResources(c,tx,w);
        tx.Commit();return w;
    }
    static void ReadJobs(SqliteConnection c,SqliteTransaction tx,Workspace w)
    {
        w.Jobs.Clear();
        using(var cmd=Command(c,tx,"SELECT json FROM batch_jobs WHERE workspace_id=$id ORDER BY ordinal",("$id",w.Id.ToString())))
        using(var reader=cmd.ExecuteReader()) while(reader.Read()) w.Jobs.Add(System.Text.Json.JsonSerializer.Deserialize<BatchJob>(reader.GetString(0))!);
        var jobs=w.Jobs.ToDictionary(j=>j.Id);
        using(var cmd=Command(c,tx,"SELECT job_id,json FROM batch_items WHERE workspace_id=$id ORDER BY job_id,ordinal",("$id",w.Id.ToString())))
        using(var reader=cmd.ExecuteReader()) while(reader.Read()) jobs[Guid.Parse(reader.GetString(0))].Items.Add(System.Text.Json.JsonSerializer.Deserialize<ChangeItem>(reader.GetString(1))!);
    }
    public void UpdateOperationsCredentials(Workspace workspace,string clientId,string secret)
        => SaveConnection(workspace,clientId,secret,true,null,null);
    public void SaveConnectionSettings(Workspace workspace,string clientId,string secret,string url,bool localRouteTimes)
    {
        if(workspace.Jobs.Any(j=>j.Mode==RunMode.Online && j.Items.Any(i=>i.State is ItemState.Pending or ItemState.Running or ItemState.Unknown))) throw OperationsAdapter.Block("ApiPendingJob");
        url=url.Trim();
        if(url!="" && (!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo!="")) throw OperationsAdapter.Block("ApiInvalid","URL");
        SaveConnection(workspace,clientId,secret,false,url,localRouteTimes);
    }
    void SaveConnection(Workspace workspace,string clientId,string secret,bool requireNewSecret,string? url,bool? localRouteTimes)
    {
        if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        clientId=clientId.Trim();
        OperationsCredentials? credentials=null;
        if(secret.Length>0)
        {
            if(clientId.Length==0 || string.IsNullOrWhiteSpace(secret)) throw OperationsAdapter.Block("ApiCredentials");
            credentials=new(clientId,secret); // Secret is opaque; never trim it.
        }
        else
        {
            if(requireNewSecret) throw OperationsAdapter.Block("ApiCredentials");
            if(LoadSecret(workspace.Id) is string raw)
            {
                var saved=System.Text.Json.JsonSerializer.Deserialize<OperationsCredentials>(raw) ?? throw OperationsAdapter.Block("ApiCredentials");
                if(saved.ClientId.Trim()!=clientId) throw OperationsAdapter.Block("ApiCredentials");
                credentials=new(clientId,saved.Secret);
            }
        }
        var staged=Workspace.Deserialize(workspace.Serialize());
        staged.ClientId=clientId; staged.Mode=RunMode.Offline; staged.ConnectedAt=null;
        if(url!=null) staged.InstanceUrl=url;
        if(localRouteTimes.HasValue) staged.LocalRouteTimes=localRouteTimes.Value;
        var encrypted=credentials==null ? null : ProtectedData.Protect(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(credentials)),workspace.Id.ToByteArray(),DataProtectionScope.CurrentUser);
        workspace.StorageRevision=SavePayload(Capture(staged),encrypted);
        workspace.ClientId=staged.ClientId;workspace.Mode=RunMode.Offline;workspace.ConnectedAt=null;
        workspace.InstanceUrl=staged.InstanceUrl;workspace.LocalRouteTimes=staged.LocalRouteTimes;
    }
    public void SaveSecret(Guid id, string secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), id.ToByteArray(), DataProtectionScope.CurrentUser);
        lock(writeLock) {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO secrets VALUES($id,$value) ON CONFLICT(id) DO UPDATE SET value=$value";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$value", encrypted); cmd.ExecuteNonQuery(); }
    }
    public string? LoadSecret(Guid id)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT value FROM secrets WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString());
        return cmd.ExecuteScalar() is byte[] bytes ? Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, id.ToByteArray(), DataProtectionScope.CurrentUser)) : null;
    }
}
