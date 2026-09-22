param([string]$DataDirectory=('F:\vamsys\artifacts\storage-v3-20260922\startup-ui-'+[Guid]::NewGuid().ToString('N')))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
if(Get-Process VamSys.App -ErrorAction SilentlyContinue){throw 'Close existing app before isolated test'}
$owned=[Collections.Generic.List[Diagnostics.Process]]::new()
function Launch([string]$dir) {
    $env:VAMSYS_DATA_DIR=$dir
    $p=Start-Process "$PSScriptRoot/../artifacts/app/VamSys.App.exe" -WindowStyle Hidden -PassThru
    $owned.Add($p);return $p
}
function Texts($process) {
    try {
        $process.Refresh()
        if($process.MainWindowHandle -eq [IntPtr]::Zero){return ''}
        $root=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        return (($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.Name}) -join [Environment]::NewLine)
    } catch {return ''}
}
function WaitText($process,[string[]]$need) {
    $limit=[DateTime]::UtcNow.AddSeconds(20)
    do {
        $text=Texts $process
        if(@($need|Where-Object{!$text.Contains($_)}).Count -eq 0){return}
        Start-Sleep -Milliseconds 100
    } while([DateTime]::UtcNow -lt $limit)
    throw "Missing startup text: $need"
}
try {
    & "$PSScriptRoot/../.tools/dotnet/dotnet.exe" run --no-build --project "$PSScriptRoot/../tests/VamSys.Tests" -c Release -- --seed-storage-ui $DataDirectory legacy
    if($LASTEXITCODE -ne 0){throw 'Legacy seed failed'}
    $primary=Launch $DataDirectory
    WaitText $primary @('Task center','Connection & settings')
    if(@(Get-ChildItem -LiteralPath $DataDirectory -Filter 'workspaces-before-v3-*.db').Count -ne 1){throw 'Migration backup not found'}
    Write-Output 'PASS legacy UI startup migrated with unique backup and English preference'
    $duplicate=Launch $DataDirectory
    WaitText $duplicate @('该数据目录已由另一个程序实例使用','Another application instance is using this data directory.')
    $null=$duplicate.CloseMainWindow();if(!$duplicate.WaitForExit(10000)){throw 'Duplicate did not close'}
    WaitText $primary @('Task center')
    Write-Output 'PASS second instance rejected with bilingual storage prompt; original remains usable'
    $null=$primary.CloseMainWindow();if(!$primary.WaitForExit(10000)){throw 'Primary did not close'}
    $futureDir=$DataDirectory+'-future'
    & "$PSScriptRoot/../.tools/dotnet/dotnet.exe" run --no-build --project "$PSScriptRoot/../tests/VamSys.Tests" -c Release -- --seed-storage-ui $futureDir future
    if($LASTEXITCODE -ne 0){throw 'Future seed failed'}
    $future=Launch $futureDir
    WaitText $future @('数据库由更新版本创建','This database requires a newer application version.')
    Write-Output 'PASS future storage format rejected in startup UI in both languages'
} finally {
    foreach($p in $owned) {
        if(!$p.HasExited){$null=$p.CloseMainWindow();if(!$p.WaitForExit(10000)){throw 'Owned QA instance did not close'}}
    }
}