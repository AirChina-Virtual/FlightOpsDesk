param([string]$DataDirectory = ('F:\vamsys\artifacts\recovery-ui-' + [Guid]::NewGuid().ToString('N')))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$scope=[System.Windows.Automation.TreeScope]::Descendants
function Root { $p=Get-Process VamSys.App -ErrorAction SilentlyContinue | Select-Object -First 1; if($p -and $p.MainWindowHandle -ne [IntPtr]::Zero){[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)} }
function Elements {
    for($attempt=0;$attempt -lt 3;$attempt++) {
        try { $r=Root; if($r){return $r.FindAll($scope,[System.Windows.Automation.Condition]::TrueCondition)}; return }
        catch { if($_.Exception.InnerException -isnot [Runtime.InteropServices.COMException]){throw}; Start-Sleep -Milliseconds 100 }
    }
    # A window can briefly disappear while restarting; WaitFor retains its bounded timeout.
}
function Named($name,$type) {
    $matches=@(Elements | Where-Object { $_.Current.Name -eq $name -and (!$type -or $_.Current.ControlType.ProgrammaticName -eq "ControlType.$type") })
    $dialogButton=$matches | Where-Object {$_.Current.AutomationId -in @('PrimaryButton','SecondaryButton','CloseButton')} | Select-Object -First 1
    if($dialogButton){return $dialogButton}; $matches | Select-Object -First 1
}
function WaitFor([scriptblock]$condition) { $limit=[DateTime]::UtcNow.AddSeconds(15); do { $result=& $condition; if($result){return $result}; Start-Sleep -Milliseconds 100 } while([DateTime]::UtcNow -lt $limit); throw "UI timeout: $condition" }
function Click($name) { Write-Host "UI click: $name"; $c=Named $name 'Button'; if(!$c){$more=Elements | Where-Object {$_.Current.AutomationId -eq 'MoreButton'} | Select-Object -First 1; if($more){([System.Windows.Automation.InvokePattern]$more.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()}}; $c=WaitFor {Named $name 'Button'}; ([System.Windows.Automation.InvokePattern]$c.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function SelectItem($name) { $c=WaitFor {Named $name 'ListItem'}; ([System.Windows.Automation.SelectionItemPattern]$c.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
function Edit($name,$value) { $c=WaitFor {Named $name 'Edit'}; ([System.Windows.Automation.ValuePattern]$c.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($value) }
function Value($control) { ([System.Windows.Automation.ValuePattern]$control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value }
function Language($name) {
    $picker=Elements | Where-Object {$_.Current.AutomationId -eq 'LanguagePicker'} | Select-Object -First 1
    ([System.Windows.Automation.ExpandCollapsePattern]$picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    SelectItem $name
}
function DataRow { Elements | Where-Object {$_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $_.Current.Name.StartsWith('RowViewModel')} | Select-Object -First 1 }
function Assert($condition,$message) { if(!$condition){throw $message} }
if(Get-Process VamSys.App -ErrorAction SilentlyContinue){throw 'Close the existing app before running isolated UI tests'}
& "$PSScriptRoot/../.tools/dotnet/dotnet.exe" run --no-build -c Release --project "$PSScriptRoot/../tests/VamSys.Tests" -- --seed-recovery-ui $DataDirectory
if($LASTEXITCODE -ne 0){throw 'Seed failed'}
$env:VAMSYS_DATA_DIR=$DataDirectory
function Launch { Start-Process "$PSScriptRoot/../artifacts/app/VamSys.App.exe" -WindowStyle Hidden }
function CloseTest {
    $p=Get-Process VamSys.App -ErrorAction SilentlyContinue
    if($p){
        # ShowAsync can complete after the Cancel button's Invoke returns.
        # The app correctly rejects close while that interaction is finishing.
        $until=[DateTime]::UtcNow.AddSeconds(10)
        do {$null=$p.CloseMainWindow();if($p.WaitForExit(200)){return}}while([DateTime]::UtcNow -lt $until)
        throw 'Test app did not close'
    }
}
function SaveCandidate {
    Click '保存候选 ID'
    # RuntimeId can be reused after a local page refresh. Observe the completed
    # save and closed editor; the next assertions reopen and restart the app.
    WaitFor {
        $button=Named '核对未知结果' 'Button'
        if($button -and $button.Current.IsEnabled -and !(Named 'Remote ID' 'Edit') -and (Named '候选 ID 已保存。连接后恢复任务，仅回读核对。' 'Text')){return $button}
    } | Out-Null
}
Launch
try {
    SelectItem '任务中心'
    Click '详情'
    WaitFor {Elements | Where-Object {$_.Current.Name.Contains('存在尚未核对成功的机场创建')}} | Out-Null
    WaitFor {Elements | Where-Object {$_.Current.Name.Contains('判重身份涉及尚未核对的写入')}} | Out-Null
    Click '关闭'
    Click '核对未知结果'
    Assert ((Value (WaitFor {Named 'Remote ID' 'Edit'})) -eq '102') 'Legacy recovery ID not shown'
    Edit 'Remote ID' '103';SaveCandidate
    WaitFor {Named '核对未知结果' 'Button'} | Out-Null
    Click '核对未知结果'
    Assert ((Value (WaitFor {Named 'Remote ID' 'Edit'})) -eq '103') 'Candidate cannot be reopened'
    Edit 'Remote ID' '101';SaveCandidate
    WaitFor {Named '核对未知结果' 'Button'} | Out-Null
    CloseTest
    Launch
    SelectItem '任务中心'
    Click '详情'
    WaitFor {Elements | Where-Object {$_.Current.Name.Contains('存在尚未核对成功的机场创建')}} | Out-Null
    WaitFor {Elements | Where-Object {$_.Current.Name.Contains('判重身份涉及尚未核对的写入')}} | Out-Null
    Click '关闭';Click '核对未知结果'
    Assert ((Value (WaitFor {Named 'Remote ID' 'Edit'})) -eq '101') 'Corrected candidate lost after restart'
    Click '取消'
    SelectItem '连接与设置';Language 'English'
    SelectItem 'Task center'
    Click 'Details'
    WaitFor {Elements | Where-Object {$_.Current.Name.Contains('An airport creation is still unverified.')}} | Out-Null
    WaitFor {Elements | Where-Object {$_.Current.Name.Contains('This identity may be affected by an unverified write.')}} | Out-Null
    Click 'Close'
    Click 'Review unknown results'
    Assert ((Value (WaitFor {Named 'Remote ID' 'Edit'})) -eq '101') 'English recovery editor lost candidate'
    WaitFor {Named 'Save candidate ID' 'Button'} | Out-Null
    Click 'Cancel'
    Write-Output 'PASS legacy candidate correction, repeated dialog entry, restart persistence and bilingual pending-identity details / recovery UI (offline, no credentials)'
} finally {CloseTest}
