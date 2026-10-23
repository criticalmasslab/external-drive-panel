param([switch]$StopRunning, [switch]$Restart)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src\ExternalDrivePanel\ExternalDrivePanel.csproj'
$version = ((dotnet msbuild $project -getProperty:Version) | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid project release version.' }
$output = [IO.Path]::GetFullPath((Join-Path $repo 'src\ExternalDrivePanel\bin\publish-latest'))
$executable = Join-Path $output 'ExternalDrivePanel.exe'
$running = @(Get-Process ExternalDrivePanel -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $executable })
if ($running.Count -gt 0 -and !$StopRunning) { throw 'App is running. Exit through the tray first, or pass -StopRunning for a disposable test instance.' }
if ($running.Count -gt 0) { $running | Stop-Process } # Explicit test-only opt-in; normal exit waits for writers.
dotnet run --project (Join-Path $repo 'tools\IconGenerator') -c Release -- (Join-Path $repo 'src\ExternalDrivePanel\Assets\App.ico')
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
dotnet publish $project -p:PublishProfile=Portable
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
$runtimeConfig = Get-Content -LiteralPath (Join-Path $repo 'src\ExternalDrivePanel\bin\Release\net10.0-windows10.0.17763.0\win-x64\ExternalDrivePanel.runtimeconfig.json') -Raw | ConvertFrom-Json
if (@($runtimeConfig.runtimeOptions.includedFrameworks).Count -ne 2 -or
    @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.version -ne '10.0.13' }).Count -gt 0) {
    throw 'Published runtime patch does not match the validated 10.0.13 runtime packs.'
}
# Remove only this build's obsolete debug artifact, not the output directory.
$symbols = Join-Path $output 'ExternalDrivePanel.pdb'
if (Test-Path -LiteralPath $symbols) { Remove-Item -LiteralPath $symbols }
$packages = ((dotnet msbuild $project -getProperty:NuGetPackageRoot) | Out-String).Trim()
$licenses = Join-Path $output 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
foreach ($pack in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    $packRoot = Join-Path $packages "$pack\10.0.13"
    if (!(Test-Path -LiteralPath $packRoot)) { throw "Missing runtime notices: $packRoot" }
    foreach ($name in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        $source = Join-Path $packRoot $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $licenses "$pack-$name") -Force }
    }
}
$distribution = Join-Path $repo 'dist'
New-Item -ItemType Directory -Path $distribution -Force | Out-Null
$archive = Join-Path $distribution "ExternalDrivePanel-$version-win-x64.zip"
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -CompressionLevel Optimal -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive + '.sha256') -Value ($hash + '  ' + [IO.Path]::GetFileName($archive)) -Encoding ascii
Write-Host ("Portable ZIP: {0} ({1:N2} MiB)" -f $archive, ((Get-Item -LiteralPath $archive).Length / 1MB))
if ($Restart) { Start-Process -FilePath $executable -WindowStyle Normal }
