using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;
namespace VamSys.App;
public sealed partial class MainWindow
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
    readonly ManualResetEventSlim verificationRelease=new();
    volatile bool verificationArmed,verificationEntered,verificationFail;
    int verificationSaves,verificationActions,verificationDraftCommits,verificationFullCommits;
    partial void ConfigureVerificationTheme()
        => RootGrid.RequestedTheme = Environment.GetEnvironmentVariable("VAMSYS_QA_THEME") == "light" ? ElementTheme.Light : ElementTheme.Dark;
    partial void InitializeVerification()
    {
        var pipe=Environment.GetEnvironmentVariable("VAMSYS_QA_PIPE");if(string.IsNullOrWhiteSpace(pipe))return;
        typeof(VamSys.Infrastructure.WorkspaceStore).GetProperty("Barrier",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(store,(Action<string>)(p=>{
            if(p=="Draft:AfterCommit")Interlocked.Increment(ref verificationDraftCommits);
            if(p=="Full:AfterCommit")Interlocked.Increment(ref verificationFullCommits);
            if(p!="Full:BeforeCommit"||!verificationArmed||closeState.State!=WindowCloseState.Saving)return;
            verificationArmed=false;verificationSaves++;verificationEntered=true;
            if(!verificationRelease.Wait(TimeSpan.FromSeconds(45)))throw new TimeoutException("QA close barrier");
            if(verificationFail)throw new IOException("QA injected close save failure");
        }));
        _=Task.Run(async()=>{
            using var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();using var reader=new StreamReader(server);using var writer=new StreamWriter(server){AutoFlush=true};
            while(await reader.ReadLineAsync() is string command)
            {
                var reply=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                DispatcherQueue.TryEnqueue(async()=>{try{reply.SetResult(await VerificationCommand(command));}catch(Exception e){reply.SetResult(new{error=e.ToString()});}});
                await writer.WriteLineAsync(JsonSerializer.Serialize(await reply.Task));
            }
        });
    }
    async Task<object> VerificationCommand(string command)
    {
        switch(command)
        {
            case "prepare":
                resource=ResourceKind.Fleets;ResourcePicker.SelectedIndex=(int)resource;Navigation.SelectedItem=Navigation.MenuItems[1];Render();
                await dataQueryTask;RootGrid.UpdateLayout();
                return new{prepared=true};
            case "data-open":
            {
                var timer=Stopwatch.StartNew();
                ResourcePicker.SelectedIndex=(int)ResourceKind.Routes;
                if(page=="data")Render();else Navigation.SelectedItem=Navigation.MenuItems[1];
                await dataQueryTask;RootGrid.UpdateLayout();
                return new{milliseconds=timer.Elapsed.TotalMilliseconds,state=DataVerificationState()};
            }
            case "data-state":return DataVerificationState();
            case "data-edit-rebound":
            {
                var previous=Data.Draft[0];var before=previous.Get("Remarks");
                ChangeDataPage(2);RootGrid.UpdateLayout();var current=visible[0].Row;
                var editor=FindDataEditor(table!,"Remarks")??throw new InvalidOperationException("Remarks editor not rendered");
                editor.Text="native rebound editor";RootGrid.UpdateLayout();
                return new{correct=current.Get("Remarks")=="native rebound editor"&&previous.Get("Remarks")==before,current=current.Get("ID"),previous=previous.Get("ID"),state=DataVerificationState()};
            }
            case "data-undo-save":
            {
                await dataQueryTask;var total=Stopwatch.StartNew();var core=Stopwatch.StartNew();
                Data.UndoEdit();core.Stop();await Save();Render();await dataQueryTask;RootGrid.UpdateLayout();total.Stop();
                var fullCommits=verificationFullCommits;
                Data.RedoEdit();await Save();Render();await dataQueryTask;RootGrid.UpdateLayout();
                return new{coreMilliseconds=core.Elapsed.TotalMilliseconds,totalMilliseconds=total.Elapsed.TotalMilliseconds,fullCommits};
            }
            case "data-select-first":if(visible.Count>0)table!.SelectedItems.Add(visible[0]);return DataVerificationState();
            case "data-select-all":shownState!.Model.SelectAll();RestoreDataSelection();return DataVerificationState();
            case "data-clear":shownState!.Model.Selected.Clear();RestoreDataSelection();return DataVerificationState();
            case "data-cancel-test":
            {
                var before=shownState!.Model.QueryBuilds;var task=LoadRowsAsync("canceled",0,null);CancelDataQuery();await task;
                return new{retained=shownState.Model.QueryBuilds==before,state=DataVerificationState()};
            }
            case "data-benchmark":
            {
                await dataQueryTask;var model=shownState!.Model;var builds=dataPageBuilds;var queries=model.QueryBuilds;
                var paging=DataMeasure("paging",n=>ChangeDataPage(n%2+1));
                var language=DataMeasure("language",n=>localization.SetLanguage(n%2==0?"zh-CN":"en-US"));
                var edit=DataMeasure("edit",n=>visible[0].Cells.Single(c=>c.Field=="Remarks").Value="benchmark "+n);
                return new{paging,language,edit,buildsBefore=builds,buildsAfter=dataPageBuilds,queriesBefore=queries,queriesAfter=model.QueryBuilds,state=DataVerificationState()};
            }
            case "arm":
                verificationRelease.Reset();verificationFail=false;verificationEntered=false;verificationArmed=true;return new{armed=true};
            case "close":PostMessage(WinRT.Interop.WindowNative.GetWindowHandle(this),0x0010,IntPtr.Zero,IntPtr.Zero);return new{closing=true};
            case "release":verificationRelease.Set();return new{released=true};
            case "fail":verificationFail=true;verificationRelease.Set();return new{released=true};
            case "action":await UserAction(()=>{verificationActions++;return Task.CompletedTask;});return new{actions=verificationActions};
            case "state":return new{state=closeState.State.ToString(),idle=interactiveOperations==0&&!busy&&!querying&&dialogs==0,workspaceName=workspace.Name,entered=verificationEntered,saves=verificationSaves,actions=verificationActions,enabled=Navigation.IsEnabled,name=workspace.Resources[ResourceKind.Fleets].Draft.FirstOrDefault()?.Get("Name"),undo=Data.Undo.Count,redo=Data.Redo.Count,draftSaves=verificationDraftCommits,fullSaves=verificationFullCommits};
            case "workspace-away":
                var other=new Workspace{Name="QA other"};store.Save(other);ReloadWorkspaces();WorkspacePicker.SelectedItem=((List<WorkspaceChoice>)WorkspacePicker.ItemsSource).Single(w=>w.Id==other.Id);return new{requested=true};
            case "workspace-back":
                WorkspacePicker.SelectedItem=((List<WorkspaceChoice>)WorkspacePicker.ItemsSource).First(w=>w.Id!=workspace.Id);return new{requested=true};
            case "tasks":Navigation.SelectedItem=Navigation.MenuItems[4];Render();return new{jobs=taskModels.Count,builds=taskPageBuilds};
            case "cancellable":running=new();RefreshTaskAvailability();return new{ready=true};
            case "task-state":return new{selected=(taskList?.SelectedItem as TaskJobViewModel)?.Id,builds=taskPageBuilds,canceled=running?.IsCancellationRequested??false};
            case "progress":
            {
                var job=workspace.Jobs.Last();var samples=new List<double>();
                for(var n=0;n<550;n++)
                {
                    var i=job.Items[n%job.Items.Count];var timer=Stopwatch.StartNew();
                    i.State=n%2==0?ItemState.Running:ItemState.Succeeded;
                    NotifyTask(new(workspace.Id,job.Id,i.Id));FlushTasks();RootGrid.UpdateLayout();
                    if(n>=50)samples.Add(timer.Elapsed.TotalMilliseconds);
                }
                samples.Sort();return new{p95=samples[(int)(samples.Count*.95)],max=samples[^1],samples=samples.Count,builds=taskPageBuilds};
            }
            case "baseline":
            {
                var samples=new List<double>();
                for(var n=0;n<7;n++)
                {
                    var timer=Stopwatch.StartNew();var cards=Stack();
                    foreach(var j in workspace.Jobs.OrderByDescending(j=>j.Created))
                    {
                        var card=Stack(Text(()=>j.Status,17),Text(()=>L("Text_9A397E63C4",("arg0",j.Items.Count(i=>i.State==ItemState.Succeeded)),("arg1",j.Items.Count),("arg2",j.Items.Count(i=>i.State==ItemState.Unknown)))));
                        card.Children.Add(Actions(Button(()=>L("Text_979A332955"),()=>Task.CompletedTask),Button(()=>L("ApiUnknownReview"),()=>Task.CompletedTask),Button(()=>L("Text_9C4801C86B"),()=>Task.CompletedTask),Button(()=>L("Text_6A1C23202F"),()=>Task.CompletedTask)));
                        cards.Children.Add(new Border{Child=card,Padding=new Thickness(16),Margin=new Thickness(0,6,0,6),CornerRadius=new CornerRadius(8)});
                    }
                    PageHost.Content=Scroll(cards);RootGrid.UpdateLayout();if(n>=2)samples.Add(timer.Elapsed.TotalMilliseconds);
                }
                PageHost.Content=TasksPage();samples.Sort();return new{p95=samples[^1],samples=samples.Count};
            }
        }
        if(command.StartsWith("language:")){localization.SetLanguage(command[9..]);RootGrid.UpdateLayout();return new{language=localization.Language};}
        if(command.StartsWith("data-page:")){ChangeDataPage(int.Parse(command[10..]));RootGrid.UpdateLayout();return DataVerificationState();}
        if(command.StartsWith("data-size:")){pageSize!.SelectedItem=int.Parse(command[10..]);RootGrid.UpdateLayout();return DataVerificationState();}
        if(command.StartsWith("data-query:"))
        {
            using var doc=JsonDocument.Parse(command[11..]);var value=doc.RootElement;
            var search=value.GetProperty("search").GetString()!;var filter=value.GetProperty("filter").GetInt32();
            var sort=value.TryGetProperty("sort",out var s)&&s.ValueKind==JsonValueKind.String?s.GetString():null;
            searchBox!.Text=search;filterBox!.SelectedIndex=filter;if(sort!=null)sortBox!.SelectedItem=sort;
            var timer=Stopwatch.StartNew();await LoadRowsAsync(search,filter,sort);RootGrid.UpdateLayout();
            return new{milliseconds=timer.Elapsed.TotalMilliseconds,state=DataVerificationState()};
        }
        if(command.StartsWith("data-screenshot:"))
        {
            // Let text replacement/translation transitions finish before an exported image.
            await Task.Delay(250);RootGrid.UpdateLayout();
            var bitmap=new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();await bitmap.RenderAsync(RootGrid);
            var buffer=await bitmap.GetPixelsAsync();var pixels=new byte[buffer.Length];using(var reader=Windows.Storage.Streams.DataReader.FromBuffer(buffer))reader.ReadBytes(pixels);
            var output=Path.GetFullPath(command[16..]);Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var folder=await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!);
            var file=await folder.CreateFileAsync(Path.GetFileName(output),Windows.Storage.CreationCollisionOption.ReplaceExisting);
            using(var stream=await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
            {
                var encoder=await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,stream);
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels);await encoder.FlushAsync();
            }
            return new{path=output};
        }
        if(command.StartsWith("edit:"))
        {visible[0].Cells.Single(c=>c.Field=="Name").Value=command[5..];return new{edited=true};}
        return new{error="Unknown QA command"};
    }
    object DataVerificationState()=>new{rows=visible.Count,cells=visible.Sum(r=>r.Cells.Count),columns=DisplayColumns().Count,hasEditors=table!=null&&FindVisual<TextBox>(table)!=null,total=shownState?.Model.Total,page=shownState?.Model.Page,pages=shownState?.Model.PageCount,pageSize=shownState?.Model.PageSize,selected=shownState?.Model.Selected.Count,selectedRows=SelectedRows().Take(10).Select(r=>r.Get("ID")).ToArray(),first=visible.FirstOrDefault()?.Row.Get("ID"),last=visible.LastOrDefault()?.Row.Get("ID"),builds=dataPageBuilds,rowBuilds=dataRowBuilds,queryBuilds=shownState?.Model.QueryBuilds,querying};
    static TextBox? FindDataEditor(DependencyObject root,string field)
    {
        if(root is TextBox editor&&Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(editor)==field)return editor;
        for(var n=0;n<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);n++)
            if(FindDataEditor(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root,n),field) is {} found)return found;
        return null;
    }
    object DataMeasure(string phase,Action<int> action)
    {
        var samples=new List<double>();var allocations=new List<long>();
        var directory=Environment.GetEnvironmentVariable("VAMSYS_DATA_DIR")??throw new InvalidOperationException("QA requires an isolated directory");
        for(var n=0;n<25;n++){var bytes=GC.GetAllocatedBytesForCurrentThread();var timer=Stopwatch.StartNew();action(n);RootGrid.UpdateLayout();var elapsed=timer.Elapsed.TotalMilliseconds;if(n>=5){samples.Add(elapsed);allocations.Add(GC.GetAllocatedBytesForCurrentThread()-bytes);}File.AppendAllText(Path.Combine(directory,"ui-timing.log"),$"{phase} {n}: {elapsed:F2} ms\n");}
        var sorted=samples.Order().ToArray();return new{median=sorted[10],p95=sorted[18],max=sorted[^1],samples,allocatedBytes=allocations};
    }
}
