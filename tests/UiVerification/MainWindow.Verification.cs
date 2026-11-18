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
    Task? verificationLocalTask;
    double verificationConstructedMs;
    double? verificationFirstFrameMs;
    Guid? verificationSwitchTarget;
    ContentDialog? verificationDialog;
    partial void CreateVerification(ref IWindowVerification? hooks) => hooks = new WindowVerification(this);
    // The QA hooks the window calls; each forwards to the methods below.
    sealed class WindowVerification(MainWindow window) : IWindowVerification
    {
        public void ConfigureTheme() => window.ConfigureVerificationTheme();
        public void Start() => window.InitializeVerification();
        public void ObserveDialog(ContentDialog dialog, bool showing) => window.ObserveVerificationDialog(dialog, showing);
    }
    readonly List<string> verificationInput=[];
    void LogVerificationInput(string kind,object? source)
    {
        var focus=RootGrid.XamlRoot is null?null:Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        var hit=source is null?"":" on "+source.GetType().Name+(source is TextBlock text?" '"+text.Text+"'":"");
        verificationInput.Add($"{DateTime.Now:HH:mm:ss.fff} {kind}{hit} canEdit={gate.CanEdit} querying={gate.QueryInProgress} actions={gate.RunningUserActions} focus={focus?.GetType().Name}");
        if(verificationInput.Count>40)verificationInput.RemoveAt(0);
    }
    void ObserveVerificationDialog(ContentDialog dialog, bool showing) { if (showing) verificationDialog=dialog; else if(verificationDialog==dialog) verificationDialog=null; }
    void ConfigureVerificationTheme()
        => RootGrid.RequestedTheme = Environment.GetEnvironmentVariable("VAMSYS_QA_THEME") == "light" ? ElementTheme.Light : ElementTheme.Dark;
    void InitializeVerification()
    {
        var pipe=Environment.GetEnvironmentVariable("VAMSYS_QA_PIPE");if(string.IsNullOrWhiteSpace(pipe))return;
        // Process creation to the end of the window constructor, and to the first composition frame.
        var processStarted=Process.GetCurrentProcess().StartTime;verificationConstructedMs=(DateTime.Now-processStarted).TotalMilliseconds;
        EventHandler<object>? firstFrame=null;
        firstFrame=(_,_)=>{Microsoft.UI.Xaml.Media.CompositionTarget.Rendering-=firstFrame;verificationFirstFrameMs=(DateTime.Now-processStarted).TotalMilliseconds;};
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering+=firstFrame;
        // What the window received of real pointer input, for a test whose click did nothing: presses and taps anywhere
        // under the root (popups are not under it), with what they hit and whether the window took edits at that moment.
        RootGrid.AddHandler(UIElement.PointerPressedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler((_,e)=>LogVerificationInput("pressed",e.OriginalSource)),true);
        RootGrid.AddHandler(UIElement.PointerReleasedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler((_,e)=>LogVerificationInput("released",e.OriginalSource)),true);
        RootGrid.AddHandler(UIElement.TappedEvent,new Microsoft.UI.Xaml.Input.TappedEventHandler((_,e)=>LogVerificationInput("tapped",e.OriginalSource)),true);
        RootGrid.AddHandler(UIElement.DoubleTappedEvent,new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler((_,e)=>LogVerificationInput("double-tapped",e.OriginalSource)),true);
        Activated+=(_,e)=>LogVerificationInput("window "+e.WindowActivationState,null);
        VamSys.Infrastructure.StorageTestHooks.SetBarrier(storage.Inner,p=>{
            if(p=="Draft:AfterCommit")Interlocked.Increment(ref verificationDraftCommits);
            if(p=="Full:AfterCommit")Interlocked.Increment(ref verificationFullCommits);
            // The close save is a journal save, or a full save when the journal asks for one (after a failure).
            if(p is not ("Full:BeforeCommit" or "Journal:BeforeCommit")||!verificationArmed||gate.Closing.State!=WindowCloseState.Saving)return;
            verificationArmed=false;verificationSaves++;verificationEntered=true;
            if(!verificationRelease.Wait(TimeSpan.FromSeconds(45)))throw new TimeoutException("QA close barrier");
            if(verificationFail)throw new IOException("QA injected close save failure");
        });
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
                resource=ResourceKind.Fleets;SelectResourceTab(resource);SelectPane(ShellPage.Data);Render();
                await dataQueryTask;RootGrid.UpdateLayout();
                return new{prepared=true};
            case "data-open":
            {
                var timer=Stopwatch.StartNew();
                SelectResourceTab(ResourceKind.Routes);
                if(page=="data")Render();else SelectPane(ShellPage.Data);
                await dataQueryTask;RootGrid.UpdateLayout();
                await WaitForDataPageReady();
                return new{milliseconds=timer.Elapsed.TotalMilliseconds,state=DataVerificationState()};
            }
            case "data-state":RootGrid.UpdateLayout();return DataVerificationState();
            case "input-log":return new{input=verificationInput.ToArray()};
            case "review-fixture":
                { var fleets = workspace.Resources[ResourceKind.Fleets].Draft; fleets[0] = fleets[0].With("Name", "QA reviewed fleet"); }
                { var airports = workspace.Resources[ResourceKind.Airports].Draft; airports[0] = airports[0].With("Name", "QA reviewed airport"); }
                await Save(); SelectPane(ShellPage.Review); Render(); await reviewLoad; RootGrid.UpdateLayout();
                return ReviewVerificationState();
            case "review-state":await reviewLoad;return ReviewVerificationState();
            case "review-stale":
            {
                { var fleets = workspace.Resources[ResourceKind.Fleets].Draft; fleets[0] = fleets[0].With("Name", "QA changed after review"); }
                var result = await ConfirmWorkspaceSubmission(workspace.Mode);
                await reviewLoad; RootGrid.UpdateLayout();
                return new { refreshed = result is null, state = ReviewVerificationState(), status = StatusLabel.Text };
            }
            case "data-batch-dialog":
                verificationLocalTask = EditRules();
                await Task.Delay(80);
                return new { locked = gate.LocalOperationActive && !CanEditData() && !Navigation.IsEnabled && !WorkspacePicker.IsEnabled && !ResourcePicker.IsEnabled && !PageHost.IsEnabled };
            case "parameter-dialog":
            {
                // The picker over made-up parameters: the real list needs the server (filtering it is MaintenanceServiceTests' job).
                if(verificationLocalTask!=null)await verificationLocalTask;
                var row=workspace.Resources[ResourceKind.Fleets].Draft.First(r=>r.Identity is null);
                verificationLocalTask=LocalOperation(()=>PickParameter(row,[new("11","7","AAA","AA",true,false,[]),new("12","7","BBB","BB",true,false,[])]));
                await Task.Delay(80);
                return new{opened=verificationDialog!=null};
            }
            case "dialog-cancel":
            {
                (verificationDialog ?? throw new InvalidOperationException("No QA dialog is open")).Hide();
                if (verificationLocalTask != null) await verificationLocalTask;
                return new { unlocked = !gate.LocalOperationActive && CanEditData(), state = DataVerificationState() };
            }
            case "data-edit-rebound":
            {
                var previous=Data.Draft[0];var before=previous.Get("Remarks");
                ChangeDataPage(2);RootGrid.UpdateLayout();var current=visible[0].Row;
                var editor=FindDataEditor(table!,"Remarks")??throw new InvalidOperationException("Remarks editor not rendered");
                editor.Text="native rebound editor";RootGrid.UpdateLayout();
                // Edits replace rows. Read both identities from the current draft, including the row
                // on the previous page, so an incorrect callback cannot pass by leaving an old object unchanged.
                var updated=Data.FindDraftRow(current.LocalId)!;var untouched=Data.FindDraftRow(previous.LocalId)!;
                return new{correct=updated.Get("Remarks")=="native rebound editor"&&untouched.Get("Remarks")==before,current=updated.Get("ID"),previous=untouched.Get("ID"),state=DataVerificationState()};
            }
            case "data-undo-save":
            {
                // The undo and redo buttons' own path: history move, save, page query and layout.
                await dataQueryTask;var draftBefore=verificationDraftCommits;var fullBefore=verificationFullCommits;var queriesBefore=shownState!.Model.QueryBuilds;
                var total=Stopwatch.StartNew();await MoveHistory(undo:true);await dataQueryTask;RootGrid.UpdateLayout();total.Stop();
                var undone=ShownRowsMatchDraft();
                var redo=Stopwatch.StartNew();await MoveHistory(undo:false);await dataQueryTask;RootGrid.UpdateLayout();redo.Stop();
                return new{totalMilliseconds=total.Elapsed.TotalMilliseconds,redoMilliseconds=redo.Elapsed.TotalMilliseconds,
                    draftCommits=verificationDraftCommits-draftBefore,fullCommits=verificationFullCommits-fullBefore,
                    queries=shownState.Model.QueryBuilds-queriesBefore,rowsMatch=undone&&ShownRowsMatchDraft(),status=StatusLabel.Text};
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
            case "startup":
            {
                for(var n=0;n<300&&verificationFirstFrameMs is null;n++)await Task.Delay(16);
                return new{constructedMilliseconds=verificationConstructedMs,firstFrameMilliseconds=verificationFirstFrameMs,page,routes=workspace.Resources[ResourceKind.Routes].Draft.Count};
            }
            case "workspace-load":
            {
                // The same synchronous load the constructor and workspace switch run on the UI thread.
                var samples=new List<double>();var allocations=new List<long>();
                for(var n=0;n<3;n++){var bytes=GC.GetAllocatedBytesForCurrentThread();var timer=Stopwatch.StartNew();_=storage.Load(workspace.Id);samples.Add(timer.Elapsed.TotalMilliseconds);allocations.Add(GC.GetAllocatedBytesForCurrentThread()-bytes);}
                return new{samples,allocatedBytes=allocations};
            }
            case "memory":
            {
                using var process=Process.GetCurrentProcess();var workingBeforeCollect=process.WorkingSet64;
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();process.Refresh();
                var gc=GC.GetGCMemoryInfo();
                return new{page,managedBytes=GC.GetTotalMemory(false),heapBytes=gc.HeapSizeBytes,committedBytes=gc.TotalCommittedBytes,workingSetBeforeCollect=workingBeforeCollect,workingSetBytes=process.WorkingSet64,privateBytes=process.PrivateMemorySize64};
            }
            case "workspace-switch":
            {
                // One round trip: save and leave the large workspace, then save the empty one and reload the data page.
                var home=workspace.Id;
                if(verificationSwitchTarget is null){var target=new Workspace{Name="QA switch target"};storage.Save(target);verificationSwitchTarget=target.Id;ReloadWorkspaces();}
                var away=Stopwatch.StartNew();await SwitchWorkspace(verificationSwitchTarget.Value);await dataQueryTask;RootGrid.UpdateLayout();away.Stop();
                var back=Stopwatch.StartNew();await SwitchWorkspace(home);await dataQueryTask;RootGrid.UpdateLayout();back.Stop();
                ReloadWorkspaces();
                return new{awayMilliseconds=away.Elapsed.TotalMilliseconds,backMilliseconds=back.Elapsed.TotalMilliseconds,status=StatusLabel.Text,state=DataVerificationState()};
            }
            case "arm":
                verificationRelease.Reset();verificationFail=false;verificationEntered=false;verificationArmed=true;return new{armed=true};
            case "close":PostMessage(WinRT.Interop.WindowNative.GetWindowHandle(this),0x0010,IntPtr.Zero,IntPtr.Zero);return new{closing=true};
            case "release":verificationRelease.Set();return new{released=true};
            case "fail":verificationFail=true;verificationRelease.Set();return new{released=true};
            case "action":await UserAction(()=>{verificationActions++;return Task.CompletedTask;});return new{actions=verificationActions};
            case "state":return new{state=gate.Closing.State.ToString(),idle=gate.IsIdle,workspaceName=workspace.Name,entered=verificationEntered,saves=verificationSaves,actions=verificationActions,enabled=Navigation.IsEnabled,name=workspace.Resources[ResourceKind.Fleets].Draft.FirstOrDefault()?.Get("Name"),undo=Data.Undo.Count,redo=Data.Redo.Count,draftSaves=verificationDraftCommits,fullSaves=verificationFullCommits};
            case "workspace-away":
                var other=new Workspace{Name="QA other"};storage.Save(other);ReloadWorkspaces();WorkspacePicker.SelectedItem=((List<WorkspaceChoice>)WorkspacePicker.ItemsSource).Single(w=>w.Id==other.Id);return new{requested=true};
            case "workspace-back":
                WorkspacePicker.SelectedItem=((List<WorkspaceChoice>)WorkspacePicker.ItemsSource).First(w=>w.Id!=workspace.Id);return new{requested=true};
            case "tasks":SelectPane(ShellPage.Tasks);Render();return new{jobs=taskModels.Count,builds=taskPageBuilds};
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
                PageHost.Content=BuildTasksPage();samples.Sort();return new{p95=samples[^1],samples=samples.Count};
            }
        }
        if(command.StartsWith("data-density:")){await ChangeTableDensity(command[13..]);RootGrid.UpdateLayout();await Task.Delay(80);RootGrid.UpdateLayout();return DataVerificationState();}
        if(command.StartsWith("data-theme:")){RootGrid.RequestedTheme=command[11..]=="light"?ElementTheme.Light:ElementTheme.Dark;RootGrid.UpdateLayout();return DataVerificationState();}
        if(command.StartsWith("data-window:"))
        {
            var size=command[12..].Split(',');var scale=RootGrid.XamlRoot.RasterizationScale;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(int.Parse(size[0])*scale),(int)(int.Parse(size[1])*scale)));
            await Task.Delay(100);RootGrid.UpdateLayout();return DataVerificationState();
        }
        if(command.StartsWith("data-scroll:"))
        {
            var value=command[12..].Split(',');table!.ScrollIntoView(visible[int.Parse(value[0])],ScrollIntoViewAlignment.Leading);RootGrid.UpdateLayout();
            horizontalTable?.ChangeView(double.Parse(value[1],System.Globalization.CultureInfo.InvariantCulture),null,null,true);
            RootGrid.UpdateLayout();await Task.Delay(50);RootGrid.UpdateLayout();
            return DataVerificationState();
        }
        if(command.StartsWith("review-bench:"))
        {
            // Changes the first N route drafts in memory only; the review page plans and validates every resource.
            var routes=workspace.Resources[ResourceKind.Routes].Draft;var count=Math.Min(int.Parse(command[13..]),routes.Count);
            for(var n=0;n<count;n++)routes[n]=routes[n].With("Remarks","review "+n);
            InvalidateDataViews();
            var plans=new List<double>();var keys=new List<double>();var renders=new List<double>();var renderStalls=new List<double>();
            var checks=new List<double>();var checkStalls=new List<double>();var checkPauses=new List<double>();var checkAllocations=new List<long>();int entries=0,rows=0;bool current=true;
            for(var n=0;n<3;n++)
            {
                // Plan plus key, as the worker builds it; then a fresh key alone on the same entries.
                var timer=Stopwatch.StartNew();var plan=BuildReviewPlan();plans.Add(timer.Elapsed.TotalMilliseconds);entries=plan.Entries.Count;
                timer.Restart();_=new WorkspaceReviewPlan(plan.Entries,plan.Blockers).ContentKey();keys.Add(timer.Elapsed.TotalMilliseconds);
                // Opening the page until it is filled, and the longest UI-thread frame meanwhile.
                using(var stall=new FrameStall())
                {
                    timer.Restart();
                    if(page=="review")Render();else SelectPane(ShellPage.Review);
                    await reviewLoad;RootGrid.UpdateLayout();renders.Add(timer.Elapsed.TotalMilliseconds);renderStalls.Add(stall.Longest);rows=reviewRows.Count;
                }
                // The check a submit runs before its confirmation dialog.
                using(var stall=new FrameStall())
                {
                    var pause=GC.GetTotalPauseDuration();var allocated=GC.GetTotalAllocatedBytes();
                    timer.Restart();current&=await CurrentReviewPlan(workspace.Mode) is not null;checks.Add(timer.Elapsed.TotalMilliseconds);checkStalls.Add(stall.Longest);
                    checkPauses.Add((GC.GetTotalPauseDuration()-pause).TotalMilliseconds);checkAllocations.Add(GC.GetTotalAllocatedBytes()-allocated);
                }
            }
            return new{changed=count,entries,rows,current,planMilliseconds=plans,contentKeyMilliseconds=keys,renderMilliseconds=renders,renderStallMilliseconds=renderStalls,
                submitCheckMilliseconds=checks,submitCheckStallMilliseconds=checkStalls,submitCheckGcPauseMilliseconds=checkPauses,submitCheckAllocatedBytes=checkAllocations};
        }
        if(command.StartsWith("scroll-bench:"))
        {
            await dataQueryTask;RootGrid.UpdateLayout();
            var step=double.Parse(command[13..],System.Globalization.CultureInfo.InvariantCulture);
            var vertical=FindVisual<ScrollViewer>(table!)??throw new InvalidOperationException("Table scroller missing");var horizontal=horizontalTable!;
            vertical.ChangeView(null,0,null,true);horizontal.ChangeView(0,null,null,true);RootGrid.UpdateLayout();await Task.Delay(200);
            var down=await FramePass(n=>{var target=Math.Min(n*step,vertical.ScrollableHeight);vertical.ChangeView(null,target,null,true);return target<vertical.ScrollableHeight;});
            var up=await FramePass(n=>{var target=Math.Max(vertical.ScrollableHeight-n*step,0);vertical.ChangeView(null,target,null,true);return target>0;});
            var downAgain=await FramePass(n=>{var target=Math.Min(n*step,vertical.ScrollableHeight);vertical.ChangeView(null,target,null,true);return target<vertical.ScrollableHeight;});
            var right=await FramePass(n=>{var target=Math.Min(n*step,horizontal.ScrollableWidth);horizontal.ChangeView(target,null,null,true);return target<horizontal.ScrollableWidth;});
            var left=await FramePass(n=>{var target=Math.Max(horizontal.ScrollableWidth-n*step,0);horizontal.ChangeView(target,null,null,true);return target>0;});
            vertical.ChangeView(null,0,null,true);RootGrid.UpdateLayout();
            return new{step,scrollableHeight=vertical.ScrollableHeight,scrollableWidth=horizontal.ScrollableWidth,down,up,downAgain,right,left,state=DataVerificationState()};
        }
        if(command.StartsWith("data-row:"))
        {var view=visible[int.Parse(command[9..])];return new{id=view.Row.LocalId,deleted=Schemas.Deleted(view.Row),fields=view.Row.Fields,focused=Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot)?.GetType().Name,editors=CountVisual<TextBox>(table!),hints=view.Cells.Where(c=>c.Hint!=null).ToDictionary(c=>c.Field,c=>c.Hint)};}
        if(command.StartsWith("data-mark-deleted:"))
        {
            // Marks a shown row deleted in memory only, then rebinds it as a page refresh would.
            var view=visible[int.Parse(command[18..])];var marked=view.Row.With("_delete","TRUE");Data.ReplaceDraftRows([marked]);view.Rebind(marked);RootGrid.UpdateLayout();
            return new{id=view.Row.LocalId,readOnly=view.Cells.Where(c=>c.Field!="_delete").All(c=>c.IsReadOnly)};
        }
        if(command.StartsWith("data-page-input:")){DataPageJumpBox!.Text=command[16..];RestoreDataSelection();return DataVerificationState();}
        if(command.StartsWith("data-unapplied:")){searchBox!.Text=command[15..];await Task.Delay(50);RootGrid.UpdateLayout();return DataVerificationState();}
        if(command.StartsWith("language:")){localization.SetLanguage(command[9..]);RootGrid.UpdateLayout();return new{language=localization.Language};}
        if(command.StartsWith("data-page:")){ChangeDataPage(int.Parse(command[10..]));RootGrid.UpdateLayout();return DataVerificationState();}
        if(command.StartsWith("data-size:")){DataPageSizeBox!.SelectedItem=int.Parse(command[10..]);RootGrid.UpdateLayout();return DataVerificationState();}
        if(command.StartsWith("data-query:"))
        {
            using var doc=JsonDocument.Parse(command[11..]);var value=doc.RootElement;
            var search=value.GetProperty("search").GetString()!;var filter=value.GetProperty("filter").GetInt32();
            var sort=value.TryGetProperty("sort",out var s)&&s.ValueKind==JsonValueKind.String?s.GetString():null;
            // This command runs the query itself: the controls show its conditions and do not start another one.
            settingQueryControls=true;searchBox!.Text=search;filterBox!.SelectedIndex=filter;sortBox!.SelectedItem=string.IsNullOrEmpty(sort)?null:sort;settingQueryControls=false;queryPause?.Stop();
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
            // Encode in memory; filesystem access uses the same .NET path as the isolated QA data.
            using(var stream=new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                var encoder=await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,stream);
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels);await encoder.FlushAsync();
                var encoded=new byte[(int)stream.Size];
                using(var encodedReader=new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0))) { await encodedReader.LoadAsync((uint)encoded.Length);encodedReader.ReadBytes(encoded); }
                await File.WriteAllBytesAsync(output,encoded);
            }
            return new{path=output};
        }
        if(command.StartsWith("edit:"))
        {visible[0].Cells.Single(c=>c.Field=="Name").Value=command[5..];return new{edited=true};}
        return new{error="Unknown QA command"};
    }
    object ReviewVerificationState() => new { resources = reviewPlan?.Entries.Select(e => e.Planned.Resource.ToString()).Distinct().ToArray(),
        total = reviewPlan?.Entries.Count, ready = reviewPlan?.SubmitItems.Count, resourcePickerVisible = ResourceBar.Visibility == Visibility.Visible,
        renderedText = FindVisual<ListView>(PageHost) is { } list ? FindVisual<TextBlock>(list)?.Text : null,
        rows = (FindVisual<ListView>(PageHost)?.Items.Count ?? 0), mode = workspace.Mode.ToString(),
        // Why an entry is held back: its disposition and reason (with the reason's arguments, e.g. the field of a validation issue).
        entries = reviewPlan?.Entries.Select(e => $"{e.Planned.Resource} {e.Planned.Kind} {e.Disposition} {e.Reason?.Code} {(e.Reason is { } reason ? L(reason) : "")}".TrimEnd()).ToArray(),
        blockers = reviewPlan?.Blockers.Select(b => b.Code).ToArray() };
    // dataQueryTask can complete before the first row containers and cell editors are realised, or a
    // superseded query can return early; wait until the page is really usable (10 s at most).
    async Task WaitForDataPageReady()
    {
        var deadline=Stopwatch.StartNew();
        while(deadline.Elapsed<TimeSpan.FromSeconds(10))
        {
            RootGrid.UpdateLayout();
            if(!gate.QueryInProgress&&shownState?.Model.Total>0&&table?.ContainerFromIndex(0)!=null&&FindVisual<EditableCell>(table)!=null)return;
            await Task.Delay(50);
            await dataQueryTask;
        }
    }
    object DataVerificationState()
    {
        var scroll=table is null?null:FindVisual<ScrollViewer>(table);
        var realized=new List<(double Top,double Height)>();
        if(table!=null&&scroll!=null)
            for(var n=0;n<visible.Count;n++) if(table.ContainerFromIndex(n) is FrameworkElement item)
                realized.Add((item.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y,item.ActualHeight));
        return new { rows=visible.Count,cells=visible.Sum(r=>r.Cells.Count),columns=DisplayColumns().Count,
            hasEditors=table!=null&&FindVisual<EditableCell>(table)!=null,textBoxes=table is null?0:CountVisual<TextBox>(table),total=shownState?.Model.Total,page=shownState?.Model.Page,pages=shownState?.Model.PageCount,
            pageSize=shownState?.Model.PageSize,selected=shownState?.Model.Selected.Count,selectedRows=SelectedRows().Take(10).Select(r=>r.Get("ID")).ToArray(),
            first=visible.FirstOrDefault()?.Row.Get("ID"),last=visible.LastOrDefault()?.Row.Get("ID"),builds=dataPageBuilds,rowBuilds=dataRowBuilds,queryBuilds=shownState?.Model.QueryBuilds,querying=gate.QueryInProgress,
            density=tableDensity,actualRowHeight=realized.FirstOrDefault().Height,viewport=scroll?.ViewportHeight,fullVisibleRows=realized.Count(r=>r.Top>=0&&r.Top+r.Height<=scroll!.ViewportHeight),
            vertical=scroll?.VerticalOffset,horizontal=horizontalTable?.HorizontalOffset,anchor=FirstVisibleDataRow()?.Id,
            windowWidth=RootGrid.ActualWidth,windowHeight=RootGrid.ActualHeight,rasterizationScale=RootGrid.XamlRoot?.RasterizationScale,
            theme=RootGrid.ActualTheme.ToString(),pageInput=DataPageJumpBox?.Text,unapplied=queryNotice?.Visibility==Visibility.Visible,
            batchEnabled=BatchCommandStates(),
            draftSaves=verificationDraftCommits,fullSaves=verificationFullCommits,undo=Data.Undo.Count };
    }
    // Every shown row is bound to the live draft row and every editor shows that row's value.
    bool ShownRowsMatchDraft() => visible.All(v => ReferenceEquals(Data.FindDraftRow(v.Row.LocalId), v.Row)
        && v.Cells.All(c => c.Field is "_delete" or "Fleet ID" or "Fleet IDs" || c.Value == v.Row.Get(c.Field)));
    // Cells show text until edited; open the first realized cell of the field the way a click does.
    TextBox? FindDataEditor(DependencyObject root,string field)
    {
        if(FindCell(root,field) is not {} cell)return null;
        cell.BeginEdit();RootGrid.UpdateLayout();return cell.Editor;
    }
    static EditableCell? FindCell(DependencyObject root,string field)
    {
        if(root is EditableCell cell&&Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(cell)==field)return cell;
        for(var n=0;n<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);n++)
            if(FindCell(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root,n),field) is {} found)return found;
        return null;
    }
    static int CountVisual<T>(DependencyObject root) where T:DependencyObject
    {
        var count=root is T?1:0;
        for(var n=0;n<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);n++)count+=CountVisual<T>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root,n));
        return count;
    }
    // Longest interval between composition frames while it is alive: how long the UI thread was blocked.
    sealed class FrameStall : IDisposable
    {
        long last=Stopwatch.GetTimestamp();
        public double Longest{get;private set;}
        public FrameStall()=>Microsoft.UI.Xaml.Media.CompositionTarget.Rendering+=Frame;
        void Frame(object? sender,object e){var now=Stopwatch.GetTimestamp();Longest=Math.Max(Longest,Stopwatch.GetElapsedTime(last,now).TotalMilliseconds);last=now;}
        public void Dispose(){Frame(null,EventArgs.Empty);Microsoft.UI.Xaml.Media.CompositionTarget.Rendering-=Frame;}
    }
    // Advances once per composition frame and records the interval between frames. Intervals include
    // the UI-thread work of each step (container realization, binding, layout); not GPU present time.
    static Task<object> FramePass(Func<int,bool> advance)
    {
        var done=new TaskCompletionSource<object>();var stamps=new List<long>();var frame=0;
        var bytes=GC.GetAllocatedBytesForCurrentThread();var gen0=GC.CollectionCount(0);var gen2=GC.CollectionCount(2);
        EventHandler<object>? handler=null;
        handler=(_,_)=>{
            stamps.Add(Stopwatch.GetTimestamp());
            if(frame<2000&&advance(++frame))return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering-=handler;
            var intervals=stamps.Zip(stamps.Skip(1),(a,b)=>Stopwatch.GetElapsedTime(a,b).TotalMilliseconds).ToArray();var sorted=intervals.Order().ToArray();
            double At(double q)=>sorted.Length==0?0:sorted[Math.Min(sorted.Length-1,(int)(sorted.Length*q))];
            done.SetResult(new{frames=stamps.Count,durationMilliseconds=stamps.Count<2?0:Stopwatch.GetElapsedTime(stamps[0],stamps[^1]).TotalMilliseconds,
                minimum=At(0),median=At(.5),p95=At(.95),max=sorted.Length==0?0:sorted[^1],
                over17=intervals.Count(i=>i>16.7),over33=intervals.Count(i=>i>33.4),over50=intervals.Count(i=>i>50),
                allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-bytes,gen0=GC.CollectionCount(0)-gen0,gen2=GC.CollectionCount(2)-gen2,intervals});
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering+=handler;
        return done.Task;
    }
    object DataMeasure(string phase,Action<int> action)
    {
        var samples=new List<double>();var allocations=new List<long>();
        var directory=Environment.GetEnvironmentVariable("VAMSYS_DATA_DIR")??throw new InvalidOperationException("QA requires an isolated directory");
        for(var n=0;n<25;n++){var bytes=GC.GetAllocatedBytesForCurrentThread();var timer=Stopwatch.StartNew();action(n);RootGrid.UpdateLayout();var elapsed=timer.Elapsed.TotalMilliseconds;if(n>=5){samples.Add(elapsed);allocations.Add(GC.GetAllocatedBytesForCurrentThread()-bytes);}File.AppendAllText(Path.Combine(directory,"ui-timing.log"),$"{phase} {n}: {elapsed:F2} ms\n");}
        var sorted=samples.Order().ToArray();return new{median=sorted[10],p95=sorted[18],max=sorted[^1],samples,allocatedBytes=allocations};
    }
}
