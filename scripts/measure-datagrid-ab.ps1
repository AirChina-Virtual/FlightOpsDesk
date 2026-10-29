# R6 D3: the code-built data table and the DataGrid control measured on the same machine in alternating order, three
# rounds (old/new, new/old, old/new). Each run writes its own results under -OutputDirectory; summarize afterwards
# with -Summarize. Needs the QA package (scripts/build.ps1 -Publish -UiVerification). Takes about an hour.
param([string]$OutputDirectory,[int]$Rounds=3,[switch]$Summarize)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo 'artifacts/r6-d3'}
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null

function Median($values){$sorted=@($values|Where-Object{$_ -ne $null}|Sort-Object);if($sorted.Count -eq 0){return $null};$sorted[[int][Math]::Floor($sorted.Count/2)]}

if(!$Summarize){
    $previous=$env:VAMSYS_QA_DATAGRID
    try{
        for($round=1;$round -le $Rounds;$round++){
            $order=if($round%2 -eq 1){'old','new'}else{'new','old'}
            foreach($grid in $order){
                $env:VAMSYS_QA_DATAGRID=$(if($grid -eq 'new'){'new'}else{''})
                foreach($run in @(
                    @{name='perf-50000';script='test-data-performance-ui.ps1';args=@('-Rows','50000')},
                    @{name='perf-20000';script='test-data-performance-ui.ps1';args=@('-Rows','20000')},
                    @{name='baseline-50000';script='test-baseline-ui.ps1';args=@('-Rows','50000','-Launches','1')})){
                    $target=Join-Path $OutputDirectory "$($run.name)/$grid-$round"
                    New-Item -ItemType Directory -Force $target|Out-Null
                    $started=Get-Date
                    $text=pwsh -NoProfile -File (Join-Path $PSScriptRoot $run.script) @($run.args) -OutputDirectory $target 2>&1|Out-String -Width 4000
                    $text|Set-Content -LiteralPath (Join-Path $target 'output.txt') -Encoding utf8
                    $pass=[regex]::Match($text,'PASS[^\r\n]*').Value
                    "{0:HH:mm:ss} round {1} {2,-3} {3,-15} {4}" -f $started,$round,$grid,$run.name,$(if($pass){$pass}else{'FAILED (see output.txt)'})
                }
            }
        }
    }finally{$env:VAMSYS_QA_DATAGRID=$previous}
}

# Medians over the rounds, per table; the gate compares each 50k median with the old table's (no more than 10% slower).
$rows=@()
foreach($grid in 'old','new'){
    $perf50=@(Get-ChildItem (Join-Path $OutputDirectory 'perf-50000') -Directory -Filter "$grid-*" -ErrorAction SilentlyContinue|ForEach-Object{Get-Content (Join-Path $_.FullName 'benchmark.json') -Raw -ErrorAction SilentlyContinue|ConvertFrom-Json})
    $perf20=@(Get-ChildItem (Join-Path $OutputDirectory 'perf-20000') -Directory -Filter "$grid-*" -ErrorAction SilentlyContinue|ForEach-Object{Get-Content (Join-Path $_.FullName 'benchmark.json') -Raw -ErrorAction SilentlyContinue|ConvertFrom-Json})
    $base=@(Get-ChildItem (Join-Path $OutputDirectory 'baseline-50000') -Directory -Filter "$grid-*" -ErrorAction SilentlyContinue|ForEach-Object{Get-Content (Join-Path $_.FullName 'baseline.json') -Raw -ErrorAction SilentlyContinue|ConvertFrom-Json})
    function Native($m){if($m){($m.privateBytes-$m.committedBytes)/1MB}}
    $rows+=[pscustomobject][ordered]@{
        grid=$grid;runs50k=$perf50.Count;runs20k=$perf20.Count;baselineRuns=$base.Count
        open50k=Median $perf50.opened.milliseconds;search50k=Median $perf50.search.milliseconds
        paging50k=Median $perf50.benchmark.paging.median;language50k=Median $perf50.benchmark.language.median;edit50k=Median $perf50.benchmark.edit.median
        undo50k=Median $perf50.undoWithSave.totalMedian
        open20k=Median $perf20.opened.milliseconds;search20k=Median $perf20.search.milliseconds;paging20k=Median $perf20.benchmark.paging.median
        scroll50k=Median ($base|ForEach-Object{$_.scroll.page200.down.median});scrollP95_50k=Median ($base|ForEach-Object{$_.scroll.page200.down.p95})
        nativeStartMiB=Median ($base|ForEach-Object{Native $_.memory.start});nativeAfterSwitchMiB=Median ($base|ForEach-Object{Native $_.memory.afterSwitch})
        nativeGrowthMiB=Median ($base|ForEach-Object{(Native $_.memory.afterSwitch)-(Native $_.memory.start)})
        workingSetAfterSwitchMiB=Median ($base|ForEach-Object{$_.memory.afterSwitch.workingSetBytes/1MB})
    }
}
$rows|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
$rows|Format-List
$old=$rows|Where-Object grid -eq 'old';$new=$rows|Where-Object grid -eq 'new'
foreach($metric in 'open50k','search50k','paging50k','edit50k','undo50k','scroll50k'){
    if($old.$metric -and $new.$metric){"{0,-12} old {1,8:N2} new {2,8:N2}  {3,6:+0.0;-0.0}%  {4}" -f $metric,$old.$metric,$new.$metric,(($new.$metric/$old.$metric-1)*100),$(if($new.$metric -le $old.$metric*1.1){'within 10%'}else{'SLOWER than 10%'})}
}
if($old.nativeGrowthMiB -ne $null -and $new.nativeGrowthMiB -ne $null){"native growth old {0:N0} MiB new {1:N0} MiB  {2}" -f $old.nativeGrowthMiB,$new.nativeGrowthMiB,$(if($new.nativeGrowthMiB -le $old.nativeGrowthMiB){'not higher'}else{'HIGHER'})}
