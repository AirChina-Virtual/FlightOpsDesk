param([ValidateSet(20000,50000)][int]$Rows=50000,[string]$Executable,[string]$OutputDirectory,[switch]$ScreenshotsOnly)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(!$Executable){$Executable=Join-Path $repo 'artifacts/v3-qa/FlightOpsDesk.exe'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo "artifacts/ui-performance/$Rows"}
$dir=Join-Path $OutputDirectory ('data-'+[Guid]::NewGuid().ToString('N'))
$pipeName='flightops-data-'+[Guid]::NewGuid().ToString('N')
if(Get-Process FlightOpsDesk -ErrorAction SilentlyContinue){throw 'An existing FlightOps Desk window is open; isolate QA before running'}
& "$repo/.tools/dotnet/dotnet.exe" run --no-build --project "$repo/tests/VamSys.Tests" -c Release -- --seed-data-ui $dir $Rows
if($LASTEXITCODE -ne 0){throw 'Seed failed'}
$env:VAMSYS_DATA_DIR=$dir;$env:VAMSYS_QA_PIPE=$pipeName
$process=Start-Process -FilePath $Executable -WindowStyle Hidden -PassThru
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut)
function Q([string]$command){
    $writer.WriteLine($command);$pending=$reader.ReadLineAsync()
    if(!$pending.Wait(30000)){throw "Timed out: $command"}
    $line=$pending.GetAwaiter().GetResult();if(!$line){throw 'QA pipe closed'}
    $result=$line|ConvertFrom-Json;if($result.error){throw $result.error};return $result
}
function Assert($value,[string]$message){if(!$value){throw $message}}
function Query([string]$search,[int]$filter=0,[string]$sort){Q ('data-query:'+(@{search=$search;filter=$filter;sort=$sort}|ConvertTo-Json -Compress))}
try{
    $pipe.Connect(30000);$reader=[IO.StreamReader]::new($pipe);$writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true
    $opened=Q 'data-open'
    Assert ($opened.state.total -eq $Rows -and $opened.state.rows -eq 200 -and $opened.state.pageSize -eq 200 -and $opened.state.hasEditors) 'Initial page or editor rendering failed'
    Q ('data-screenshot:'+(Join-Path $OutputDirectory 'initial-page.png'))|Out-Null
    if($ScreenshotsOnly){
        Q ('data-page:'+($Rows/200))|Out-Null;Q ('data-screenshot:'+(Join-Path $OutputDirectory 'last-page.png'))|Out-Null
        Q 'language:zh-CN'|Out-Null;Q 'data-page:1'|Out-Null;Q ('data-screenshot:'+(Join-Path $OutputDirectory 'first-page-zh.png'))|Out-Null
        Q close|Out-Null;Assert ($process.WaitForExit(15000)) 'Screenshot fixture did not close';Write-Output 'PASS isolated page screenshots';return
    }
    Q 'data-select-first'|Out-Null;Q 'data-page:2'|Out-Null;$selected=Q 'data-select-first'
    Assert ($selected.selected -eq 2 -and $selected.selectedRows -contains '0' -and $selected.selectedRows -contains '200') 'Cross-page selection lost targets'
    $rebound=Q 'data-edit-rebound';Assert ($rebound.correct -and $rebound.current -eq '200' -and $rebound.previous -eq '0') 'Reused native editor changed the previous page'
    $sorted=Query '' 0 'ID';Assert ($sorted.state.selected -eq 2) 'Sorting cleared selection'
    $search=Query ('row '+($Rows-1))
    Assert ($search.state.total -eq 1 -and $search.state.first -eq [string]($Rows-1) -and $search.state.selected -eq 0 -and $search.state.page -eq 1) 'Search did not cover the full dataset'
    $empty=Query 'no-such-record-20261015';Assert ($empty.state.rows -eq 0 -and $empty.state.pages -eq 1) 'Empty query failed'
    Query ''|Out-Null
    Q 'data-page:3'|Out-Null;$small=Q 'data-size:100';Assert ($small.page -eq 5 -and $small.first -eq '400') 'Page size anchor lost'
    $large=Q 'data-size:500';Assert ($large.rows -eq 500 -and $large.page -eq 1) '500 row page wrong'
    Q 'data-size:200'|Out-Null;$all=Q 'data-select-all'
    Assert ($all.selected -eq $Rows -and $all.rows -eq 200) 'Selecting all results created all page models'
    Q 'data-clear'|Out-Null;$canceled=Q 'data-cancel-test';Assert $canceled.retained 'Canceled query replaced results'
    $metrics=Q 'data-benchmark'
    @{opened=$opened;search=$search;benchmark=$metrics;dataDirectory=$dir}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $OutputDirectory 'measurements.json') -Encoding utf8
    Assert ($metrics.buildsBefore -eq $metrics.buildsAfter -and $metrics.queriesBefore -eq $metrics.queriesAfter) 'Paging rebuilt the data page or query'
    Assert ($metrics.state.rows -le 200 -and $metrics.state.cells -eq $metrics.state.rows*$metrics.state.columns) 'Cell models exceeded current page'
    Assert ($metrics.paging.p95 -le 150 -and $metrics.language.p95 -le 150 -and $metrics.edit.p95 -le 50) 'UI P95 exceeded the agreed limits'
    Assert ($opened.milliseconds -le 1000 -and $search.milliseconds -le 1000) 'Full-data query exceeded 1 second'
    $last=Q ('data-page:'+($Rows/200));Assert ($last.last -eq [string]($Rows-1)) 'Last record not reachable'
    Q ('data-screenshot:'+(Join-Path $OutputDirectory 'last-page.png'))|Out-Null
    Q 'language:zh-CN'|Out-Null;Q 'data-page:1'|Out-Null
    Q ('data-screenshot:'+(Join-Path $OutputDirectory 'first-page-zh.png'))|Out-Null
    $undoSamples=@();for($n=0;$n -lt 25;$n++){$sample=Q 'data-undo-save';if($n -ge 5){$undoSamples+=$sample}}
    $undoOrdered=@($undoSamples.totalMilliseconds|Sort-Object);$redoOrdered=@($undoSamples.redoMilliseconds|Sort-Object)
    $undo=@{samples=$undoSamples;totalMedian=$undoOrdered[10];totalP95=$undoOrdered[18];redoMedian=$redoOrdered[10];redoP95=$redoOrdered[18]}
    # Replacing undo/redo steps must not fall back to a full workspace save.
    Assert (@($undoSamples|Where-Object{$_.fullCommits -ne 0 -or $_.draftCommits -ne 2}).Count -eq 0) 'Undo/redo of a replaced row did not use the row save'
    # ...nor re-run the full-data query; the shown rows are rebound in place instead.
    Assert (@($undoSamples|Where-Object{$_.queries -ne 0 -or !$_.rowsMatch}).Count -eq 0) 'Undo/redo re-queried or left stale rows on the page'
    $process.Refresh();$result=@{rows=$Rows;opened=$opened;search=$search;benchmark=$metrics;last=$last;workingSetBytes=$process.WorkingSet64;dataDirectory=$dir}
    $result.undoWithSave=$undo;$result.reboundEditor=$rebound
    $result|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $OutputDirectory 'benchmark.json') -Encoding utf8
    Q close|Out-Null;Assert ($process.WaitForExit(15000)) 'Final close/save did not finish'
    Write-Output ("PASS {0} rows: open {1:N1} ms; search {2:N1} ms; paging P95 {3:N1} ms; language P95 {4:N1} ms; edit P95 {5:N1} ms; models {6} rows / {7} cells" -f $Rows,$opened.milliseconds,$search.milliseconds,$metrics.paging.p95,$metrics.language.p95,$metrics.edit.p95,$metrics.state.rows,$metrics.state.cells)
}finally{
    if($writer){$writer.Dispose()};if($reader){$reader.Dispose()};$pipe.Dispose()
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    Remove-Item Env:VAMSYS_DATA_DIR -ErrorAction SilentlyContinue;Remove-Item Env:VAMSYS_QA_PIPE -ErrorAction SilentlyContinue
}
