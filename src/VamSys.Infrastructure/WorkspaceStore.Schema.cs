using Microsoft.Data.Sqlite;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed partial class WorkspaceStore
{
    public const int FormatVersion=4;
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
            MigrationBackup=Path.Combine(directory,"workspaces-before-v4-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".db");
            using var backup=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=MigrationBackup,Pooling=false}.ToString());
            backup.Open();c.BackupDatabase(backup);
            if(!Equals(Scalar(backup,null,"PRAGMA integrity_check"),"ok")) throw OperationsAdapter.Block("StorageBackupFailed");
        }
        OnStorageBarrier("Migration:BeforeTransaction");
        using var tx=c.BeginTransaction();
        Execute(c,tx,"DROP TRIGGER IF EXISTS workspace_format_insert; DROP TRIGGER IF EXISTS workspace_format_update;");
        if(exists)
        {
            if(version<2)Execute(c,tx,"ALTER TABLE workspaces ADD COLUMN format_version INTEGER NOT NULL DEFAULT 4");
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
            CREATE TABLE IF NOT EXISTS resource_metadata(workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,resource INTEGER NOT NULL,columns_json TEXT NOT NULL,conflicts_json TEXT NOT NULL,snapshot_at TEXT,PRIMARY KEY(workspace_id,resource));
            CREATE TABLE IF NOT EXISTS resource_rows(workspace_id TEXT NOT NULL,resource INTEGER NOT NULL,collection INTEGER NOT NULL,local_id TEXT NOT NULL,ordinal INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(workspace_id,resource,collection,local_id),FOREIGN KEY(workspace_id,resource) REFERENCES resource_metadata(workspace_id,resource) ON DELETE CASCADE);
            CREATE UNIQUE INDEX IF NOT EXISTS resource_row_order ON resource_rows(workspace_id,resource,collection,ordinal);
            CREATE TABLE resource_history_state(workspace_id TEXT NOT NULL,resource INTEGER NOT NULL,undo_json TEXT NOT NULL,redo_json TEXT NOT NULL,PRIMARY KEY(workspace_id,resource),FOREIGN KEY(workspace_id,resource) REFERENCES resource_metadata(workspace_id,resource) ON DELETE CASCADE);
            CREATE TABLE resource_history_steps(workspace_id TEXT NOT NULL,resource INTEGER NOT NULL,step_id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(workspace_id,resource,step_id),FOREIGN KEY(workspace_id,resource) REFERENCES resource_metadata(workspace_id,resource) ON DELETE CASCADE);
            """);
        var ids=new List<string>();
        using(var cmd=Command(c,tx,"SELECT id FROM workspaces"))
        using(var reader=cmd.ExecuteReader()) while(reader.Read()) ids.Add(reader.GetString(0));
        foreach(var id in ids)
        {
            var json=(string)Scalar(c,tx,"SELECT json FROM workspaces WHERE id=$id",("$id",id))!;
            var w=Workspace.Deserialize(json);
            if(w.Id.ToString()!=id) throw OperationsAdapter.Block("StorageInvalid");
            if(version==3)
            {
                MigrateV3History(c,tx,w.Id);
                Execute(c,tx,"UPDATE workspaces SET format_version=4 WHERE id=$id",("$id",id));
                continue;
            }
            if(version==2)ReadJobs(c,tx,w);
            var payload=Capture(w,false);
            Execute(c,tx,"UPDATE workspaces SET json=$json,format_version=4 WHERE id=$id",("$json",payload.Json),("$id",id));
            if(version<2)Execute(c,tx,"INSERT INTO workspace_revisions VALUES($id,0)",("$id",id));
            if(version<2)WriteJobs(c,tx,payload);WriteResources(c,tx,payload);
        }
        Execute(c,tx,"""
            DROP TABLE IF EXISTS resource_history;
            CREATE TRIGGER workspace_format_insert BEFORE INSERT ON workspaces WHEN NEW.format_version!=4 BEGIN SELECT RAISE(ABORT,'unsupported workspace format'); END;
            CREATE TRIGGER workspace_format_update BEFORE UPDATE ON workspaces WHEN NEW.format_version!=4 BEGIN SELECT RAISE(ABORT,'unsupported workspace format'); END;
            """);
        OnStorageBarrier("Migration:BeforeCommit");
        Execute(c,tx,"PRAGMA user_version=4");
        tx.Commit();
        OnStorageBarrier("Migration:AfterCommit");
        Execute(c,null,"PRAGMA journal_mode=WAL");
    }
    static void MigrateV3History(SqliteConnection c,SqliteTransaction tx,Guid workspace)
    {
        foreach(var kind in Enum.GetValues<ResourceKind>())
        {
            var args=new (string,object?)[]{("$w",workspace.ToString()),("$r",(int)kind)};
            var data=new ResourceData();
            using(var cmd=Command(c,tx,"SELECT local_id,json FROM resource_rows WHERE workspace_id=$w AND resource=$r AND collection=1 ORDER BY ordinal",args))
            using(var reader=cmd.ExecuteReader())while(reader.Read())
            {
                var row=JsonSerializer.Deserialize<DataRow>(reader.GetString(1))??throw OperationsAdapter.Block("StorageInvalid");
                if(row.LocalId.ToString()!=reader.GetString(0))throw OperationsAdapter.Block("StorageInvalid");data.Draft.Add(row);
            }
            using(var cmd=Command(c,tx,"SELECT undo_json,redo_json FROM resource_history WHERE workspace_id=$w AND resource=$r",args))
            using(var reader=cmd.ExecuteReader())
            {
                if(!reader.Read())throw OperationsAdapter.Block("StorageInvalid");
                data.History=EditHistory.ConvertLegacy(data.Draft,JsonSerializer.Deserialize<List<List<DataRow>>>(reader.GetString(0))??throw OperationsAdapter.Block("StorageInvalid"),JsonSerializer.Deserialize<List<List<DataRow>>>(reader.GetString(1))??throw OperationsAdapter.Block("StorageInvalid"));
            }
            WriteHistory(c,tx,workspace,kind,CaptureHistory(data.History,full:true),full:true);
        }
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
