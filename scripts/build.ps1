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
        & $sdk publish src/VamSys.App/VamSys.App.csproj -c Release -p:Platform=x64 -p:RestoreLockedMode=true -o artifacts/app
    } else {
        & $sdk build src/VamSys.App/VamSys.App.csproj -c Release -p:Platform=x64 -p:RestoreLockedMode=true
    }
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed' }
    if ($Publish -and !(Test-Path 'artifacts/app/MainWindow.xbf')) { throw 'Published XAML resources missing' }
    if ($Publish) {
        Copy-Item README.md artifacts/app/README.md -Force
        New-Item -ItemType Directory -Force artifacts/app/docs,artifacts/app/examples | Out-Null
        Copy-Item docs/* artifacts/app/docs -Recurse -Force
        Copy-Item examples/* artifacts/app/examples -Recurse -Force
    }
} finally { Pop-Location }
