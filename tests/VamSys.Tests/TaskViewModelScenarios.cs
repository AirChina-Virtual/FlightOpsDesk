using VamSys.Core;
using VamSys.App;
static class TaskViewModelScenarios
{
    static void Check(bool ok){if(!ok)throw new Exception("Task view model assertion failed");}
    static TaskJobViewModel Model(BatchJob job,LocalizationService language)=>new(job,language,()=>true,()=>RunMode.Online,()=>Task.CompletedTask,()=>Task.CompletedTask,()=>Task.CompletedTask,()=>Task.CompletedTask);
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("Task presentation never observes mutable uncommitted status or item changes",()=>{
            var l=new LocalizationService();var item=new ChangeItem{State=ItemState.Pending};var job=new BatchJob{Mode=RunMode.Online,Items=[item],Status="committed"};
            var vm=Model(job,l);var list=vm.Items;var text=list[0].Text;var title=vm.Title;var notifications=0;vm.PropertyChanged+=(_,_)=>notifications++;
            item.State=ItemState.Succeeded;item.Message="not committed";job.Status="not committed";
            vm.RefreshHeader();Check(list[0].Text==text&&vm.Title==title);
            notifications=0;vm.Update(item.Id,false);Check(notifications==0&&vm.Title.Contains("not committed"));
            vm.Present(item.Id);vm.RefreshHeader();Check(list[0].Text.Contains("not committed")&&ReferenceEquals(list,vm.Items));return Task.CompletedTask;
        });
        await test("Task counters remain correct for repeated updates, replacement candidates and rebasing",()=>{
            var l=new LocalizationService();var item=new ChangeItem{State=ItemState.Unknown,Resource=ResourceKind.Airports};var job=new BatchJob{Mode=RunMode.Online,Items=[item]};var vm=Model(job,l);
            Check(vm.Review.CanExecute(null)&&vm.Resume.CanExecute(null));var list=vm.Items;
            job.Items[0]=new(){Id=item.Id,State=ItemState.Succeeded,Rebased=false};vm.Update(item.Id);vm.Update(item.Id);
            Check(!vm.Review.CanExecute(null)&&vm.Resume.CanExecute(null));
            job.Items[0].Rebased=true;vm.Update(item.Id);Check(!vm.Resume.CanExecute(null));
            l.SetLanguage("en-US");vm.RefreshLanguage();Check(ReferenceEquals(list,vm.Items)&&list[0].Text.Contains("Succeeded",StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        });
        await test("Large task updates notify only the changed job and detail row",()=>{
            var l=new LocalizationService();var models=Enumerable.Range(0,1000).Select(_=>Model(new(){Items=Enumerable.Range(0,20).Select(_=>new ChangeItem()).ToList()},l)).ToList();
            var job=new BatchJob{Items=Enumerable.Range(0,20000).Select(_=>new ChangeItem()).ToList()};var active=Model(job,l);models.Add(active);var details=active.Items;
            var unrelated=0;foreach(var vm in models.Take(1000))vm.PropertyChanged+=(_,_)=>unrelated++;
            var changed=0;foreach(var vm in details)vm.PropertyChanged+=(_,_)=>changed++;
            for(var n=0;n<1000;n++){var item=job.Items[n];item.State=ItemState.Succeeded;active.Update(item.Id,false);active.Present(item.Id);active.RefreshHeader();}
            Check(unrelated==0&&changed==1000&&ReferenceEquals(details,active.Items));return Task.CompletedTask;
        });
    }
}