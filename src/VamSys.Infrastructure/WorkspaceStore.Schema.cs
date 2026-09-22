using Microsoft.Data.Sqlite;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed partial class WorkspaceStore
{
    const int FormatVersion=3;
    internal Action<string>? Barrier { get; set; }
    public string? MigrationBackup { get; private set; }
    void Initialize(string directory)
    {
        using var c=Open();
        var version=Convert.ToInt32(Scalar(c,null,"PRAGMA user_version"));
        if(version>FormatVersion) throw OperationsAdapter.Block("StorageNewer");
        if(version==FormatVersion) { Execute(c,null,"PRAGMA journal_mode=WAL"); return; }
        var exists=Convert.ToInt32(Scalar(c,null,"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='workspaces'"))>0;
        if(exists)
        {
            MigrationBackup=Path.Combine(directory,"workspaces-before-v3-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".db");
            using var backup=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=MigrationBackup,Pooling=false}.ToString());
            backup.Open();c.BackupDatabase(backup);
            if(!Equals(Scalar(backup,null,"PRAGMA integrity_check"),"ok")) throw OperationsAdapter.Block("StorageBackupFailed");
        }
        Barrier?.Invoke("Migration:BeforeTransaction");
        using var tx=c.BeginTransaction();
        if(exists)
        {
            if(version<2)Execute(c,tx,"ALTER TABLE workspaces ADD COLUMN format_version INTEGER NOT NULL DEFAULT 3");
        }
        else Execute(c,tx,"CREATE TABLE workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,json TEXT NOT NULL,format_version INTEGER NOT NULL)");
        Execute(c,tx,"""
            CREATE TABLE IF NOT EXISTS secrets(id TEXT PRIMARY KEY,value BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS app_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS workspace_revisions(workspace_id TEXT PRIMARY KEY REFERENCES workspaces(id) ON DELETE CASCADE,revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS batch_jobs(workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,id TEXT NOT NULL,ordinal INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(workspace_id,id));
            CREATE TABLE IF NOT EXISTS batch_items(workspace_id TEXT NOT NULL,job_id TEXT NOT NULL,id TEXT NOT NULL,ordinal INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(workspace_id,job_id,id),FOREIGN KEY(workspace_id,job_id) REFERENCES batch_jobs(workspace_id,id) ON DELETE CASCADE);
            """);
        Execute(c,tx,"""
            CREATE TABLE resource_metadata(workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,resource INTEGER NOT NULL,columns_json TEXT NOT NULL,conflicts_json TEXT NOT NULL,snapshot_at TEXT,PRIMARY KEY(workspace_id,resource));
            CREATE TABLE resource_rows(workspace_id TEXT NOT NULL,resource INTEGER NOT NULL,collection INTEGER NOT NULL,local_id TEXT NOT NULL,ordinal INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(workspace_id,resource,collection,local_id),FOREIGN KEY(workspace_id,resource) REFERENCES resource_metadata(workspace_id,resource) ON DELETE CASCADE);
            CREATE UNIQUE INDEX resource_row_order ON resource_rows(workspace_id,resource,collection,ordinal);
            CREATE TABLE resource_history(workspace_id TEXT NOT NULL,resource INTEGER NOT NULL,undo_json TEXT NOT NULL,redo_json TEXT NOT NULL,PRIMARY KEY(workspace_id,resource),FOREIGN KEY(workspace_id,resource) REFERENCES resource_metadata(workspace_id,resource) ON DELETE CASCADE);
            """);
        var legacy=new List<(string Id,string Json)>();
        using(var cmd=Command(c,tx,"SELECT id,json FROM workspaces"))
        using(var reader=cmd.ExecuteReader()) while(reader.Read()) legacy.Add((reader.GetString(0),reader.GetString(1)));
        foreach(var row in legacy)
        {
            var w=Workspace.Deserialize(row.Json);
            if(w.Id.ToString()!=row.Id) throw OperationsAdapter.Block("StorageInvalid");
            if(version==2)ReadJobs(c,tx,w);
            var payload=Capture(w,false);
            Execute(c,tx,"UPDATE workspaces SET json=$json,format_version=3 WHERE id=$id",("$json",payload.Json),("$id",row.Id));
            if(version<2)Execute(c,tx,"INSERT INTO workspace_revisions VALUES($id,0)",("$id",row.Id));
            if(version<2)WriteJobs(c,tx,payload);WriteResources(c,tx,payload);
        }
        Execute(c,tx,"""
            CREATE TRIGGER workspace_format_insert BEFORE INSERT ON workspaces WHEN NEW.format_version!=3 BEGIN SELECT RAISE(ABORT,'unsupported workspace format'); END;
            CREATE TRIGGER workspace_format_update BEFORE UPDATE ON workspaces WHEN NEW.format_version!=3 BEGIN SELECT RAISE(ABORT,'unsupported workspace format'); END;
            """);
        Barrier?.Invoke("Migration:BeforeCommit");
        Execute(c,tx,"PRAGMA user_version=3");
        tx.Commit();
        Barrier?.Invoke("Migration:AfterCommit");
        Execute(c,null,"PRAGMA journal_mode=WAL");
    }
    static SqliteCommand Command(SqliteConnection c,SqliteTransaction? tx,string sql,params (string,object?)[] values)
    {
        var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;
        foreach(var (key,value) in values) cmd.Parameters.AddWithValue(key,value ?? DBNull.Value);
        return cmd;
    }
    static int Execute(SqliteConnection c,SqliteTransaction? tx,string sql,params (string,object?)[] values)
    {using var cmd=Command(c,tx,sql,values);return cmd.ExecuteNonQuery();}
    static object? Scalar(SqliteConnection c,SqliteTransaction? tx,string sql,params (string,object?)[] values)
    {using var cmd=Command(c,tx,sql,values);return cmd.ExecuteScalar();}
}
