param([string]$DataDirectory = ('F:\vamsys\artifacts\credentials-ui-' + [Guid]::NewGuid().ToString('N')))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$scope=[System.Windows.Automation.TreeScope]::Descendants
function Root { $p=Get-Process VamSys.App -ErrorAction SilentlyContinue | Select-Object -First 1; if($p -and $p.MainWindowHandle -ne [IntPtr]::Zero){[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)} }
function Elements { $r=Root; if($r){$r.FindAll($scope,[System.Windows.Automation.Condition]::TrueCondition)} }
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
& "$PSScriptRoot/../.tools/dotnet/dotnet.exe" run --no-build -c Release --project "$PSScriptRoot/../tests/VamSys.Tests" -- --seed-recovery-ui $DataDirectory
if($LASTEXITCODE -ne 0){throw 'Seed failed'}
$env:VAMSYS_DATA_DIR=$DataDirectory
function Launch { Start-Process "$PSScriptRoot/../artifacts/app/VamSys.App.exe" -WindowStyle Hidden }
function CloseTest { $p=Get-Process VamSys.App -ErrorAction SilentlyContinue;if($p){$null=$p.CloseMainWindow();if(!$p.WaitForExit(10000)){throw 'Test app did not close'}} }
Launch
try {
    SelectItem '连接与设置'
    Edit 'Client ID' 'rotated-ui-client'
    Edit 'Client Secret（留空保留已有凭据）' 'ui-test-secret-not-real'
    $setting=WaitFor {Named '航线使用机场本地时间（离线保持原值，不推测机场时区）' 'CheckBox'}
    ([System.Windows.Automation.TogglePattern]$setting.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
    Click '仅更新 API 凭据'
    WaitFor {Named '凭据已更新，请重新连接并核验同一 VA 后恢复任务。' 'Text'} | Out-Null
    SelectItem '任务中心';WaitFor {Named '核对未知结果' 'Button'} | Out-Null
    SelectItem '连接与设置';Language 'English'
    WaitFor {Named 'Update API credentials only' 'Button'} | Out-Null
    Assert ((Value (Named 'Client ID' 'Edit')) -eq 'rotated-ui-client') 'Updated Client ID was lost'
    Write-Output 'PASS credential-only update with an unresolved task and unsaved time setting; English entry visible; no connect invoked'
} finally {CloseTest}
Write-Output $DataDirectory
