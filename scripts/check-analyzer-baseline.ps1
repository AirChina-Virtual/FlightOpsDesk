<#
 Rebuilds the solution (Release, locked restore, clean) and fails when any (project, rule) warning count
 is higher than docs/analyzer-baseline.txt. Use -Update to rewrite the baseline from the current build.
 Baseline format: "<project>|<rule>|<count>" per line, '#' lines are comments.
#>
[CmdletBinding()]
param([switch]$Update)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$baselinePath = Join-Path $root 'docs/analyzer-baseline.txt'
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$log = & $dotnet build (Join-Path $root 'VamSys.slnx') -c Release --no-incremental '-p:RestoreLockedMode=true' -nologo -v:m '-clp:NoSummary' 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) { $log | Select-Object -Last 40 | Write-Host; throw 'Build failed.' }

# One warning is often printed once per target framework / project instance; count each distinct location once.
$rx = '^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\): warning (?<rule>[A-Za-z]+\d+): .*\[(?<proj>[^\]]+\.csproj)\]\s*$'
$seen = [System.Collections.Generic.HashSet[string]]::new()
$counts = @{}
foreach ($l in $log) {
    $m = [regex]::Match($l, $rx)
    if (-not $m.Success) { continue }
    $proj = $m.Groups['proj'].Value.Replace($root, '').TrimStart('\', '/').Replace('\', '/')
    $file = $m.Groups['file'].Value.Replace($root, '').TrimStart('\', '/').Replace('\', '/')
    $key = "$proj|$file|$($m.Groups['line'].Value)|$($m.Groups['col'].Value)|$($m.Groups['rule'].Value)"
    if (-not $seen.Add($key)) { continue }
    $ck = "$proj|$($m.Groups['rule'].Value)"
    $counts[$ck] = 1 + [int]$counts[$ck]
}

if ($Update) {
    $lines = @('# Analyzer warning baseline: <project>|<rule>|<count>. Regenerate with scripts/check-analyzer-baseline.ps1 -Update.')
    $lines += $counts.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)|$($_.Value)" }
    [IO.File]::WriteAllLines($baselinePath, $lines)
    Write-Host "Baseline written: $($counts.Count) entries, $(($counts.Values | Measure-Object -Sum).Sum) warnings."
    return
}

$baseline = @{}
foreach ($l in Get-Content $baselinePath) {
    if ($l -match '^\s*#' -or [string]::IsNullOrWhiteSpace($l)) { continue }
    $p = $l.Split('|')
    $baseline["$($p[0])|$($p[1])"] = [int]$p[2]
}
$failed = $false
foreach ($e in $counts.GetEnumerator() | Sort-Object Name) {
    $allowed = [int]$baseline[$e.Name]
    if ($e.Value -gt $allowed) {
        Write-Host "NEW WARNINGS: $($e.Name) now $($e.Value), baseline $allowed" -ForegroundColor Red
        $failed = $true
    }
}
if ($failed) { exit 1 }
Write-Host "Analyzer warnings within baseline ($(($counts.Values | Measure-Object -Sum).Sum) current, $(($baseline.Values | Measure-Object -Sum).Sum) allowed)."