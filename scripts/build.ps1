param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
Push-Location $repo
try {
    & $sdk run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Behavior tests failed' }
    if ($Publish) {
        & $sdk publish src/VamSys.App/VamSys.App.csproj -c Release -p:Platform=x64 -p:RestoreLockedMode=true -o artifacts/flightops-app
    } else {
        & $sdk build src/VamSys.App/VamSys.App.csproj -c Release -p:Platform=x64 -p:RestoreLockedMode=true
    }
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed' }
    if ($Publish -and !(Test-Path 'artifacts/flightops-app/MainWindow.xbf')) { throw 'Published XAML resources missing' }
    if ($Publish) {
        [IO.File]::WriteAllText((Join-Path $repo 'artifacts/flightops-app/README.md'), [IO.File]::ReadAllText((Join-Path $repo 'README.md')).Replace('src/VamSys.App/Assets/FlightOpsDesk.png','Assets/FlightOpsDesk.png'), [Text.UTF8Encoding]::new($false))
        Copy-Item CHANGELOG.md artifacts/flightops-app/CHANGELOG.md -Force
        foreach ($project in @('src/VamSys.App','src/VamSys.Core','src/VamSys.Infrastructure','tests/VamSys.Tests')) {
            $destination = Join-Path 'artifacts/flightops-app' $project
            New-Item -ItemType Directory -Force $destination | Out-Null
            Copy-Item (Join-Path $project 'README.md') (Join-Path $destination 'README.md') -Force
        }
        New-Item -ItemType Directory -Force artifacts/flightops-app/docs,artifacts/flightops-app/examples | Out-Null
        Copy-Item docs/* artifacts/flightops-app/docs -Recurse -Force
        Copy-Item examples/* artifacts/flightops-app/examples -Recurse -Force
    }
} finally { Pop-Location }
