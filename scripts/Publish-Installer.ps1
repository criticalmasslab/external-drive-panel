param([string]$Version = '', [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src\ExternalDrivePanel\ExternalDrivePanel.csproj'
$projectVersion = ((dotnet msbuild $project -getProperty:Version) | Out-String).Trim()
if (!$Version) { $Version = $projectVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be a stable major.minor.patch version.' }
if ($LASTEXITCODE -ne 0 -or $projectVersion -ne $Version) { throw "Project version ($projectVersion) must match release version ($Version)." }
Push-Location $repo
try {
    if (!$SkipPublish) { & (Join-Path $PSScriptRoot 'Publish-Portable.ps1') }
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Velopack restore failed.' }
    $output = Join-Path $repo "dist\releases\$Version"
    dotnet tool run vpk -- pack --packId ExternalDrivePanel --packVersion $Version --runtime win-x64 `
        --packDir (Join-Path $repo 'src\ExternalDrivePanel\bin\publish-latest') `
        --mainExe ExternalDrivePanel.exe --packTitle 'External Drive Panel' --packAuthors criticalmasslab `
        --icon (Join-Path $repo 'src\ExternalDrivePanel\Assets\App.ico') `
        --shortcuts 'Desktop,StartMenuRoot' --delta None --outputDir $output
    if ($LASTEXITCODE -ne 0) { throw 'Installer packaging failed.' }
    $assets = @(Get-ChildItem -LiteralPath $output -File | Where-Object { $_.Extension -in @('.exe', '.nupkg', '.json') -or $_.Name -like 'RELEASES*' })
    $lines = $assets | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name }
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Value $lines -Encoding ascii
    Write-Host "Installer and update feed: $output"
} finally { Pop-Location }
