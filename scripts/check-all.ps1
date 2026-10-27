<#
 One entry point for the checks every change must pass, with a one-line summary per step and a final verdict.

 -Mode Quick (default, run after each commit): Release build of the whole solution (App included), then all
   unit tests and the integration tests (goldens, inventories, storage, transport) except those marked
   [Trait("Category", "Slow")], without rebuilding.
 -Mode Full (run once before handing work back): Quick, plus the same tests in Debug (row and guard checks),
   and the analyzer baseline. (The legacy runner has no scenarios left; it only provides command-line entries.)

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

$ok = Step 'build-release' { & $dn build (Join-Path $root 'VamSys.slnx') @common -v:m } {
    param($o) LastMatch $o '\d+ Error\(s\)' 'build finished' }

# Quick warning gate: projects rebuilt by the build above report all their warnings, so a count above
# docs/analyzer-baseline.txt is already visible here. Full mode re-checks everything with a clean rebuild.
if ($ok)
{
    Step 'new-warnings' {
        $log = Get-Content (Join-Path $logs "$($Mode.ToLowerInvariant())-build-release.log")
        $rx = '^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\): warning (?<rule>[A-Za-z]+\d+): .*\[(?<proj>[^\]]+\.csproj)\]\s*$'
        $seen = [Collections.Generic.HashSet[string]]::new(); $counts = @{}
        foreach ($l in $log)
        {
            $m = [regex]::Match($l, $rx)
            if (-not $m.Success) { continue }
            $proj = $m.Groups['proj'].Value.Replace($root, '').TrimStart('\', '/').Replace('\', '/')
            if (-not $seen.Add("$proj|$($m.Groups['file'].Value)|$($m.Groups['line'].Value)|$($m.Groups['col'].Value)|$($m.Groups['rule'].Value)")) { continue }
            $key = "$proj|$($m.Groups['rule'].Value)"; $counts[$key] = 1 + [int]$counts[$key]
        }
        $baseline = @{}
        foreach ($l in Get-Content (Join-Path $root 'docs/analyzer-baseline.txt'))
        { if ($l -notmatch '^\s*#' -and $l.Trim()) { $p = $l.Split('|'); $baseline["$($p[0])|$($p[1])"] = [int]$p[2] } }
        $over = @($counts.GetEnumerator() | Where-Object { $_.Value -gt [int]$baseline[$_.Name] } | ForEach-Object { "$($_.Name) $($_.Value)>$([int]$baseline[$_.Name])" })
        if ($over.Count -gt 0) { "NEW WARNINGS: $($over -join '; ')"; $global:LASTEXITCODE = 1 } else { 'none above baseline'; $global:LASTEXITCODE = 0 }
    } { param($o) ($o | Select-Object -Last 1) } | Out-Null
}
if ($ok)
{
    Step 'unit-release' { & $dn test --project $unit @common --no-build } { param($o) TestSummary $o } | Out-Null
    # Quick mode skips tests marked [Trait("Category", "Slow")]; Full mode runs them in both configurations.
    $skipSlow = if ($Mode -eq 'Quick') { @('--', '--filter-not-trait', 'Category=Slow') } else { @() }
    Step 'integration-release' { & $dn test --project $integration @common --no-build @skipSlow } { param($o) TestSummary $o } | Out-Null
}

if ($Mode -eq 'Full' -and $ok)
{
    $debug = @('-c', 'Debug', '-p:RestoreLockedMode=true')
    Step 'unit-debug' { & $dn test --project $unit @debug } { param($o) TestSummary $o } | Out-Null
    Step 'integration-debug' { & $dn test --project $integration @debug } { param($o) TestSummary $o } | Out-Null
    Step 'analyzer-baseline' { & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'check-analyzer-baseline.ps1') } {
        param($o)
        # Show every rule over its baseline, not only the last one printed.
        $new = @($o | Where-Object { $_ -match 'NEW WARNINGS' } | ForEach-Object { $_.Replace('NEW WARNINGS: ', '').Trim() })
        if ($new.Count -gt 0) { 'NEW WARNINGS: ' + ($new -join '; ') } else { LastMatch $o 'within baseline' 'no analyzer summary found' } } | Out-Null
}

$failed = @($results | Where-Object { -not $_.Ok })
$verdict = if ($failed.Count -eq 0) { 'ALL PASSED' } else { "FAILED: $(($failed.Step) -join ', ')" }
Write-Host ("check-all {0}: {1} in {2:0} s" -f $Mode, $verdict, $watch.Elapsed.TotalSeconds)
if ($failed.Count -gt 0) { exit 1 }
