# Opens the QA package on seeded test data for looking at the data table by hand, in the light or dark theme.
# High contrast and display scaling are Windows settings; change them before starting. The data lives under
# artifacts/, never in the user's data directory.
param([ValidateSet('light','dark')][string]$Theme='dark',[ValidateSet(20000,50000)][int]$Rows=20000,[string]$Executable)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(!$Executable){$Executable=Join-Path $repo 'artifacts/v3-qa/FlightOpsDesk.exe'}
if(!(Test-Path $Executable)){throw "QA package missing: run scripts/build.ps1 -Publish -UiVerification first"}
if(Get-Process FlightOpsDesk -ErrorAction SilentlyContinue){throw 'A FlightOps Desk window is already open; close it first'}
$dir=Join-Path $repo "artifacts/manual-datagrid/data-$Rows"
if(!(Test-Path (Join-Path $dir '*'))){
    & "$repo/.tools/dotnet/dotnet.exe" run --no-build --project "$repo/tests/VamSys.Tests" -c Release -- --seed-data-ui $dir $Rows
    if($LASTEXITCODE -ne 0){throw 'Seed failed'}
}
$saved=@{data=$env:VAMSYS_DATA_DIR;theme=$env:VAMSYS_QA_THEME;pipe=$env:VAMSYS_QA_PIPE}
try{
    $env:VAMSYS_DATA_DIR=$dir;$env:VAMSYS_QA_THEME=$Theme;$env:VAMSYS_QA_PIPE=''
    $process=Start-Process -FilePath $Executable -PassThru
    Write-Output "Opened the $Theme theme, $Rows rows (process $($process.Id)); data: $dir"
}finally{
    $env:VAMSYS_DATA_DIR=$saved.data;$env:VAMSYS_QA_THEME=$saved.theme;$env:VAMSYS_QA_PIPE=$saved.pipe
}
