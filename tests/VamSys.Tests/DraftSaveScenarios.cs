using System.Diagnostics;
using VamSys.Core;
using VamSys.Infrastructure;
using static StorageScenarios;

// Autosave of edited draft cells: only the edited rows (and undo history when a step was added) are written,
// and the database must still equal memory afterwards.
static class DraftSaveScenarios
{
    static void Assert(bool value,string text="Draft save assertion failed")=>StorageScenarios.Check(value,text);
    const string Touched="CREATE TABLE touched(tbl TEXT,resource INTEGER,local_id TEXT);"
        +"CREATE TRIGGER row_i AFTER INSERT ON resource_rows BEGIN INSERT INTO touched VALUES('rows',NEW.resource,NEW.local_id); END;"
        +"CREATE TRIGGER row_u AFTER UPDATE ON resource_rows BEGIN INSERT INTO touched VALUES('rows',NEW.resource,NEW.local_id); END;"
        +"CREATE TRIGGER row_d AFTER DELETE ON resource_rows BEGIN INSERT INTO touched VALUES('rows',OLD.resource,OLD.local_id); END;"
        +"CREATE TRIGGER history_u AFTER UPDATE ON resource_history_state BEGIN INSERT INTO touched VALUES('history',NEW.resource,NULL); END;"
        +"CREATE TRIGGER step_i AFTER INSERT ON resource_history_steps BEGIN INSERT INTO touched VALUES('step',NEW.resource,NEW.step_id); END;"
        +"CREATE TRIGGER step_u AFTER UPDATE ON resource_history_steps BEGIN INSERT INTO touched VALUES('step',NEW.resource,NEW.step_id); END;"
        +"CREATE TRIGGER step_d AFTER DELETE ON resource_history_steps BEGIN INSERT INTO touched VALUES('step',OLD.resource,OLD.step_id); END;"
        +"CREATE TRIGGER meta_u AFTER UPDATE ON resource_metadata BEGIN INSERT INTO touched VALUES('meta',NEW.resource,NULL); END;"
        +"CREATE TRIGGER jobs_u AFTER UPDATE ON batch_jobs BEGIN INSERT INTO touched VALUES('jobs',NULL,NULL); END;"
        +"CREATE TRIGGER jobs_d AFTER DELETE ON batch_jobs BEGIN INSERT INTO touched VALUES('jobs',NULL,NULL); END;"
        +"CREATE TRIGGER root_u AFTER UPDATE ON workspaces BEGIN INSERT INTO touched VALUES('root',NULL,NULL); END;";
    static Workspace Large(int rows,int undo)
    {
        var w=Fixture();var data=w.Resources[ResourceKind.Routes];
        for(var n=0;n<rows;n++){var row=new DataRow{Fields=new(){["ID"]=n.ToString(),["Flight Number"]="AC"+n,["Callsign"]="ACA123"}};data.Snapshot.Add(row.Copy());data.Draft.Add(row);}
        for(var n=0;n<undo;n++){var row=data.Draft[n%rows];data.Edit([row.LocalId],()=>row.Fields["Callsign"]="HISTORY"+n);}
        return w;
    }
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("Edited cells save only their rows and undo history, and reload equals memory (20k rows, 20 undo steps)",async()=>
        {
            var dir=Temp();var store=new WorkspaceStore(dir);var w=Large(20000,19);
            long fullBytes;double fullMs;
            using(var full=PerformanceRun.Begin(w.Id,Guid.NewGuid())){var watch=Stopwatch.StartNew();store.Save(w);fullMs=watch.Elapsed.TotalMilliseconds;fullBytes=full.JsonBytes;}
            using(var c=Open(dir))Sql(c,Touched);
            var data=w.Resources[ResourceKind.Routes];var edits=new DraftEdits();
            data.Edit([data.Draft[10].LocalId],()=>data.Draft[10].Fields["Flight Number"]="EDIT1",newStep:false);edits.Cell(w,ResourceKind.Routes,data.Draft[10].LocalId,false);
            data.Edit([data.Draft[19999].LocalId],()=>data.Draft[19999].Fields["Callsign"]="EDIT2",newStep:false);edits.Cell(w,ResourceKind.Routes,data.Draft[19999].LocalId,false);
            long rowBytes;double rowMs;
            using(var run=PerformanceRun.Begin(w.Id,Guid.NewGuid())){var watch=Stopwatch.StartNew();Assert(await store.TrySaveDraftRowsAsync(w,edits.Changes));rowMs=watch.Elapsed.TotalMilliseconds;rowBytes=run.JsonBytes;Assert(run.Checkpoints["Draft"]==1);}
            using(var c=Open(dir))
            {
                Assert(Convert.ToInt32(Sql(c,"SELECT count(*) FROM touched"))==3,"Unrelated rows written");
                Assert(Convert.ToInt32(Sql(c,"SELECT count(*) FROM touched WHERE tbl='rows' AND resource=$r AND local_id IN ($a,$b)",("$r",(int)ResourceKind.Routes),("$a",data.Draft[10].LocalId.ToString()),("$b",data.Draft[19999].LocalId.ToString())))==2);
                Sql(c,"DELETE FROM touched");
            }
            Assert(store.Load(w.Id).Serialize()==w.Serialize(),"Reload differs after row save");
            // A new undo step writes that resource's history too, and nothing else.
            data.Edit([data.Draft[5].LocalId],()=>data.Draft[5].Fields["Flight Number"]="EDIT3");edits.Take();edits.Cell(w,ResourceKind.Routes,data.Draft[5].LocalId,true);
            long historyBytes;using(var run=PerformanceRun.Begin(w.Id,Guid.NewGuid())){Assert(await store.TrySaveDraftRowsAsync(w,edits.Changes));historyBytes=run.JsonBytes;}
            using(var c=Open(dir))
            {
                Assert(Convert.ToInt32(Sql(c,"SELECT count(*) FROM touched"))==3&&Convert.ToInt32(Sql(c,"SELECT count(*) FROM touched WHERE tbl='history' AND resource=$r",("$r",(int)ResourceKind.Routes)))==1,"History write scope");
            }
            var disk=store.Load(w.Id);Assert(disk.Serialize()==w.Serialize()&&disk.Resources[ResourceKind.Routes].Undo.Count==20,"Reload differs after history save");
            Console.WriteLine($"  20k rows/20 undo: full save {fullBytes:N0} B {fullMs:F0} ms; 2 edited rows {rowBytes:N0} B {rowMs:F1} ms; with new undo step {historyBytes:N0} B");
            Assert(rowBytes<4096&&historyBytes<8192,"Row save serialized unrelated data");
        });
        await test("Row save refuses stale revisions and missing rows without writing",async()=>
        {
            var dir=Temp();var store=new WorkspaceStore(dir);var w=Fixture();store.Save(w);
            var other=store.Load(w.Id);other.Name="changed elsewhere";store.Save(other);
            var row=w.Resources[ResourceKind.Fleets].Draft[0];row.Fields["Name"]="stale edit";var edits=new DraftEdits();edits.Cell(w,ResourceKind.Fleets,row.LocalId,false);
            var revision=w.StorageRevision;
            try{await store.TrySaveDraftRowsAsync(w,edits.Changes);throw new Exception("Stale row save accepted");}catch(InvalidOperationException e){Assert(MessageErrors.Describe(e).Code=="StorageStale");}
            Assert(w.StorageRevision==revision&&store.Load(w.Id).Resources[ResourceKind.Fleets].Draft[0].Get("Name")!="stale edit");
            var fresh=store.Load(w.Id);var before=fresh.Serialize();var missing=new DraftEdits();missing.Cell(fresh,ResourceKind.Fleets,Guid.NewGuid(),true);
            Assert(!await store.TrySaveDraftRowsAsync(fresh,missing.Changes)&&store.Load(w.Id).Serialize()==before,"Missing row wrote data");
        });
        await test("Pending edits hand over to a save, return after failure and force full saves when unsure",()=>
        {
            var w=new Workspace();var id=Guid.NewGuid();var edits=new DraftEdits();
            Assert(!edits.CanSaveRows(w),"Empty edits allow a row save");
            edits.Cell(w,ResourceKind.Fleets,id,false);Assert(edits.CanSaveRows(w)&&!edits.CanSaveRows(new Workspace()));
            var taken=edits.Take();Assert(!edits.CanSaveRows(w)&&taken.CanSaveRows(w)&&taken.Changes.Single().Rows.Single()==id&&taken.Changes.Single().History!.Upserts.Length==0);
            var later=Guid.NewGuid();edits.Cell(w,ResourceKind.Routes,later,true);edits.Restore(taken);
            Assert(edits.RequiresFullSave&&!edits.CanSaveRows(w)&&edits.Changes.Count==2,"Failed save not merged back");
            var retried=edits.Take();Assert(retried.RequiresFullSave&&!edits.RequiresFullSave&&edits.Changes.Count==0);
            var switched=new DraftEdits();switched.Cell(w,ResourceKind.Fleets,id,false);switched.Cell(new Workspace(),ResourceKind.Fleets,id,false);
            Assert(switched.RequiresFullSave,"Edits from two workspaces allowed a row save");
            return Task.CompletedTask;
        });
    }
}
