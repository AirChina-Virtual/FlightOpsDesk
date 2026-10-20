param([ValidateSet(20000,50000)][int]$Rows=20000,[string]$Executable,[string]$OutputDirectory,[switch]$ReviewOnly)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(!$Executable){$Executable=Join-Path $repo 'artifacts/v3-qa/FlightOpsDesk.exe'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo "artifacts/ui-interactions/$Rows"}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Get-Process FlightOpsDesk -ErrorAction SilentlyContinue){throw 'An existing FlightOps Desk window is open; isolate QA before running'}
New-Item -ItemType Directory -Force -Path $OutputDirectory|Out-Null
$dataDirectory=Join-Path $OutputDirectory ('data-'+[Guid]::NewGuid().ToString('N'))
$pipeName='flightops-interactions-'+[Guid]::NewGuid().ToString('N')
& "$repo/.tools/dotnet/dotnet.exe" run --no-build --project "$repo/tests/VamSys.Tests" -c Release -- --seed-data-ui $dataDirectory $Rows
if($LASTEXITCODE -ne 0){throw 'Seed failed'}
$previousData=$env:VAMSYS_DATA_DIR;$previousPipe=$env:VAMSYS_QA_PIPE
$env:VAMSYS_DATA_DIR=$dataDirectory;$env:VAMSYS_QA_PIPE=$pipeName
$process=$null;$reader=$null;$writer=$null
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut)
function Q([string]$command){
    $writer.WriteLine($command);$pending=$reader.ReadLineAsync()
    if(!$pending.Wait(30000)){throw "Timed out: $command"}
    $line=$pending.GetAwaiter().GetResult();if(!$line){throw 'QA pipe closed'}
    $result=$line|ConvertFrom-Json;if($result.error){throw $result.error};return $result
}
function Capture([string]$path){ Start-Sleep -Milliseconds 400; & "$PSScriptRoot/ui-check.ps1" -Action Screenshot -OutputPath $path }
function Assert($condition,[string]$message){if(!$condition){throw $message}}
try{
    $process=Start-Process -FilePath $Executable -WindowStyle Hidden -PassThru
    $pipe.Connect(30000);$reader=[IO.StreamReader]::new($pipe);$writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true
    $opened=Q 'data-open';$initial=$opened.state
    if($ReviewOnly){
        Assert (@($initial.batchEnabled|Where-Object enabled).Count -eq 0) 'Batch actions enabled without selection'
        $one=Q 'data-select-first';Assert (@($one.batchEnabled|Where-Object {$_.target -eq 'delete' -and $_.enabled}).Count -eq 1) 'Deletion command did not follow selected targets'
        Q 'data-clear'|Out-Null
        Capture (Join-Path $OutputDirectory 'initial-view.png')
        Q 'data-window:1200,800'|Out-Null;Q 'language:zh-CN'|Out-Null
        $review=Q 'review-fixture'
        Assert ($review.ready -eq $review.total -and $review.resources.Count -ge 3 -and $review.renderedText) 'Review content was not rendered'
        Capture (Join-Path $OutputDirectory 'workspace-review.png')
        Q 'language:en-US'|Out-Null
        Capture (Join-Path $OutputDirectory 'workspace-review-en.png')
        $english=Q 'review-state';Assert ($english.renderedText -eq 'Airports') 'Review language did not update visible groups'
        Q 'language:zh-CN'|Out-Null
        $stale=Q 'review-stale';Assert $stale.refreshed 'Stale review was accepted'
        @{review=$review;stale=$stale}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $OutputDirectory 'review-verification.json') -Encoding utf8
        Q close|Out-Null;Assert ($process.WaitForExit(15000)) 'Review window did not close'
        Write-Output ("PASS workspace review: {0} changes, rendered {1}" -f $review.total,$review.renderedText);return
    }
    Assert ($initial.density -eq 'comfortable' -and $initial.actualRowHeight -eq 40) 'Comfortable default row height failed'
    Assert (@($initial.batchEnabled|Where-Object enabled).Count -eq 0) 'Batch actions enabled without a selection'
    Assert (!$initial.unapplied) 'Initial query incorrectly marked as unapplied'
    Q 'data-select-first'|Out-Null;Q 'data-page:2'|Out-Null;Q 'data-select-first'|Out-Null
    $inputState=Q 'data-page-input:9';Assert ($inputState.pageInput -eq '9') 'Selection refresh overwrote page input'
    $before=Q 'data-scroll:30,120'
    $compact=Q 'data-density:compact'
    @{before=$before;compact=$compact}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $OutputDirectory 'density-check.json') -Encoding utf8
    Capture (Join-Path $OutputDirectory 'after-density.png')
    Assert ($compact.actualRowHeight -eq 32 -and $compact.fullVisibleRows -gt $before.fullVisibleRows) 'Compact view did not increase visible rows'
    Assert ($compact.selected -eq 2 -and $compact.page -eq 2 -and $compact.anchor -eq $before.anchor) 'Density switch lost selection, page or visible row anchor'
    Assert ([Math]::Abs($compact.horizontal-$before.horizontal) -le 1) 'Density switch lost horizontal position'
    Assert ($compact.queryBuilds -eq $before.queryBuilds -and $compact.draftSaves -eq $before.draftSaves -and $compact.fullSaves -eq $before.fullSaves -and $compact.undo -eq $before.undo) 'Density switch queried or edited data'
    $restored=Q 'data-density:comfortable';Assert ($restored.anchor -eq $before.anchor -and $restored.actualRowHeight -eq 40) 'Comfortable restoration lost anchor'
    $unapplied=Q 'data-unapplied:pending condition'
    @{restored=$restored;unapplied=$unapplied}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $OutputDirectory 'query-check.json') -Encoding utf8
    Assert ($unapplied.unapplied -and $unapplied.queryBuilds -eq $restored.queryBuilds -and $unapplied.total -eq $Rows) 'Unapplied query changed results'
    Q ('data-query:'+(@{search='';filter=0;sort=$null}|ConvertTo-Json -Compress))|Out-Null
    $beforeDialog=Q 'data-state';$dialog=Q 'data-batch-dialog';Assert $dialog.locked 'Batch preview did not freeze the operation scope'
    Capture (Join-Path $OutputDirectory 'batch-dialog.png')
    $canceled=Q 'dialog-cancel'
    Assert ($canceled.unlocked -and $canceled.state.undo -eq $beforeDialog.undo -and $canceled.state.fullSaves -eq $beforeDialog.fullSaves) 'Cancel changed drafts or left controls locked'
    Q 'data-page:1'|Out-Null;Q 'data-scroll:0,0'|Out-Null
    $matrix=@()
    foreach($size in @('1200,800','700,650')){
        Q ('data-window:'+$size)|Out-Null
        foreach($language in @('zh-CN','en-US')){
            Q ('language:'+$language)|Out-Null
            foreach($theme in @('dark','light')){
                Q ('data-theme:'+$theme)|Out-Null
                foreach($density in @('comfortable','compact')){
                    $state=Q ('data-density:'+$density)
                    Assert ($state.density -eq $density -and $state.rows -eq 200 -and !$state.unapplied) 'Language/theme changed density or query state'
                    $name=($size.Replace(',','x'))+"-$language-$theme-$density.png"
                    Capture (Join-Path $OutputDirectory $name)
                    $matrix+=@{size=$size;language=$language;theme=$theme;density=$density;state=$state;screenshot=$name}
                }
            }
        }
    }
    Q 'data-window:1200,800'|Out-Null;Q 'language:zh-CN'|Out-Null;Q 'data-theme:dark'|Out-Null
    $review=Q 'review-fixture'
    $review|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $OutputDirectory 'review-check.json') -Encoding utf8
    Assert (!$review.resourcePickerVisible -and $review.resources.Count -ge 3 -and $review.ready -eq $review.total -and $review.rows -gt $review.total -and $review.renderedText) 'Workspace review omitted resource groups or showed a single-resource picker'
    Capture (Join-Path $OutputDirectory 'workspace-review.png')
    $stale=Q 'review-stale';Assert $stale.refreshed 'Stale workspace review was accepted'
    @{rows=$Rows;initial=$initial;before=$before;compact=$compact;restored=$restored;cancel=$canceled;review=$review;stale=$stale;matrix=$matrix;dataDirectory=$dataDirectory}|ConvertTo-Json -Depth 16|Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
    Q close|Out-Null;Assert ($process.WaitForExit(15000)) 'Final close did not save'
    Write-Output ("PASS {0} rows: comfortable {1} visible / compact {2}; {3} layout combinations at scale {4}; workspace review {5} changes" -f $Rows,$before.fullVisibleRows,$compact.fullVisibleRows,$matrix.Count,$initial.rasterizationScale,$review.total)
}finally{
    if($writer){$writer.Dispose()};if($reader){$reader.Dispose()};$pipe.Dispose()
    if($process -and !$process.HasExited){$process.Kill();$process.WaitForExit()}
    $env:VAMSYS_DATA_DIR=$previousData;$env:VAMSYS_QA_PIPE=$previousPipe
}
