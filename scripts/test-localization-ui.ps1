param([string]$DataDirectory, [string]$AppDirectory = "$PSScriptRoot/../artifacts/flightops-app")
$ErrorActionPreference = 'Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(!$DataDirectory){$DataDirectory=Join-Path $repo ('artifacts/ui-regression/test-localization-ui-'+[Guid]::NewGuid().ToString('N'))}
if(Test-Path -LiteralPath $DataDirectory){throw 'UI tests require a new isolated directory; existing data is never used'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$scope=[System.Windows.Automation.TreeScope]::Descendants
function Root { if($testProcess -and !$testProcess.HasExited){$testProcess.Refresh();if($testProcess.MainWindowHandle -ne [IntPtr]::Zero){[System.Windows.Automation.AutomationElement]::FromHandle($testProcess.MainWindowHandle)}} }
function Elements {
    for($attempt=0;$attempt -lt 3;$attempt++) {
        try { $r=Root; if($r){return $r.FindAll($scope,[System.Windows.Automation.Condition]::TrueCondition)}; return }
        catch { if($_.Exception.InnerException -isnot [Runtime.InteropServices.COMException]){throw}; Start-Sleep -Milliseconds 100 }
    }
    # The provider may be unavailable briefly during window startup; WaitFor is still bounded.
}
function Named($name,$type) { Elements | Where-Object { $_.Current.Name -eq $name -and (!$type -or $_.Current.ControlType.ProgrammaticName -eq "ControlType.$type") } | Select-Object -First 1 }
function WaitFor([scriptblock]$condition) { $limit=[DateTime]::UtcNow.AddSeconds(15); do { $result=& $condition; if($result){return $result}; Start-Sleep -Milliseconds 100 } while([DateTime]::UtcNow -lt $limit); throw "UI timeout: $condition" }
function Click($name) {
    # A page selected a moment ago may not be laid out yet: wait for the button before deciding it sits in a
    # command bar's overflow. Opening the overflow of a page that is still being replaced fails in UI Automation.
    $limit=[DateTime]::UtcNow.AddSeconds(2)
    do { $c=Named $name 'Button'; if($c){break}; Start-Sleep -Milliseconds 100 } while([DateTime]::UtcNow -lt $limit)
    if(!$c){
        $more=WaitFor {Elements | Where-Object {$_.Current.AutomationId -eq 'MoreButton'} | Select-Object -First 1}
        ([System.Windows.Automation.InvokePattern]$more.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        $c=WaitFor {Named $name 'Button'}
    }
    ([System.Windows.Automation.InvokePattern]$c.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function SelectItem($name) { $c=WaitFor {Named $name 'ListItem'}; ([System.Windows.Automation.SelectionItemPattern]$c.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
function Edit($name,$value) { $c=WaitFor {Named $name 'Edit'}; ([System.Windows.Automation.ValuePattern]$c.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($value) }
function Value($control) { ([System.Windows.Automation.ValuePattern]$control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value }
function Language($name) {
    $picker=Elements | Where-Object {$_.Current.AutomationId -eq 'LanguagePicker'} | Select-Object -First 1
    ([System.Windows.Automation.ExpandCollapsePattern]$picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    SelectItem $name
    Assert ((Root).Current.Name -eq "FlightOps Desk") "Brand name changed with UI language"
}
function DataRow { Elements | Where-Object {$_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $_.Current.Name.StartsWith('RowViewModel')} | Select-Object -First 1 }
function Assert($condition,$message) { if(!$condition){throw $message} }
if(Get-Process FlightOpsDesk -ErrorAction SilentlyContinue){throw 'Close the existing app before running isolated UI tests'}
$env:VAMSYS_DATA_DIR=$DataDirectory
$testProcess=Start-Process -FilePath (Join-Path $AppDirectory 'FlightOpsDesk.exe') -WindowStyle Hidden -PassThru
try {
WaitFor {Named '连接与设置' 'ListItem'} | Out-Null
SelectItem '连接与设置'
WaitFor {Named 'Client ID' 'Edit'} | Out-Null
Edit 'Client ID' 'unsaved-language-test'
$before=Named 'Client ID' 'Edit'; $runtimeId=$before.GetRuntimeId() -join ','
Language 'English'
WaitFor {Named 'Save connection settings' 'Button'} | Out-Null
$after=Named 'Client ID' 'Edit'
Assert ((Value $after) -eq 'unsaved-language-test') 'Unsaved setting was lost'
Assert (($after.GetRuntimeId() -join ',') -eq $runtimeId) 'Language switching recreated the editor'
Language '简体中文'
WaitFor {Named '保存连接设置' 'Button'} | Out-Null
Assert ((Value (Named 'Client ID' 'Edit')) -eq 'unsaved-language-test') 'Reverse switch lost setting'
Language 'English'
WaitFor {Named 'Save connection settings' 'Button'} | Out-Null
Write-Output 'PASS bidirectional in-place translation and unsaved settings'
SelectItem 'Workspaces'; Click 'Create demo workspace'
WaitFor {Named 'Demo · No live connection' 'Text'} | Out-Null
SelectItem 'Data management'
WaitFor {Named 'Callsign' 'Edit'} | Out-Null
Edit 'Callsign' 'DEM999'
Edit 'Search all fields' 'DEM999'
Click 'Filter / sort'
WaitFor { (Elements | Where-Object {$_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $_.Current.Name.StartsWith('RowViewModel')}).Count -eq 1 } | Out-Null
$row=DataRow
([System.Windows.Automation.SelectionItemPattern]$row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
SelectItem 'Connection & settings'; WaitFor {Named 'Interface language (applies immediately)' 'ComboBox'} | Out-Null
Language '简体中文'; WaitFor {Named '保存连接设置' 'Button'} | Out-Null
SelectItem '数据管理'; WaitFor {Named '搜索全部字段' 'Edit'} | Out-Null
Assert ((Value (Named '搜索全部字段' 'Edit')) -eq 'DEM999') 'Search state lost'
Assert ((Value (Named 'Callsign' 'Edit')) -eq 'DEM999') 'Edited data lost'
$row=DataRow
Assert (([System.Windows.Automation.SelectionItemPattern]$row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected) 'Row selection lost'
SelectItem '连接与设置'; WaitFor {Named '界面语言（立即生效）' 'ComboBox'} | Out-Null
Language 'English'; WaitFor {Named 'Save connection settings' 'Button'} | Out-Null
SelectItem 'Data management'; WaitFor {Named 'Choose fleet' 'Button'} | Out-Null
Click 'Choose fleet'; WaitFor {Named 'Select available fleets (multiple)' 'Text'} | Out-Null
$fleet=Named 'Airbus A320 · A320 · 2' 'CheckBox'
([System.Windows.Automation.TogglePattern]$fleet.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
Click 'Preview changes'; Click 'Apply to drafts'
WaitFor {Named 'Assign fleets' 'Button'} | Out-Null
Click 'Delete selected'; Click 'Confirm deletion'
WaitFor {Named 'Pending deletion' 'Text'} | Out-Null
$id=Named 'ID' 'Edit'
Assert (([System.Windows.Automation.ValuePattern]$id.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.IsReadOnly) 'Deleted row is editable'
# Restoring shows a preview of the rows it brings back; the change applies only after Confirm.
Click 'Restore deleted'; Click 'Confirm'; WaitFor {Named 'Unsubmitted draft' 'Text'} | Out-Null
Write-Output 'PASS draft, filter, selection, fleet dialog, delete and restore'
# The review covers the whole workspace: each changed resource has its own CSV export, and a demo workspace
# offers only the simulated submission, never the live API one.
SelectItem 'Change review'; WaitFor {Named 'Export Routes CSV' 'Button'} | Out-Null
Assert (Named 'Simulate workspace submission' 'Button') 'Demo submission missing from the review'
Assert (!(Named 'Submit workspace changes' 'Button')) 'Live API submission offered for a demo workspace'
Write-Output 'PASS English change review and disabled live API'
} finally {
    if(!$testProcess.HasExited){$null=$testProcess.CloseMainWindow();if(!$testProcess.WaitForExit(15000)){$testProcess.Kill();$testProcess.WaitForExit()}}
    Remove-Item Env:VAMSYS_DATA_DIR -ErrorAction SilentlyContinue
}
