# Real pointer and keyboard editing of text-only cells: a click opens the editor, typing commits,
# Tab moves to the next cell's editor, and automation reports values and read-only state of deleted rows.
param([string]$Executable,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms
Add-Type -Namespace QaInput -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
'@
$repo=Split-Path $PSScriptRoot -Parent
if(!$Executable){$Executable=Join-Path $repo 'artifacts/v3-qa/FlightOpsDesk.exe'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo 'artifacts/ui-cells/editing'}
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null
$dir=Join-Path $OutputDirectory ('data-'+[Guid]::NewGuid().ToString('N'))
if(Get-Process FlightOpsDesk -ErrorAction SilentlyContinue){throw 'An existing FlightOps Desk window is open; isolate QA before running'}
& "$repo/.tools/dotnet/dotnet.exe" run --no-build --project "$repo/tests/VamSys.Tests" -c Release -- --seed-data-ui $dir 20000
if($LASTEXITCODE -ne 0){throw 'Seed failed'}
$pipeName='flightops-cells-'+[Guid]::NewGuid().ToString('N')
$env:VAMSYS_DATA_DIR=$dir;$env:VAMSYS_QA_PIPE=$pipeName
$process=Start-Process -FilePath $Executable -PassThru
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut)
function Q([string]$command){$writer.WriteLine($command);$t=$reader.ReadLineAsync();if(!$t.Wait(60000)){throw "Timed out: $command"};$r=$t.Result|ConvertFrom-Json;if($r.error){throw $r.error};$r}
function Assert($value,[string]$message){if(!$value){throw $message}}
function Cells([string]$name){
    $process.Refresh();$root=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $condition=New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name)),(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)))
    @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition))
}
function Value($element){([System.Windows.Automation.ValuePattern]$element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current}
function Wait([scriptblock]$condition){$limit=[DateTime]::UtcNow.AddSeconds(15);do{$v=& $condition;if($v){return $v};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $limit);throw "Timeout: $condition"}
try{
    $pipe.Connect(60000);$reader=[IO.StreamReader]::new($pipe);$writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true
    $opened=Q 'data-open';Assert ($opened.state.hasEditors -and $opened.state.textBoxes -eq 0) "Cells created editors before editing ($($opened.state.textBoxes))"
    # Pointer: click the second row's Departure cell, type, then Tab into the next cell and type there.
    # Digits only: an active Chinese IME composes letters and converts punctuation. The editor opens with the caret
    # at the end; End would reach the list there and move focus to the last row, as with any single-line TextBox.
    [QaInput.Native]::SetForegroundWindow($process.MainWindowHandle)|Out-Null;Start-Sleep -Milliseconds 300
    # A column inside the window; Remarks is scrolled out of view horizontally.
    $departures=Wait {$c=Cells 'Departure Airport (ICAO/IATA)';if($c.Count -gt 1 -and !$c[1].Current.BoundingRectangle.IsEmpty){,$c}}
    $target=$departures[1].Current.BoundingRectangle;$x=[int]($target.X+$target.Width/2);$y=[int]($target.Y+$target.Height/2)
    [QaInput.Native]::SetCursorPos($x,$y)|Out-Null;[QaInput.Native]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[QaInput.Native]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    $editing=Wait {$s=Q 'data-row:1';if($s.focused -eq 'TextBox'){$s}}
    Assert ($editing.editors -eq 1) "Click opened $($editing.editors) editors"
    [System.Windows.Forms.SendKeys]::SendWait('123')
    $typed=Wait {$s=Q 'data-row:1';if($s.fields.'Departure Airport (ICAO/IATA)' -eq 'ZBAA123'){$s}}
    # The column after Departure.
    $before=(Q 'data-row:1').fields.'Arrival Airport (ICAO/IATA)'
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
    Wait {$s=Q 'data-row:1';if($s.focused -eq 'TextBox'){$s}}|Out-Null
    [System.Windows.Forms.SendKeys]::SendWait('456')
    $tabbed=Wait {$s=Q 'data-row:1';if($s.fields.'Arrival Airport (ICAO/IATA)' -eq ($before+'456')){$s}}
    Assert ($tabbed.editors -eq 1) "Tab left $($tabbed.editors) editors open"
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    $closed=Wait {$s=Q 'data-row:1';if($s.editors -eq 0){$s}}
    Assert ($closed.focused -eq 'EditableCell') "Escape moved focus to $($closed.focused)"
    # Automation: one edit element per realized cell, with the row value.
    $remarks=Wait {$c=Cells 'Remarks';if($c.Count -gt 0){,$c}}
    Assert ((Value $remarks[0]).Value -eq 'row 0' -and !(Value $remarks[0]).IsReadOnly) 'Automation value of the first Remarks cell is wrong'
    ([System.Windows.Automation.ValuePattern]$remarks[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('set by automation')
    $set=Q 'data-row:0';Assert ($set.fields.Remarks -eq 'set by automation' -and $set.editors -eq 0) "Automation SetValue did not edit the row, or opened $($set.editors) editors"
    # Deleted rows are read-only to automation.
    $deleted=Q 'data-mark-deleted:2';Assert $deleted.readOnly 'Deleted row cells are editable'
    $row2=@(Cells 'Remarks'|Where-Object{(Value $_).Value -eq 'row 2'})
    Assert ($row2.Count -eq 1 -and (Value $row2[0]).IsReadOnly) 'Automation reports a deleted row as editable'
    Q close|Out-Null;Assert ($process.WaitForExit(30000)) 'Close did not finish'
    Write-Output 'PASS text cells: no editors until edited; automation value/SetValue/read-only; click, type, Tab and Escape edit the live row'
}catch{
    $state=try{Q 'data-row:1'|ConvertTo-Json -Compress -Depth 4}catch{'unavailable'}
    $focus=try{$f=[System.Windows.Automation.AutomationElement]::FocusedElement;"$($f.Current.ControlType.ProgrammaticName) '$($f.Current.Name)' pid $($f.Current.ProcessId)"}catch{'unknown'}
    throw "$_ | row 1: $state | UIA focus: $focus"
}finally{
    if($writer){$writer.Dispose()};if($reader){$reader.Dispose()};$pipe.Dispose()
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    Remove-Item Env:VAMSYS_DATA_DIR -ErrorAction SilentlyContinue;Remove-Item Env:VAMSYS_QA_PIPE -ErrorAction SilentlyContinue
}
