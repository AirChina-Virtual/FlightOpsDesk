param()
# Copies the sources into isolated folders, plants one known history bug in each copy and checks that the
# history tests reject it. Neither the source tree nor any package is changed; copies and logs are kept for review.
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$root=Join-Path $repo ('artifacts/history-mutations-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$dotnet=Join-Path $repo '.tools/dotnet/dotnet.exe'
if(!(Test-Path $dotnet)){$dotnet=Join-Path (Split-Path $repo -Parent) 'vamsys/.tools/dotnet/dotnet.exe'}
if(!(Test-Path $dotnet)){$dotnet='dotnet'}
$files=@(foreach($top in 'src','tests','docs/contracts'){
    Get-ChildItem (Join-Path $repo $top) -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { [IO.Path]::GetRelativePath($repo,$_.FullName) }
})+@('Directory.Build.props','global.json','VamSys.slnx')
$tests=@(
    @{Project='tests/VamSys.UnitTests/VamSys.UnitTests.csproj';Class='VamSys.UnitTests.Domain.EditHistoryTests'},
    @{Project='tests/VamSys.IntegrationTests/VamSys.IntegrationTests.csproj';Class='VamSys.IntegrationTests.Storage.StorageHistoryTests'})
foreach($name in @('missing-after','missing-redo-delete')) {
    $dir=Join-Path $root $name
    foreach($relative in $files){
        $target=Join-Path $dir $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)|Out-Null
        Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $target
    }
    if($name -eq 'missing-after'){
        $path=Join-Path $dir 'src/VamSys.Domain/EditHistory.cs'
        $old="if (undo.Count > 0)`n            undo[^1] = Change(undo[^1], true);"
        $new='// MUTATION: omit same-step After update'
    }else{
        $path=Join-Path $dir 'src/VamSys.Storage/Repositories/HistoryRepository.cs'
        $old='foreach (var step in h.Deletes)' + "`n            " + 'Execute(c, tx, "DELETE FROM resource_history_steps WHERE workspace_id=$w AND resource=$r AND step_id=$id", [.. args, ("$id", step.ToString())]);'
        $new='// MUTATION: retain discarded redo and evicted steps'
    }
    $source=[IO.File]::ReadAllText($path).Replace("`r`n","`n")
    if(!$source.Contains($old)){throw "Mutation anchor missing: $name"}
    [IO.File]::WriteAllText($path,$source.Replace($old,$new))
    $rejected=$false
    Push-Location $dir
    try{
        foreach($t in $tests){
            $log=Join-Path $root "$name-$([IO.Path]::GetFileNameWithoutExtension($t.Project)).log"
            # A compile error is not a behaviour failure: only failed test cases count.
            & $dotnet test --project $t.Project -c Release -p:RestoreLockedMode=true -- --filter-class $t.Class *> $log
            $text=[IO.File]::ReadAllText($log)
            if($text -match 'error CS\d+'){throw "Mutation build failed: $name (see $log)"}
            if($text -match 'failed:\s*([1-9]\d*)'){$rejected=$true; Write-Output "  $name rejected by $($t.Class): $($Matches[1]) failed"}
        }
    }finally{Pop-Location}
    if(!$rejected){throw "Mutation did not produce a behavior failure: $name"}
    Write-Output "PASS rejected $name; logs: $root"
}
