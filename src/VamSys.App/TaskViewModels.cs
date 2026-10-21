using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VamSys.Core;
namespace VamSys.App;

public sealed record TaskItemDisplay(Guid Id,ResourceKind Resource,string Key,ItemState State,bool Rebased,MessageDescriptor? Description,string Message,string? Diagnostic,string? Evidence=null,bool CanVerifyDeletion=false)
{
    // Captured with the item; discarded items stay Failed so every executor skips them.
    public bool CanDiscard {get;init;}
    public bool Discarded {get;init;}
    public static TaskItemDisplay Capture(ChangeItem i)=>new(i.Id,i.Resource,Schemas.Key(i.Resource,i.After),i.State,i.Rebased,i.Description,i.Message,
        i.Diagnostic is {Length:>8000}?i.Diagnostic[..8000]+"…":i.Diagnostic,
        string.Join("\n",new[]{i.DiagnosticDetails?.Summary,i.DiagnosticDetails is {} d?$"{d.Method} {d.Path} · {d.StartedAt:O} → {d.FinishedAt:O}\nDate: {d.ServerDate:O}\n{string.Join("; ",d.TraceHeaders.Select(p=>$"{p.Key}: {p.Value}"))}\nBodyTruncated: {d.BodyTruncated}; SHA-256: {d.BodySha256}":null,
            i.DiagnosticDetails is {} validation?string.Join("\n",validation.FieldErrors.SelectMany(p=>p.Value.Select(message=>$"{p.Key}: {message}"))):null,
            i.ReadbackDetails is {} read?$"GET {read.Path} · HTTP {read.Status} · {read.StartedAt:O} → {read.FinishedAt:O}\n{read.Summary}":null,
            i.ReadbackDetails is {} readErrors?string.Join("\n",readErrors.FieldErrors.SelectMany(p=>p.Value.Select(message=>$"{p.Key}: {message}"))):null,
            i.PreflightComparison is {} comparison?$"updated_at: {comparison.PreviousUpdatedAt??"∅"} → {comparison.CurrentUpdatedAt??"∅"}\n{string.Join("; ",comparison.BusinessFields.Select(f=>$"{f.Field}: {f.Expected??"∅"} → {f.Actual??"∅"}"))}":null,
            i.DeletionVerification is {} deletion?$"API deletion check: {deletion.Outcome} · {deletion.CheckedAt:O} · DELETE {deletion.WriteStatus} / GET {deletion.ReadStatus} · previously manually confirmed: {deletion.WasManuallyConfirmed}":null,
            i.ManualResolution is {} manual?$"Manual resolution: {manual.Source} · {manual.ConfirmedAt:O} · GET {manual.ReadStatus} · write receipt: {(manual.WriteReceipt?"yes":"none")}":null,
            string.Join("\n",i.VerificationDifferences.Select(d=>$"{d.Field}: {d.Expected??"∅"} → {d.Actual??"∅"}"))}.Where(s=>!string.IsNullOrEmpty(s))),IndependentApiEvidence.CanCheckDeletion(i))
        {CanDiscard=ApiOperationPolicy.CanDiscardUnsent(i),Discarded=ApiOperationPolicy.IsDiscardedUnsent(i)};
}
public sealed class TaskItemViewModel(TaskItemDisplay item,ILocalizationService language) : ObservableObject
{
    public TaskItemDisplay Item {get;set;}=item;
    public string Text => $"{language.Format(Schemas.All[Item.Resource].Name)} {Item.Key} · {language.Get(Item.Discarded?"ItemStateDiscarded":"Enum_ItemState_"+Item.State)} · {language.Format(Item.Description??Messages.FromLegacy(Item.Message))}\n{Item.Evidence}\n{Item.Diagnostic}";
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
    int unreBased,deletionChecks,discardable,discarded;
    public BatchJob Job {get;}
    public Guid Id=>Job.Id;
    public IReadOnlyList<TaskItemViewModel> Items
    {
        get {if(itemList==null){itemList=previous.Values.Select(i=>new TaskItemViewModel(i,language)).ToList();itemModels=itemList.ToDictionary(i=>i.Item.Id);}return itemList;}
    }
    public string Title=>$"{Job.Created.ToLocalTime().ToString("g",language.DisplayCulture)} · {language.Get("Enum_RunMode_"+Job.Mode)} · {language.Format(displayDescription??Messages.FromLegacy(displayStatus))}";
    public string Summary=>language.Get("ApiTaskCounts",("success",counts[(int)ItemState.Succeeded]),("total",Job.Items.Count),("unknown",counts[(int)ItemState.Unknown]),("manual",counts[(int)ItemState.ManuallyConfirmed]))
        +(discarded>0?language.Get("ApiTaskDiscarded",("count",discarded)):"");
    public string DetailsLabel=>language.Get("Text_979A332955");
    public string ReviewLabel=>language.Get("ApiUnknownReview");
    public string ResumeLabel=>language.Get("Text_9C4801C86B");
    public string RetryLabel=>language.Get("Text_6A1C23202F");
    public string ExportLabel=>language.Get("ApiEvidenceExport");
    public string VerifyDeletesLabel=>language.Get("ApiVerifyDeletes");
    public string DiscardLabel=>language.Get("ApiDiscardPending");
    public IAsyncRelayCommand Details {get;}
    public IAsyncRelayCommand Review {get;}
    public IAsyncRelayCommand Resume {get;}
    public IAsyncRelayCommand Retry {get;}
    public IAsyncRelayCommand Export {get;}
    public IAsyncRelayCommand VerifyDeletes {get;}
    public IAsyncRelayCommand Discard {get;}
    public TaskJobViewModel(BatchJob job,ILocalizationService l,Func<bool> available,Func<RunMode> mode,Func<Task> details,Func<Task> review,Func<Task> resume,Func<Task> retry,Func<Task>? export=null,Func<Task>? verifyDeletes=null,Func<Task>? discard=null)
    {
        Job=job;displayStatus=job.Status;displayDescription=job.Description;language=l;this.available=available;this.mode=mode;
        positions=job.Items.Select((i,n)=>(i.Id,n)).ToDictionary(p=>p.Id,p=>p.n);
        previous=job.Items.ToDictionary(i=>i.Id,TaskItemDisplay.Capture);
        foreach(var i in previous.Values){counts[(int)i.State]++;if(i.State==ItemState.Succeeded&&!i.Rebased)unreBased++;if(i.CanVerifyDeletion)deletionChecks++;if(i.CanDiscard)discardable++;if(i.Discarded)discarded++;}
        Details=new AsyncRelayCommand(details);
        Review=new AsyncRelayCommand(review,()=>available()&&job.Mode==RunMode.Online&&mode()!=RunMode.Demo&&counts[(int)ItemState.Unknown]>0);
        Resume=new AsyncRelayCommand(resume,()=>available()&&job.Mode==mode()&&job.Mode!=RunMode.Offline&&(counts[(int)ItemState.Pending]+counts[(int)ItemState.Running]+counts[(int)ItemState.Unknown]+unreBased)>0);
        Retry=new AsyncRelayCommand(retry,()=>available()&&job.Mode==RunMode.Demo&&counts[(int)ItemState.Failed]>0);
        Export=new AsyncRelayCommand(export??(()=>Task.CompletedTask),()=>available());
        VerifyDeletes=new AsyncRelayCommand(verifyDeletes??(()=>Task.CompletedTask),()=>verifyDeletes!=null&&available()&&job.Mode==RunMode.Online&&mode()==RunMode.Online&&deletionChecks>0);
        // Local only: needs no connection, so it also unblocks an offline workspace.
        Discard=new AsyncRelayCommand(discard??(()=>Task.CompletedTask),()=>discard!=null&&available()&&job.Mode==RunMode.Online&&discardable>0);
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
            if(old.CanVerifyDeletion)deletionChecks--;if(previous[key].CanVerifyDeletion)deletionChecks++;
            if(old.CanDiscard)discardable--;if(previous[key].CanDiscard)discardable++;
            if(old.Discarded)discarded--;if(previous[key].Discarded)discarded++;
            if(itemModels?.TryGetValue(key,out var vm)==true){vm.Item=previous[key];if(notify)vm.Refresh();}
        }
        if(notify)RefreshHeader();
    }
    public void Present(Guid id){if(itemModels?.TryGetValue(id,out var vm)==true)vm.Refresh();}
    public void RefreshHeader()
    {
        OnPropertyChanged(nameof(Title));OnPropertyChanged(nameof(Summary));
        Review.NotifyCanExecuteChanged();Resume.NotifyCanExecuteChanged();Retry.NotifyCanExecuteChanged();Export.NotifyCanExecuteChanged();
        VerifyDeletes.NotifyCanExecuteChanged();Discard.NotifyCanExecuteChanged();
    }
    public void RefreshLanguage()
    {
        RefreshHeader();OnPropertyChanged(nameof(DetailsLabel));OnPropertyChanged(nameof(ReviewLabel));OnPropertyChanged(nameof(ResumeLabel));OnPropertyChanged(nameof(RetryLabel));OnPropertyChanged(nameof(ExportLabel));OnPropertyChanged(nameof(VerifyDeletesLabel));OnPropertyChanged(nameof(DiscardLabel));
        if(itemModels!=null)foreach(var vm in itemModels.Values)vm.Refresh();
    }
}
