#if UI_VERIFICATION
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
    int verificationSaves,verificationActions;
    void InitializeVerification()
    {
        var pipe=Environment.GetEnvironmentVariable("VAMSYS_QA_PIPE");if(string.IsNullOrWhiteSpace(pipe))return;
        typeof(VamSys.Infrastructure.WorkspaceStore).GetProperty("Barrier",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(store,(Action<string>)(p=>{
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
                return new{prepared=true};
            case "arm":
                verificationRelease.Reset();verificationFail=false;verificationEntered=false;verificationArmed=true;return new{armed=true};
            case "close":PostMessage(WinRT.Interop.WindowNative.GetWindowHandle(this),0x0010,IntPtr.Zero,IntPtr.Zero);return new{closing=true};
            case "release":verificationRelease.Set();return new{released=true};
            case "fail":verificationFail=true;verificationRelease.Set();return new{released=true};
            case "action":await UserAction(()=>{verificationActions++;return Task.CompletedTask;});return new{actions=verificationActions};
            case "state":return new{state=closeState.State.ToString(),entered=verificationEntered,saves=verificationSaves,actions=verificationActions,enabled=Navigation.IsEnabled,name=workspace.Resources[ResourceKind.Fleets].Draft[0].Get("Name")};
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
        if(command.StartsWith("edit:"))
        {visible[0].Cells.Single(c=>c.Field=="Name").Value=command[5..];return new{edited=true};}
        return new{error="Unknown QA command"};
    }
}
#endif