using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using VamSys.Core;
namespace VamSys.App;
public sealed partial class MainWindow
{
    Workspace? tasksOwner;
    UIElement? tasksPage;
    ListView? taskList;
    Button? taskCancel;
    readonly ObservableCollection<TaskJobViewModel> taskModels=[];
    readonly Dictionary<Guid,TaskJobViewModel> taskIndex=[];
    readonly Dictionary<Guid,HashSet<Guid>> pendingTaskItems=[];
    readonly DispatcherTimer taskUpdates=new(){Interval=TimeSpan.FromMilliseconds(50)};
    readonly Dictionary<Guid,double> taskScroll=[];
    bool taskTimerReady;
    int taskPageBuilds;
    void EnsureTasks()
    {
        if(!ReferenceEquals(tasksOwner,workspace))
        {
            tasksOwner=workspace;tasksPage=null;taskList=null;taskModels.Clear();taskIndex.Clear();pendingTaskItems.Clear();
        }
        if(taskIndex.Count==workspace.Jobs.Count)return;
        foreach(var job in workspace.Jobs.OrderByDescending(j=>j.Created))
        {
            if(taskIndex.ContainsKey(job.Id))continue;
            TaskJobViewModel? vm=null;
            vm=new(job,localization,()=>!busy&&closeState.IsOpen,()=>workspace.Mode,
                ()=>UserAction(()=>TaskDetails(vm!)),
                ()=>UserAction(()=>ReviewUnknown(job)),
                ()=>UserAction(()=>job.Mode==RunMode.Online?ExecuteApi(job):ExecuteDemo(job)),
                ()=>UserAction(async()=>{
                    if(busy||!closeState.IsOpen)return;
                    var reset=job.Items.Where(i=>i.State==ItemState.Failed).ToList();foreach(var i in reset)i.State=ItemState.Pending;
                    await Save();foreach(var i in reset)vm!.Update(i.Id);
                    await ExecuteDemo(job);
                }));
            taskIndex.Add(job.Id,vm);
            var at=0;while(at<taskModels.Count&&taskModels[at].Job.Created>job.Created)at++;
            taskModels.Insert(at,vm);
        }
    }
    void NotifyTask(BatchProgress change)
    {
        if(change.WorkspaceId!=workspace.Id)return;
        EnsureTasks();
        if(!taskIndex.TryGetValue(change.JobId,out var vm))return;
        vm.Update(change.ItemId,false);
        // Coalesce only presentation. Each row is read from the latest committed
        // model; cached counters account for its previously presented state.
        if(!pendingTaskItems.TryGetValue(change.JobId,out var ids))pendingTaskItems[change.JobId]=ids=[];
        if(change.ItemId is Guid id)ids.Add(id);
        if(change.Immediate){FlushTasks();return;}
        if(!taskTimerReady){taskUpdates.Tick+=(_,_)=>FlushTasks();taskTimerReady=true;}
        if(!taskUpdates.IsEnabled)taskUpdates.Start();
    }
    void FlushTasks()
    {
        taskUpdates.Stop();
        foreach(var (id,items) in pendingTaskItems)
            if(taskIndex.TryGetValue(id,out var vm)){foreach(var item in items)vm.Present(item);vm.RefreshHeader();}
        pendingTaskItems.Clear();
    }
    void RefreshTaskAvailability()
    {
        if(!ReferenceEquals(tasksOwner,workspace))return;
        if(taskCancel!=null)taskCancel.IsEnabled=running!=null&&closeState.IsOpen;
        foreach(var vm in taskModels)vm.RefreshHeader();
    }
    async Task TaskDetails(TaskJobViewModel vm)
    {
        FlushTasks();
        var list=new ListView{MaxHeight=450,ItemsSource=vm.Items,SelectionMode=ListViewSelectionMode.Single,
            ItemTemplate=(DataTemplate)XamlReader.Load("""<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Text="{Binding Text}" TextWrapping="Wrap" Padding="8"/></DataTemplate>""")};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(list,"TaskDetailsList");
        await Confirm(L("Text_4DCE8EF2E4"),list,L("Text_3FD47EDCE4"));
    }
    UIElement TasksPage()
    {
        EnsureTasks();FlushTasks();RefreshTaskAvailability();
        if(tasksPage!=null)return tasksPage;
        taskPageBuilds++;var grid=new Grid{RowSpacing=12};
        grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        taskCancel=Button(()=>L("Text_14F7E13979"),()=>{running?.Cancel();return Task.CompletedTask;},running!=null);
        grid.Children.Add(Stack(Text(()=>L("Text_59164F39E2")),taskCancel));
        taskList=new ListView{ItemsSource=taskModels,SelectionMode=ListViewSelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch};
        taskList.ItemTemplate=(DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Border Padding="16" Margin="0,6" CornerRadius="8" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
                <StackPanel Spacing="10">
                  <TextBlock Text="{Binding Title}" FontSize="17" TextWrapping="Wrap"/>
                  <TextBlock Text="{Binding Summary}" TextWrapping="Wrap"/>
                  <ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Disabled">
                    <StackPanel Orientation="Horizontal" Spacing="10">
                      <Button Content="{Binding DetailsLabel}" Command="{Binding Details}"/>
                      <Button Content="{Binding ReviewLabel}" Command="{Binding Review}"/>
                      <Button Content="{Binding ResumeLabel}" Command="{Binding Resume}"/>
                      <Button Content="{Binding RetryLabel}" Command="{Binding Retry}"/>
                    </StackPanel>
                  </ScrollViewer>
                </StackPanel>
              </Border>
            </DataTemplate>
            """);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(taskList,"TaskJobsList");
        var list=taskList;var owner=workspace.Id;
        list.Loaded+=(_,_)=>DispatcherQueue.TryEnqueue(()=>FindVisual<ScrollViewer>(list)?.ChangeView(null,taskScroll.GetValueOrDefault(owner),null,true));
        Grid.SetRow(list,1);grid.Children.Add(list);tasksPage=grid;return grid;
    }
    void CaptureTaskState()
    {
        if(tasksOwner!=null&&taskList!=null&&ReferenceEquals(PageHost.Content,tasksPage))
            taskScroll[tasksOwner.Id]=FindVisual<ScrollViewer>(taskList)?.VerticalOffset??0;
    }
}