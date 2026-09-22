param([string]$DataDirectory='F:\vamsys\artifacts\api-ui-20k')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
function Window { $p=Get-Process FlightOpsDesk -ErrorAction SilentlyContinue|Select-Object -First 1; if($p -and $p.MainWindowHandle -ne [IntPtr]::Zero){[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)} }
function All { try { $r=Window;if($r){$r.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)} } catch { return @() } }
function Named($n,$type) { All|Where-Object{$_.Current.Name -eq $n -and (!$type -or $_.Current.ControlType.ProgrammaticName -eq "ControlType.$type")}|Select-Object -First 1 }
function WaitFor([scriptblock]$f){$until=[DateTime]::UtcNow.AddSeconds(20);do{$r=& $f;if($r){return $r};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $until);throw "Timeout: $f"}
function Position { foreach($list in (All|Where-Object{$_.Current.ControlType.ProgrammaticName -eq 'ControlType.List'})){$pattern=$null;if($list.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern,[ref]$pattern) -and $pattern.Current.VerticallyScrollable){return $pattern.Current.VerticalScrollPercent}};return -1 }
function SelectItem($n){$e=WaitFor {Named $n 'ListItem'};([System.Windows.Automation.SelectionItemPattern]$e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()}
if(Get-Process FlightOpsDesk -ErrorAction SilentlyContinue){throw 'Close test app first'}
& "$PSScriptRoot/../.tools/dotnet/dotnet.exe" run --no-build -c Release --project "$PSScriptRoot/../tests/VamSys.Tests" -- --seed-ui $DataDirectory
if($LASTEXITCODE -ne 0){throw 'Seed failed'}
$env:VAMSYS_DATA_DIR=$DataDirectory
Start-Process "$PSScriptRoot/../artifacts/flightops-app/FlightOpsDesk.exe" -WindowStyle Hidden
SelectItem '数据管理'
WaitFor {Named 'ID' 'Edit'}|Out-Null
$materialized=@(All|Where-Object{$_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and $_.Current.Name.StartsWith('RowViewModel')}).Count
if($materialized -gt 100 -or $materialized -lt 1){throw "Virtualization failed: $materialized"}
$lists=All|Where-Object{$_.Current.ControlType.ProgrammaticName -eq 'ControlType.List'}
foreach($list in $lists){$pattern=$null;if($list.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern,[ref]$pattern) -and $pattern.Current.VerticallyScrollable){$pattern.SetScrollPercent(-1,100);break}}
Start-Sleep -Milliseconds 250
$idsBefore=@(All|Where-Object{$_.Current.Name -eq 'ID' -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Edit'}|ForEach-Object{([System.Windows.Automation.ValuePattern]$_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value})
$positionBefore=Position
SelectItem '连接与设置'
$picker=WaitFor {All|Where-Object{$_.Current.AutomationId -eq 'LanguagePicker'}|Select-Object -First 1}
([System.Windows.Automation.ExpandCollapsePattern]$picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
$timer=[System.Diagnostics.Stopwatch]::StartNew();SelectItem 'English';WaitFor {Named 'Connection & settings' 'ListItem'}|Out-Null;$timer.Stop()
SelectItem 'Data management';WaitFor {Named 'ID' 'Edit'}|Out-Null
Start-Sleep -Milliseconds 250
# Wait for the virtualized provider to finish restoring the last page. A single
# FindAll can return no controls during a transient provider refresh.
$idsAfter=@(WaitFor {
    $values=@(All|Where-Object{$_.Current.Name -eq 'ID' -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Edit'}|ForEach-Object{([System.Windows.Automation.ValuePattern]$_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value})
    if($values.Count -gt 0 -and $values[-1] -eq '19999'){return $values}
})
$positionAfter=Position
if([math]::Abs($positionBefore-$positionAfter) -gt 0.05 -or $positionAfter -lt 99.9 -or $idsAfter[-1] -ne '19999'){throw ('Scroll position lost: '+$positionBefore+' -> '+$positionAfter)}
if($idsAfter.Count -gt 100){throw 'Rows not virtualized'}
"PASS 20k UI: language switch $($timer.ElapsedMilliseconds) ms; materialized $materialized; restored rows $($idsAfter.Count); first ID $($idsAfter[0]); last ID $($idsAfter[-1]); scroll $positionBefore -> $positionAfter; working set $([math]::Round((Get-Process FlightOpsDesk).WorkingSet64/1MB)) MiB"
