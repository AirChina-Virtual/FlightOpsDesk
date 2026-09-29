param([switch]$Publish, [switch]$UiVerification)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))

function Remove-PublishDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside artifacts: $full"
    }
    if (Test-Path -LiteralPath $full) {
        if ((Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to remove a linked publish directory: $full"
        }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

Push-Location $repo
try {
    # Tests reference a separately compiled Infrastructure with fault-injection support.
    & $sdk run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Behavior tests failed' }
    $qa = $UiVerification.IsPresent.ToString().ToLowerInvariant()
    $properties = @('-p:Platform=x64', '-p:RestoreLockedMode=true', "-p:UiVerification=$qa", "-p:VamSysTestSupport=$qa")
    if (!$Publish) {
        & $sdk build src/VamSys.App/VamSys.App.csproj -c Release @properties
        if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed' }
        return
    }

    # Always publish into an empty directory so old QA files and developer docs cannot leak in.
    $staging = Join-Path $artifacts ('publish-' + [Guid]::NewGuid().ToString('N'))
    & $sdk publish src/VamSys.App/VamSys.App.csproj -c Release @properties -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
    $readme = if ($UiVerification) { 'packaging/QA-README.md' } else { 'packaging/README.md' }
    Copy-Item $readme (Join-Path $staging 'README.md')
    New-Item -ItemType Directory -Force (Join-Path $staging 'docs'), (Join-Path $staging 'examples') | Out-Null
    Copy-Item docs/STORAGE-MIGRATION.md (Join-Path $staging 'docs/STORAGE-MIGRATION.md')
    $guide = [IO.File]::ReadAllText((Join-Path $repo 'docs/USER-GUIDE.md')).Replace('[性能报告与复现方式](PERFORMANCE.md)', '项目源码中的 docs/PERFORMANCE.md')
    [IO.File]::WriteAllText((Join-Path $staging 'docs/USER-GUIDE.md'), $guide, [Text.UTF8Encoding]::new($false))
    Copy-Item examples/*.csv (Join-Path $staging 'examples')
    $check = if ($UiVerification) { '--verify-qa-package' } else { '--verify-package' }
    & $sdk run --no-build --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -- $check $staging
    if ($LASTEXITCODE -ne 0) { throw 'Published package isolation check failed' }

    $name = if ($UiVerification) { 'v3-qa' } else { 'flightops-app' }
    $destination = Join-Path $artifacts $name
    $previous = Join-Path $artifacts ($name + '-previous-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $destination) {
        if ((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Publish destination is a link' }
        Move-Item -LiteralPath $destination -Destination $previous
    }
    try { Move-Item -LiteralPath $staging -Destination $destination }
    catch {
        if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $destination }
        throw
    }
    Remove-PublishDirectory $previous
    $files = Get-ChildItem -LiteralPath $destination -File -Recurse
    Write-Output ("Published {0}: {1} files, {2:N0} bytes" -f $destination, $files.Count, ($files | Measure-Object Length -Sum).Sum)
} finally { Pop-Location }
