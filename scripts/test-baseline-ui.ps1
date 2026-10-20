# Baseline measurements not covered by test-data-performance-ui.ps1: process startup, workspace load and
# switch, review page with growing change sets, per-frame scrolling and memory. Measures only; no limits asserted.
param([ValidateSet(20000,50000)][int]$Rows=50000,[string]$Executable,[string]$OutputDirectory,[int]$Launches=5)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(!$Executable){$Executable=Join-Path $repo 'artifacts/v3-qa/FlightOpsDesk.exe'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo ("artifacts/ui-baseline/$Rows")}
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null
$dir=Join-Path $OutputDirectory ('data-'+[Guid]::NewGuid().ToString('N'))
if(Get-Process FlightOpsDesk -ErrorAction SilentlyContinue){throw 'An existing FlightOps Desk window is open; isolate QA before running'}
& "$repo/.tools/dotnet/dotnet.exe" run --no-build --project "$repo/tests/VamSys.Tests" -c Release -- --seed-data-ui $dir $Rows
if($LASTEXITCODE -ne 0){throw 'Seed failed'}
$env:VAMSYS_DATA_DIR=$dir

function Start-Qa{
    $script:pipeName='flightops-baseline-'+[Guid]::NewGuid().ToString('N');$env:VAMSYS_QA_PIPE=$script:pipeName
    # A visible window, so composition frames are produced at the display rate.
    $script:process=Start-Process -FilePath $Executable -PassThru
    $script:pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$script:pipeName,[IO.Pipes.PipeDirection]::InOut)
    $script:pipe.Connect(60000);$script:reader=[IO.StreamReader]::new($script:pipe);$script:writer=[IO.StreamWriter]::new($script:pipe);$script:writer.AutoFlush=$true
}
function Stop-Qa{
    try{Q close|Out-Null;if(!$script:process.WaitForExit(120000)){throw 'Close/save did not finish'}}
    finally{
        if($script:writer){$script:writer.Dispose();$script:writer=$null};if($script:reader){$script:reader.Dispose();$script:reader=$null};if($script:pipe){$script:pipe.Dispose();$script:pipe=$null}
        if($script:process -and !$script:process.HasExited){$script:process.Kill();$script:process.WaitForExit()}
    }
}
function Q([string]$command){
    $script:writer.WriteLine($command);$pending=$script:reader.ReadLineAsync()
    if(!$pending.Wait(180000)){throw "Timed out: $command"}
    $line=$pending.GetAwaiter().GetResult();if(!$line){throw 'QA pipe closed'}
    $result=$line|ConvertFrom-Json;if($result.error){throw $result.error};return $result
}
function Median($values){$sorted=@($values|Sort-Object);return $sorted[[int][Math]::Floor($sorted.Count/2)]}

try{
    $startups=@();$loads=@()
    for($n=0;$n -lt $Launches;$n++){
        Start-Qa
        $startup=Q 'startup';$memory=Q 'memory';$load=Q 'workspace-load'
        $startups+=[pscustomobject]@{launch=$n;constructedMilliseconds=$startup.constructedMilliseconds;firstFrameMilliseconds=$startup.firstFrameMilliseconds;memory=$memory}
        $loads+=$load
        Stop-Qa
        Write-Output ("launch {0}: constructed {1:N0} ms, first frame {2:N0} ms, working set {3:N0} MiB" -f $n,$startup.constructedMilliseconds,$startup.firstFrameMilliseconds,($memory.workingSetBytes/1MB))
    }

    Start-Qa
    $memory=[ordered]@{start=(Q 'memory')}
    $opened=Q 'data-open';$memory.dataPage=Q 'memory'
    $scroll200=Q 'scroll-bench:120'
    Q 'data-size:500'|Out-Null;$scroll500=Q 'scroll-bench:120';Q 'data-size:200'|Out-Null
    $memory.afterScroll=Q 'memory'
    $switches=@();for($n=0;$n -lt 5;$n++){$switches+=Q 'workspace-switch'}
    $memory.afterSwitch=Q 'memory'
    $reviews=@()
    foreach($count in @(1000,10000,$Rows)){$reviews+=Q ("review-bench:$count");Write-Output ("review {0} changes: plan {1:N0} ms, render {2:N0} ms" -f $count,(Median $reviews[-1].planMilliseconds),(Median $reviews[-1].renderMilliseconds))}
    $memory.review=Q 'memory'
    Stop-Qa

    $result=[ordered]@{rows=$Rows;machine=[ordered]@{cpu=(Get-CimInstance Win32_Processor|Select-Object -First 1).Name;os=[Environment]::OSVersion.VersionString;logicalProcessors=[Environment]::ProcessorCount}
        startup=$startups;workspaceLoad=$loads;dataOpen=$opened;scroll=[ordered]@{page200=$scroll200;page500=$scroll500};workspaceSwitch=$switches;review=$reviews;memory=$memory;dataDirectory=$dir}
    $result|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $OutputDirectory 'baseline.json') -Encoding utf8
    Write-Output ("PASS {0} rows: first frame median {1:N0} ms; load median {2:N0} ms; switch away/back median {3:N0}/{4:N0} ms; scroll 200 P95 {5:N1} ms ({6} frames > 33 ms); scroll 500 P95 {7:N1} ms; data page working set {8:N0} MiB" -f `
        $Rows,(Median $startups.firstFrameMilliseconds),(Median ($loads|ForEach-Object{$_.samples})),(Median $switches.awayMilliseconds),(Median $switches.backMilliseconds),$scroll200.down.p95,$scroll200.down.over33,$scroll500.down.p95,($memory.dataPage.workingSetBytes/1MB))
}finally{
    if($script:process -and !$script:process.HasExited){$script:process.Kill();$script:process.WaitForExit()}
    Remove-Item Env:VAMSYS_DATA_DIR -ErrorAction SilentlyContinue;Remove-Item Env:VAMSYS_QA_PIPE -ErrorAction SilentlyContinue
}
