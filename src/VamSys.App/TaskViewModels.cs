using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VamSys.Core;
namespace VamSys.App;

public sealed record TaskItemDisplay(Guid Id,ResourceKind Resource,string Key,ItemState State,bool Rebased,MessageDescriptor? Description,string Message,string? Diagnostic)
{
    public static TaskItemDisplay Capture(ChangeItem i)=>new(i.Id,i.Resource,Schemas.Key(i.Resource,i.After),i.State,i.Rebased,i.Description,i.Message,i.Diagnostic);
}
public sealed class TaskItemViewModel(TaskItemDisplay item,ILocalizationService language) : ObservableObject
{
    public TaskItemDisplay Item {get;set;}=item;
    public string Text => $"{language.Format(Schemas.All[Item.Resource].Name)} {Item.Key} · {language.Get("Enum_ItemState_"+Item.State)} · {language.Format(Item.Description??Messages.FromLegacy(Item.Message))}\n{Item.Diagnostic}";
    public void Refresh()=>OnPropertyChanged(nameof(Text));
}
public sealed class TaskJobViewModel : ObservableObject
{
    readonly ILocalizationService language;
    readonly Func<bool> available;
    readonly Func<RunMode> mode;
    readonly Dictionary<Guid,int> positions;
    readonly Dictionary<Guid,TaskItemDisplay> previous;
    string displayStatus;MessageDescriptor? displayDescription;
    IReadOnlyList<TaskItemViewModel>? itemList;
    readonly int[] counts=new int[Enum.GetValues<ItemState>().Length];
    Dictionary<Guid,TaskItemViewModel>? itemModels;
    int unreBased;
    public BatchJob Job {get;}
    public Guid Id=>Job.Id;
    public IReadOnlyList<TaskItemViewModel> Items
    {
        get {if(itemList==null){itemList=previous.Values.Select(i=>new TaskItemViewModel(i,language)).ToList();itemModels=itemList.ToDictionary(i=>i.Item.Id);}return itemList;}
    }
    public string Title=>$"{Job.Created.ToLocalTime().ToString("g",language.DisplayCulture)} · {language.Get("Enum_RunMode_"+Job.Mode)} · {language.Format(displayDescription??Messages.FromLegacy(displayStatus))}";
    public string Summary=>language.Get("Text_9A397E63C4",("arg0",counts[(int)ItemState.Succeeded]),("arg1",Job.Items.Count),("arg2",counts[(int)ItemState.Unknown]));
    public string DetailsLabel=>language.Get("Text_979A332955");
    public string ReviewLabel=>language.Get("ApiUnknownReview");
    public string ResumeLabel=>language.Get("Text_9C4801C86B");
    public string RetryLabel=>language.Get("Text_6A1C23202F");
    public IAsyncRelayCommand Details {get;}
    public IAsyncRelayCommand Review {get;}
    public IAsyncRelayCommand Resume {get;}
    public IAsyncRelayCommand Retry {get;}
    public TaskJobViewModel(BatchJob job,ILocalizationService l,Func<bool> available,Func<RunMode> mode,Func<Task> details,Func<Task> review,Func<Task> resume,Func<Task> retry)
    {
        Job=job;displayStatus=job.Status;displayDescription=job.Description;language=l;this.available=available;this.mode=mode;
        positions=job.Items.Select((i,n)=>(i.Id,n)).ToDictionary(p=>p.Id,p=>p.n);
        previous=job.Items.ToDictionary(i=>i.Id,TaskItemDisplay.Capture);
        foreach(var i in job.Items){counts[(int)i.State]++;if(i.State==ItemState.Succeeded&&!i.Rebased)unreBased++;}
        Details=new AsyncRelayCommand(details);
        Review=new AsyncRelayCommand(review,()=>available()&&job.Mode==RunMode.Online&&mode()!=RunMode.Demo&&counts[(int)ItemState.Unknown]>0);
        Resume=new AsyncRelayCommand(resume,()=>available()&&job.Mode==mode()&&job.Mode!=RunMode.Offline&&(counts[(int)ItemState.Pending]+counts[(int)ItemState.Running]+counts[(int)ItemState.Unknown]+unreBased)>0);
        Retry=new AsyncRelayCommand(retry,()=>available()&&job.Mode==RunMode.Demo&&counts[(int)ItemState.Failed]>0);
    }
    public void Update(Guid? id,bool notify=true)
    {
        displayStatus=Job.Status;displayDescription=Job.Description;
        if(id is Guid key && positions.TryGetValue(key,out var n))
        {
            var item=Job.Items[n];var old=previous[key];
            counts[(int)old.State]--;counts[(int)item.State]++;
            if(old.State==ItemState.Succeeded&&!old.Rebased)unreBased--;
            if(item.State==ItemState.Succeeded&&!item.Rebased)unreBased++;
            previous[key]=TaskItemDisplay.Capture(item);
            if(itemModels?.TryGetValue(key,out var vm)==true){vm.Item=previous[key];if(notify)vm.Refresh();}
        }
        if(notify)RefreshHeader();
    }
    public void Present(Guid id){if(itemModels?.TryGetValue(id,out var vm)==true)vm.Refresh();}
    public void RefreshHeader()
    {
        OnPropertyChanged(nameof(Title));OnPropertyChanged(nameof(Summary));
        Review.NotifyCanExecuteChanged();Resume.NotifyCanExecuteChanged();Retry.NotifyCanExecuteChanged();
    }
    public void RefreshLanguage()
    {
        RefreshHeader();OnPropertyChanged(nameof(DetailsLabel));OnPropertyChanged(nameof(ReviewLabel));OnPropertyChanged(nameof(ResumeLabel));OnPropertyChanged(nameof(RetryLabel));
        if(itemModels!=null)foreach(var vm in itemModels.Values)vm.Refresh();
    }
}