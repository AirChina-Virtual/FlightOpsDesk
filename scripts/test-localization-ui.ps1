param([string]$DataDirectory = 'F:\vamsys\artifacts\ui-language-smoke')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$scope=[System.Windows.Automation.TreeScope]::Descendants
function Root { $p=Get-Process VamSys.App -ErrorAction SilentlyContinue | Select-Object -First 1; if($p -and $p.MainWindowHandle -ne [IntPtr]::Zero){[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)} }
function Elements {
    for($attempt=0;$attempt -lt 3;$attempt++) {
        try { $r=Root; if($r){return $r.FindAll($scope,[System.Windows.Automation.Condition]::TrueCondition)}; return }
        catch { if($_.Exception.InnerException -isnot [Runtime.InteropServices.COMException]){throw}; Start-Sleep -Milliseconds 100 }
    }
    # The provider may be unavailable briefly during window startup; WaitFor is still bounded.
}
function Named($name,$type) { Elements | Where-Object { $_.Current.Name -eq $name -and (!$type -or $_.Current.ControlType.ProgrammaticName -eq "ControlType.$type") } | Select-Object -First 1 }
function WaitFor([scriptblock]$condition) { $limit=[DateTime]::UtcNow.AddSeconds(15); do { $result=& $condition; if($result){return $result}; Start-Sleep -Milliseconds 100 } while([DateTime]::UtcNow -lt $limit); throw "UI timeout: $condition" }
function Click($name) { $c=Named $name 'Button'; if(!$c){$more=Elements | Where-Object {$_.Current.AutomationId -eq 'MoreButton'} | Select-Object -First 1; if($more){([System.Windows.Automation.InvokePattern]$more.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()}}; $c=WaitFor {Named $name 'Button'}; ([System.Windows.Automation.InvokePattern]$c.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
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
$env:VAMSYS_DATA_DIR=$DataDirectory
Start-Process -FilePath (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\app\VamSys.App.exe') -WindowStyle Hidden
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
Click 'Restore deleted'; WaitFor {Named 'Unsubmitted draft' 'Text'} | Out-Null
Write-Output 'PASS draft, filter, selection, fleet dialog, delete and restore'
SelectItem 'Change review'; WaitFor {Named 'Export changes as CSV' 'Button'} | Out-Null
Assert (!(Named 'Submit to API' 'Button').Current.IsEnabled) 'Live API unexpectedly enabled'
Write-Output 'PASS English change review and disabled live API'
