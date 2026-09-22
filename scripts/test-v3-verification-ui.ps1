param([switch]$Tasks,[switch]$InteractionsOnly)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
$repo=Split-Path $PSScriptRoot -Parent
$dir=Join-Path $repo ('artifacts/storage-v3-20260922/qa-'+[Guid]::NewGuid().ToString('N'))
$pipeName='vamsys-ui-'+[Guid]::NewGuid().ToString('N')
if(Get-Process VamSys.App -ErrorAction SilentlyContinue){throw 'Close existing app before isolated QA'}
if($Tasks){& "$repo/.tools/dotnet/dotnet.exe" run --no-build --project "$repo/tests/VamSys.Tests" -c Release -- --seed-task-ui $dir}
else {& "$repo/.tools/dotnet/dotnet.exe" run --no-build --project "$repo/tests/VamSys.Tests" -c Release -- --seed-storage-ui $dir legacy}
if($LASTEXITCODE -ne 0){throw 'Seed failed'}
$env:VAMSYS_DATA_DIR=$dir;$env:VAMSYS_QA_PIPE=$pipeName
$p=Start-Process "$repo/artifacts/v3-qa/VamSys.App.exe" -WindowStyle Hidden -PassThru
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut)
function Q([string]$command){
    $writer.WriteLine($command);$line=$reader.ReadLine()
    if(!$line){throw 'QA pipe closed'}
    $result=$line|ConvertFrom-Json
    if($result.error){throw $result.error};return $result
}
function Assert($value,$why){if(!$value){throw $why}}
function All {
    try {$p.Refresh();if($p.MainWindowHandle -eq [IntPtr]::Zero){return @()};$root=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle);$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)}catch{return @()}
}
function Wait([scriptblock]$condition) {
    $limit=[DateTime]::UtcNow.AddSeconds(20)
    do {$value=& $condition;if($value){return $value};Start-Sleep -Milliseconds 50}while([DateTime]::UtcNow -lt $limit)
    throw "Timeout: $condition"
}
function Click([string]$name){
    $button=Wait {All|Where-Object{$_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Current.Name -eq $name}|Select-Object -First 1}
    ([System.Windows.Automation.InvokePattern]$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
try {
    $pipe.Connect(20000);$reader=[IO.StreamReader]::new($pipe);$writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true
    if(!$Tasks){
        Q prepare|Out-Null;Q 'edit:accepted before close'|Out-Null;Q arm|Out-Null;Q close|Out-Null
        $state=Wait {$s=Q state;if($s.entered){$s}}
        Assert ($state.state -eq 'Saving' -and !$state.enabled) 'Window not frozen'
        Q 'edit:must not persist'|Out-Null;Q action|Out-Null;Q close|Out-Null
        $state=Q state;Assert ($state.name -eq 'accepted before close' -and $state.actions -eq 0 -and $state.saves -eq 1) 'Mutation or duplicate close accepted'
        Q fail|Out-Null
        $state=Wait {$s=Q state;if($s.state -eq 'Open'){$s}}
        Assert $state.enabled 'Failure did not restore interaction'
        Q 'edit:accepted after failure'|Out-Null;Q action|Out-Null
        Assert ((Q state).actions -eq 1) 'Command did not recover'
        Q arm|Out-Null;Q close|Out-Null;Wait {$s=Q state;if($s.entered){$s}}|Out-Null
        Q release|Out-Null;Assert ($p.WaitForExit(10000)) 'Successful close did not exit'
        & "$repo/.tools/dotnet/dotnet.exe" run --no-build --project "$repo/tests/VamSys.Tests" -c Release -- --assert-close-ui $dir
        if($LASTEXITCODE -ne 0){throw 'Restart verification failed'}
        Write-Output 'PASS actual window: freeze before delayed save, edit/action/repeated close blocked, failure restores draft and commands, retry closes'
    } else {
        $initial=Q tasks;Assert ($initial.jobs -eq 1001) 'Task fixture count'
        $cards=@(Wait {$rows=@(All|Where-Object{$_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and $_.Current.Name.Contains('TaskJobViewModel')});if($rows.Count){$rows}})
        Assert ($cards.Count -lt 100) 'Task cards not virtualized'
        $progress=Q progress
        Assert ($progress.builds -eq $initial.builds -and $progress.p95 -le 50) 'Local updates rebuilt page or exceeded P95'
        Click 'Details'
        $items=@(Wait {$list=All|Where-Object{$_.Current.AutomationId -eq 'TaskDetailsList'}|Select-Object -First 1;if($list){$rows=@($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|Where-Object{$_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem'});if($rows.Count){$rows}}})
        Assert ($items.Count -lt 100) 'Details not virtualized'
        Click 'Close'
        if($InteractionsOnly){
            $list=Wait {All|Where-Object{$_.Current.AutomationId -eq 'TaskJobsList'}|Select-Object -First 1}
            $scroll=[System.Windows.Automation.ScrollPattern]$list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            $scroll.SetScrollPercent(-1,100)
            Wait {$scroll.Current.VerticalScrollPercent -ge 99.9}|Out-Null
            $rows=@($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|Where-Object{$_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem'})
            ([System.Windows.Automation.SelectionItemPattern]$rows[-1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            $before=Q 'task-state'
            Q 'language:zh-CN'|Out-Null;Q progress|Out-Null;Q 'language:en-US'|Out-Null
            $after=Q 'task-state'
            Assert ($before.selected -eq $after.selected -and $before.builds -eq $after.builds) 'Task selection or page identity lost'
            Assert ($scroll.Current.VerticalScrollPercent -ge 99.9) 'Task scroll lost'
            Q cancellable|Out-Null;Click 'Cancel subsequent requests'
            Wait {(Q 'task-state').canceled}|Out-Null
            Write-Output 'PASS large task selection, bottom scroll, bidirectional language and cancellation'
        } else {
        $baseline=Q baseline
        $result=[ordered]@{jobs=1001;historyItems=20000;activeItems=20000;materializedCards=$cards.Count;materializedDetails=$items.Count;localP95Ms=$progress.p95;localMaxMs=$progress.max;localSamples=$progress.samples;pageBuilds=$progress.builds;baselineP95Ms=$baseline.p95;baselineSamples=$baseline.samples}
        $result|ConvertTo-Json|Set-Content "$repo/artifacts/storage-v3-20260922/task-ui-benchmark.json" -Encoding utf8
        $result|ConvertTo-Json
        Write-Output 'PASS large task UI: stable page, bounded virtualized controls, committed local updates P95 <= 50 ms'
        }
    }
} finally {
    if(!$p.HasExited){$null=$p.CloseMainWindow();if(!$p.WaitForExit(10000)){$p.Kill();$p.WaitForExit()}}
    $pipe.Dispose();Remove-Item Env:VAMSYS_QA_PIPE -ErrorAction SilentlyContinue
}