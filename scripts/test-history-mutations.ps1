param()
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$root=Join-Path $repo ('artifacts/history-mutations-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
Push-Location $repo
try {$files=@(& rg --files src tests docs/contracts)+@('Directory.Build.props','global.json')}
finally {Pop-Location}
foreach($name in @('missing-after','missing-redo-delete')) {
    $dir=Join-Path $root $name
    foreach($relative in $files){
        if($relative -match '(^|[\\/])(bin|obj)([\\/]|$)'){continue}
        $target=Join-Path $dir $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)|Out-Null
        Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $target
    }
    if($name -eq 'missing-after'){
        $path=Join-Path $dir 'src/VamSys.Core/EditHistory.cs'
        $old='if(undo.Count>0)undo[^1]=Change(undo[^1],true);'
        $new='// MUTATION: omit same-step After update'
    }else{
        $path=Join-Path $dir 'src/VamSys.Infrastructure/WorkspaceStore.History.cs'
        $old='foreach(var step in h.Deletes)Execute(c,tx,"DELETE FROM resource_history_steps WHERE workspace_id=$w AND resource=$r AND step_id=$id",[..args,("$id",step.ToString())]);'
        $new='// MUTATION: retain discarded redo and evicted steps'
    }
    $source=[IO.File]::ReadAllText($path)
    if(!$source.Contains($old)){throw "Mutation anchor missing: $name"}
    [IO.File]::WriteAllText($path,$source.Replace($old,$new))
    Push-Location $dir
    try{
        & "$repo/.tools/dotnet/dotnet.exe" run --project tests/VamSys.Tests -c Release -p:RestoreLockedMode=true -- --history *> (Join-Path $root "$name.log")
        $code=$LASTEXITCODE
    }finally{Pop-Location}
    $log=[IO.File]::ReadAllText((Join-Path $root "$name.log"))
    if($code -eq 0 -or $log.Contains('The build failed.') -or !$log.Contains('Unhandled exception.')){throw "Mutation did not produce a behavior failure: $name"}
    Write-Output "PASS rejected $name; log: $root/$name.log"
}
# Keep isolated sources and logs for review; neither source tree nor published package was changed.
