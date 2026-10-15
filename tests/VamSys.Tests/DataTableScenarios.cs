using System.Collections.Immutable;
using VamSys.App;
using VamSys.Core;

static class DataTableScenarios
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Cell label notifications skip unchanged text and preserve language updates", () =>
        {
            var label="Fleet";var name="Choose";var notices=new List<string?>();
            var cell=new CellViewModel("Fleet IDs","1",_=>{}){DisplayText=()=>label,FleetName=()=>name};
            cell.PropertyChanged+=(_,args)=>notices.Add(args.PropertyName);
            cell.RefreshLanguage();Check(notices.Count==2,"Initial labels missing");
            cell.RefreshLanguage();Check(notices.Count==2,"Unchanged labels notified");
            label="机型";name="选择";cell.RefreshLanguage();Check(notices.Count==4&&cell.DisplayValue==label&&cell.FleetAutomationName==name,"Language change lost");
            return Task.CompletedTask;
        });
        await test("Reused page cells synchronize without commits and edit the current row only", () =>
        {
            var original=new DataRow{Fields=new(){["Remarks"]="original"}};
            var replacement=new DataRow{Fields=new(){["Remarks"]="replacement"}};
            var commits=0;RowViewModel? model=null;
            var cell=new CellViewModel("Remarks","original",value=>{commits++;model!.Row.Fields["Remarks"]=value;}){CanEdit=()=>!Schemas.Deleted(model!.Row)};
            model=new(original,[cell]);model.Rebind(replacement);
            Check(commits==0&&cell.Value=="replacement"&&ReferenceEquals(model.Row,replacement),"Rebind produced an edit");
            cell.Value="typed";Check(commits==1&&replacement.Get("Remarks")=="typed"&&original.Get("Remarks")=="original","Reused callback edited previous page");
            replacement.Fields["_delete"]="TRUE";model.Rebind(replacement);cell.Value="rejected";
            Check(cell.IsReadOnly&&commits==1&&replacement.Get("Remarks")=="typed","Deleted row accepted input");
            return Task.CompletedTask;
        });
        await test("UI fixture refuses existing files before creating or modifying a database", () =>
        {
            var directory=Path.Combine(Path.GetTempPath(),"vamsys-seed-refusal-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            var marker=Path.Combine(directory,"existing.txt");File.WriteAllText(marker,"must remain unchanged");
            try{DataUiFixture.Seed(directory,20000);throw new Exception("Existing directory accepted");}catch(InvalidOperationException){}
            Check(File.ReadAllText(marker)=="must remain unchanged"&&Directory.GetFiles(directory).Length==1,"Existing files changed");
            File.Delete(marker);Directory.Delete(directory);
            return Task.CompletedTask;
        });
        await test("Data paging keeps bounded pages, anchors page size and clamps a shortened last page", () =>
        {
            var model = new DataTableViewModel(); var ids = Enumerable.Range(0,1001).Select(_ => Guid.NewGuid()).ToArray();
            model.Apply(new(ids, 0), "", 0, null);
            Check(model.PageSize == 200 && model.PageCount == 6 && model.PageIds().Length == 200, "Default page changed");
            model.SetPage(6); Check(model.PageIds().Single() == ids[1000], "Last page wrong");
            model.SetPage(3); model.SetPageSize(100); Check(model.Page == 5 && model.PageIds()[0] == ids[400], "Page anchor lost");
            model.SetPageSize(500); Check(model.Page == 1 && model.PageIds().Length == 500, "Large page wrong");
            model.SetPage(3); model.Apply(new(ids.Take(400).ToArray(),0),"",0,null); Check(model.Page == 1, "Removed page not clamped");
            model.Apply(new([],0),"",0,null); Check(model.Page == 1 && model.PageCount == 1 && model.PageIds().Length == 0 && model.First == 0, "Empty result wrong");
            return Task.CompletedTask;
        });
        await test("Cross-page selection persists on paging and sorting and clears when search or filter changes", () =>
        {
            var model=new DataTableViewModel(); var ids=Enumerable.Range(0,501).Select(_=>Guid.NewGuid()).ToArray(); model.Apply(new(ids,0),"",0,null);
            model.SelectPage([ids[0]]); model.SetPage(2); model.SelectPage([ids[200]]);
            Check(model.Selected.SetEquals([ids[0],ids[200]]),"Cross-page selection lost");
            model.SelectPage([]); Check(model.Selected.SetEquals([ids[0]]),"Deselecting page changed another page");
            model.Apply(new(ids.Reverse().ToArray(),0),"",0,"ID"); Check(model.Selected.Contains(ids[0]),"Sorting cleared selection");
            model.SelectAll(); Check(model.Selected.Count==501 && model.PageIds().Length<=model.PageSize,"All selection materialized pages");
            model.Apply(new(ids,0),"changed",0,"ID"); Check(model.Selected.Count==0 && model.Page==1,"New search kept hidden selection");
            model.SelectPage([ids[0]]); model.Apply(new(ids,0),"changed",1,"ID"); Check(model.Selected.Count==0,"New filter kept selection");
            return Task.CompletedTask;
        });
        await test("Full-data queries preserve search, deleted flags, stable sort and cancellation", () =>
        {
            var rows = Enumerable.Range(0,50000).Select(n=>new DataQueryRow(Guid.NewGuid(),ImmutableArray.Create("row "+n,n==49999?"Needle":"value"),n%2==0?"same":"SAME",n%10==0,n%17==0)).ToArray();
            var all=DataTableViewModel.Query(rows,"",0,true,CancellationToken.None);
            Check(all.RowIds.SequenceEqual(rows.Select(r=>r.LocalId)),"Equal-key order changed");
            var found=DataTableViewModel.Query(rows,"needle",0,false,CancellationToken.None);
            Check(found.RowIds.Single()==rows[^1].LocalId,"Search limited to current page");
            Check(DataTableViewModel.Query(rows,"",1,false,CancellationToken.None).RowIds.Length==5000,"Modified filter wrong");
            Check(DataTableViewModel.Query(rows,"",2,false,CancellationToken.None).RowIds.Length==rows.Count(r=>r.Deleted),"Deleted filter wrong");
            using var canceled=new CancellationTokenSource(); canceled.Cancel();
            try { DataTableViewModel.Query(rows,"",0,true,canceled.Token); throw new Exception("Cancellation ignored"); } catch(OperationCanceledException) {}
            return Task.CompletedTask;
        });
    }
}
