param([string]$Name, [ValidateSet('Tree','Invoke','Select','Set','Screenshot')][string]$Action='Tree', [string]$Value, [string]$OutputPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
$process = Get-Process VamSys.App | Select-Object -First 1
if (!$process) { throw 'Start VamSys.App first' }
$root=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$scope=[System.Windows.Automation.TreeScope]::Descendants
if ($Action -eq 'Tree') {
    $root.FindAll($scope,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
        if ($_.Current.Name) { '{0} | {1} | {2}' -f $_.Current.ControlType.ProgrammaticName,$_.Current.AutomationId,$_.Current.Name }
    }
    exit
}
if ($Action -eq 'Screenshot') {
    Add-Type -AssemblyName System.Drawing
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowCapture {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd,out RECT rect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd,IntPtr hdc,uint flags);
}
'@
    $rect=New-Object WindowCapture+RECT
    [WindowCapture]::GetWindowRect($process.MainWindowHandle,[ref]$rect) | Out-Null
    $bitmap=New-Object System.Drawing.Bitmap(($rect.Right-$rect.Left),($rect.Bottom-$rect.Top))
    $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
    $dc=$graphics.GetHdc()
    try { if (![WindowCapture]::PrintWindow($process.MainWindowHandle,$dc,2)) { throw 'Window capture failed' } }
    finally { $graphics.ReleaseHdc($dc) }
    $bitmap.Save($OutputPath,[System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose(); exit
}
$elements=$root.FindAll($scope,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$Name)))
$target=$elements | Where-Object {
    if($Action -eq 'Set') { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit }
    elseif($Action -eq 'Invoke') { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button }
    else { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem }
} | Select-Object -First 1
if (!$target) { throw "Control not found: $Name" }
switch ($Action) {
    'Invoke' { ([System.Windows.Automation.InvokePattern]$target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
    'Select' { ([System.Windows.Automation.SelectionItemPattern]$target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
    'Set' { $target.SetFocus(); ([System.Windows.Automation.ValuePattern]$target.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Value) }
}
