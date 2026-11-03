# The data table footer through UI Automation, as a user or screen reader reaches it: paging buttons and their
# availability, the page jump (Go button, Enter key, invalid input), the page size picker, the selection menu and
# the automation names after a language change.
param([string]$Executable,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms
Add-Type -Namespace QaFooter -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
'@
$repo=Split-Path $PSScriptRoot -Parent
if(!$Executable){$Executable=Join-Path $repo 'artifacts/v3-qa/FlightOpsDesk.exe'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo 'artifacts/ui-footer'}
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null
$dir=Join-Path $OutputDirectory ('data-'+[Guid]::NewGuid().ToString('N'))
if(Get-Process FlightOpsDesk -ErrorAction SilentlyContinue){throw 'An existing FlightOps Desk window is open; isolate QA before running'}
& "$repo/.tools/dotnet/dotnet.exe" run --no-build --project "$repo/tools/VamSys.QaSeed" -c Release -- --seed-data-ui $dir 20000
if($LASTEXITCODE -ne 0){throw 'Seed failed'}
$pipeName='flightops-footer-'+[Guid]::NewGuid().ToString('N')
$env:VAMSYS_DATA_DIR=$dir;$env:VAMSYS_QA_PIPE=$pipeName
$process=Start-Process -FilePath $Executable -PassThru
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut)
$A=[System.Windows.Automation.AutomationElement]
function Q([string]$command){$writer.WriteLine($command);$t=$reader.ReadLineAsync();if(!$t.Wait(60000)){throw "Timed out: $command"};$r=$t.Result|ConvertFrom-Json;if($r.error){throw $r.error};$r}
function Assert($value,[string]$message){if(!$value){throw $message}}
function Wait([scriptblock]$until,[string]$what){$limit=[DateTime]::UtcNow.AddSeconds(15);do{$v=& $until;if($v){return $v};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $limit);throw "Timeout: $what"}
function Root{$process.Refresh();$A::FromHandle($process.MainWindowHandle)}
function Find([string]$id){@((Root).FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty,$id))))}
function One([string]$id){Wait {$e=Find $id;if($e.Count -eq 1){$e[0]}} "one element $id"}
function Invoke($element){([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()}
function SetText($element,[string]$text){([System.Windows.Automation.ValuePattern]$element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($text)}
function Text($element){([System.Windows.Automation.ValuePattern]$element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value}
function State{Q 'data-state'}
function Page([int]$page,[string]$what){Wait {$s=State;if($s.page -eq $page){$s}} "$what (page $page)"}
function Enabled([string]$id){(One $id).Current.IsEnabled}
# The selection menu button has no AutomationId; it is the one button named by TableSelection.
function SelectionButton([string]$name){
    $condition=New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($A::NameProperty,$name)),(New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
    Wait {$e=@((Root).FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition));if($e.Count -eq 1){$e[0]}} "selection button '$name'"
}
function MenuItem([string]$id){
    Invoke (SelectionButton 'Select')
    One $id
}
function PickPageSize([int]$size){
    $box=One 'DataPageSize'
    ([System.Windows.Automation.ExpandCollapsePattern]$box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    # The open list is a popup; look for its item from the window root.
    $condition=New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($A::NameProperty,[string]$size)),(New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))
    $item=Wait {$i=(Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition);if($i){$i}} "page size item $size"
    ([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    $pattern=[System.Windows.Automation.ExpandCollapsePattern]$box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if($pattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed){$pattern.Collapse()}
}
$footerIds='DataFirstPage','DataPreviousPage','DataNextPage','DataLastPage','DataPageJump','DataGoPage','DataPageSize'
try{
    $pipe.Connect(60000);$reader=[IO.StreamReader]::new($pipe);$writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true
    Q 'language:en-US'|Out-Null
    $opened=Q 'data-open'
    Assert ($opened.state.total -eq 20000 -and $opened.state.pages -eq 100 -and $opened.state.page -eq 1) 'Unexpected initial page'
    foreach($id in $footerIds){One $id|Out-Null}
    # Not querying: the cancel button is collapsed and not in the automation tree.
    Assert ((Find 'DataQueryCancel').Count -eq 0) 'Query cancel shown while idle'

    # Paging buttons and their availability at both ends.
    Assert (!(Enabled 'DataFirstPage') -and !(Enabled 'DataPreviousPage') -and (Enabled 'DataNextPage') -and (Enabled 'DataLastPage')) 'First page availability wrong'
    Invoke (One 'DataNextPage');$s=Page 2 'Next'
    Assert ($s.first -eq '200' -and (Text (One 'DataPageJump')) -eq '2') "Next showed row $($s.first), jump box '$(Text (One 'DataPageJump'))'"
    Invoke (One 'DataLastPage');$s=Page 100 'Last'
    Assert ($s.last -eq '19999') 'Last page does not end with the last row'
    Wait {if(!(Enabled 'DataNextPage') -and !(Enabled 'DataLastPage') -and (Enabled 'DataFirstPage')){$true}} 'last page availability'|Out-Null
    Invoke (One 'DataPreviousPage');Page 99 'Previous'|Out-Null
    Invoke (One 'DataFirstPage');Page 1 'First'|Out-Null

    # Page jump: the Go button, the Enter key, and invalid input that keeps the page and the typed text.
    SetText (One 'DataPageJump') '7';Invoke (One 'DataGoPage');Page 7 'Go'|Out-Null
    $process.Refresh();[QaFooter.Native]::SetForegroundWindow($process.MainWindowHandle)|Out-Null;Start-Sleep -Milliseconds 300
    Assert ([QaFooter.Native]::GetForegroundWindow() -eq $process.MainWindowHandle) 'QA window is not foreground; keep it active before retrying keyboard input'
    $jump=One 'DataPageJump';SetText $jump '12';$jump.SetFocus();Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}');Page 12 'Enter in the page jump'|Out-Null
    SetText (One 'DataPageJump') '999';Invoke (One 'DataGoPage')
    $status=Wait {$t=(One 'StatusLabel').Current.Name;if($t -eq 'Enter a valid page number.'){$t}} 'invalid page status'
    $s=State;Assert ($s.page -eq 12 -and $s.pageInput -eq '999') "Invalid page moved to $($s.page) or reset the box to '$($s.pageInput)'"

    # Page size keeps the first shown row on screen.
    SetText (One 'DataPageJump') '3';Invoke (One 'DataGoPage');Page 3 'Go to 3'|Out-Null
    PickPageSize 100;$s=Wait {$x=State;if($x.pageSize -eq 100){$x}} 'page size 100'
    Assert ($s.page -eq 5 -and $s.first -eq '400' -and $s.rows -eq 100) "Page size 100 showed page $($s.page) from row $($s.first)"
    Assert ((Text (One 'DataPageJump')) -eq '5') 'Jump box did not follow the anchored page'
    PickPageSize 200;$s=Wait {$x=State;if($x.pageSize -eq 200){$x}} 'page size 200'
    Assert ($s.page -eq 3 -and $s.rows -eq 200) "Page size 200 showed page $($s.page)"

    # Selection menu: this page, all results, clear; clearing is offered only when something is selected.
    Invoke (MenuItem 'DataSelectPage');$s=Wait {$x=State;if($x.selected -eq 200){$x}} 'select page'
    Invoke (MenuItem 'DataSelectAll');Wait {$x=State;if($x.selected -eq 20000){$x}} 'select all'|Out-Null
    Invoke (MenuItem 'DataClearSelection');Wait {$x=State;if($x.selected -eq 0){$x}} 'clear selection'|Out-Null
    $clear=MenuItem 'DataClearSelection';Assert (!$clear.Current.IsEnabled) 'Clear selection offered with nothing selected'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}');Wait {if((Find 'DataClearSelection').Count -eq 0){$true}} 'menu closed'|Out-Null

    # Automation names follow the language.
    $english=@{};foreach($id in 'DataNextPage','DataGoPage','DataPageJump','DataPageSize'){$english[$id]=(One $id).Current.Name}
    Assert ($english.DataNextPage -eq 'Next' -and $english.DataGoPage -eq 'Go' -and $english.DataPageJump -eq 'Go') "English names: $($english|ConvertTo-Json -Compress)"
    Q 'language:zh-CN'|Out-Null
    Wait {if((One 'DataNextPage').Current.Name -eq '下一页' -and (One 'DataGoPage').Current.Name -eq '跳转' -and (One 'DataPageJump').Current.Name -eq '跳转' -and (One 'DataPageSize').Current.Name -ne $english.DataPageSize){$true}} ("Chinese automation names; now: "+((@('DataNextPage','DataGoPage','DataPageJump','DataPageSize')|ForEach-Object{$_+'='+(One $_).Current.Name}) -join ', '))|Out-Null
    Invoke (SelectionButton '选择');$menu=Wait {$e=Find 'DataSelectAll';if($e.Count -ge 1){,$e}} 'Chinese selection menu';Assert ($menu.Count -eq 1 -and $menu[0].Current.Name -eq '选择全部筛选结果') "Chinese menu: $($menu.Count) items named '$($menu[0].Current.Name)'";[System.Windows.Forms.SendKeys]::SendWait('{ESC}')

    $ids=@($footerIds|ForEach-Object{(One $_).Current.AutomationId})
    @{ids=$ids;english=$english;dataDirectory=$dir}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $OutputDirectory 'footer.json') -Encoding utf8
    Q close|Out-Null;Assert ($process.WaitForExit(30000)) 'Close did not finish'
    Write-Output "PASS footer: paging buttons and availability, Go/Enter/invalid page jump, page size anchor, selection menu, automation names in two languages"
}catch{
    $state=try{State|ConvertTo-Json -Compress -Depth 3}catch{'unavailable'}
    $message="$_ | state: $state";Set-Content -LiteralPath (Join-Path $OutputDirectory 'footer-error.txt') -Value $message -Encoding utf8;throw $message
}finally{
    if($writer){$writer.Dispose()};if($reader){$reader.Dispose()};$pipe.Dispose()
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    Remove-Item Env:VAMSYS_DATA_DIR -ErrorAction SilentlyContinue;Remove-Item Env:VAMSYS_QA_PIPE -ErrorAction SilentlyContinue
}
