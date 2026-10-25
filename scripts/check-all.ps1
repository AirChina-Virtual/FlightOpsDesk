<#
 One entry point for the checks every change must pass, with a one-line summary per step and a final verdict.

 -Mode Quick (default, run after each commit): Release build of the whole solution (App included), then all
   unit tests and all integration tests (goldens, inventories, storage, transport) without rebuilding.
 -Mode Full (run once before handing work back): Quick, plus the same tests in Debug (row and guard checks),
   the legacy runner, and the analyzer baseline.

 Works from the main checkout and from worktrees: dotnet comes from -Dotnet, then $env:VAMSYS_DOTNET, then
 .tools/dotnet in this checkout, then .tools/dotnet in a sibling "vamsys" checkout, then PATH.
 Exit code 0 only when every step passed. Logs of each step go to artifacts/check-all/<mode>-<step>.log.
 Do not add -nologo to the test commands: the xUnit v3 runner then reports "Zero tests ran".
#>
[CmdletBinding()]
param([ValidateSet('Quick', 'Full')][string]$Mode = 'Quick', [string]$Dotnet)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$candidates = @($Dotnet, $env:VAMSYS_DOTNET, (Join-Path $root '.tools/dotnet/dotnet.exe'),
    (Join-Path (Split-Path -Parent $root) 'vamsys/.tools/dotnet/dotnet.exe')) | Where-Object { $_ }
$dn = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dn) { $dn = 'dotnet' }
# The analyzer script looks for dotnet on PATH when the checkout has no .tools folder.
if ($dn -ne 'dotnet') { $env:PATH = (Split-Path -Parent $dn) + [IO.Path]::PathSeparator + $env:PATH }

$logs = Join-Path $root 'artifacts/check-all'
New-Item -ItemType Directory -Force $logs | Out-Null
$results = [Collections.Generic.List[object]]::new()
$watch = [Diagnostics.Stopwatch]::StartNew()

function Step([string]$Name, [scriptblock]$Run, [scriptblock]$Summary)
{
    $log = Join-Path $logs "$($Mode.ToLowerInvariant())-$Name.log"
    $started = $watch.Elapsed
    $output = & $Run 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $output | Set-Content $log
    $text = & $Summary $output
    $seconds = ($watch.Elapsed - $started).TotalSeconds
    $ok = $code -eq 0
    $results.Add([pscustomobject]@{ Step = $Name; Ok = $ok; Text = $text })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0} {1,-22} {2} ({3:0} s)" -f $mark, $Name, $text, $seconds)
    if (-not $ok) { Write-Host "     log: $log" }
    return $ok
}

function TestSummary($output)
{
    $counts = foreach ($label in 'succeeded', 'failed', 'skipped')
    {
        $line = $output | Where-Object { $_ -match "^\s*$label\s*:\s*\d+" } | Select-Object -Last 1
        if ($line) { "$label $([regex]::Match($line, '\d+').Value)" }
    }
    if ($counts) { $counts -join ', ' } else { 'no test summary found' }
}

function LastMatch($output, [string]$Pattern, [string]$Fallback)
{
    $line = $output | Where-Object { $_ -match $Pattern } | Select-Object -Last 1
    if ($line) { $line.Trim() } else { $Fallback }
}

$common = @('-c', 'Release', '-p:RestoreLockedMode=true')
$unit = Join-Path $root 'tests/VamSys.UnitTests/VamSys.UnitTests.csproj'
$integration = Join-Path $root 'tests/VamSys.IntegrationTests/VamSys.IntegrationTests.csproj'

$ok = Step 'build-release' { & $dn build (Join-Path $root 'VamSys.slnx') @common -v:q } {
    param($o) LastMatch $o '\d+ Error\(s\)' 'build finished' }
if ($ok)
{
    Step 'unit-release' { & $dn test --project $unit @common --no-build } { param($o) TestSummary $o } | Out-Null
    Step 'integration-release' { & $dn test --project $integration @common --no-build } { param($o) TestSummary $o } | Out-Null
}

if ($Mode -eq 'Full' -and $ok)
{
    $debug = @('-c', 'Debug', '-p:RestoreLockedMode=true')
    Step 'unit-debug' { & $dn test --project $unit @debug } { param($o) TestSummary $o } | Out-Null
    Step 'integration-debug' { & $dn test --project $integration @debug } { param($o) TestSummary $o } | Out-Null
    Step 'legacy-runner' { & $dn run --project (Join-Path $root 'tests/VamSys.Tests/VamSys.Tests.csproj') @common } {
        param($o) LastMatch $o 'scenarios passed\.' 'no legacy summary found' } | Out-Null
    Step 'analyzer-baseline' { & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'check-analyzer-baseline.ps1') } {
        param($o) LastMatch $o 'within baseline|NEW WARNINGS' 'no analyzer summary found' } | Out-Null
}

$failed = @($results | Where-Object { -not $_.Ok })
$verdict = if ($failed.Count -eq 0) { 'ALL PASSED' } else { "FAILED: $(($failed.Step) -join ', ')" }
Write-Host ("check-all {0}: {1} in {2:0} s" -f $Mode, $verdict, $watch.Elapsed.TotalSeconds)
if ($failed.Count -gt 0) { exit 1 }
