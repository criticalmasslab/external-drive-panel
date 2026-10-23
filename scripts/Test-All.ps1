param([switch]$IncludeClipboard)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projects = Get-ChildItem -LiteralPath (Join-Path $repo 'tests') -Directory
foreach ($project in $projects) {
    Write-Host ("TEST " + $project.Name)
    if (!$IncludeClipboard -and $project.Name -in @('ItemMenuSmoke', 'WindowsShellSmoke')) {
        dotnet run --project $project.FullName -c Release -- --no-clipboard
    } else {
        dotnet run --project $project.FullName -c Release
    }
    if ($LASTEXITCODE -ne 0) { throw ("Test failed: " + $project.Name) }
}
Write-Host ("PASS: {0} smoke projects. Clipboard tests included: {1}" -f @($projects).Count, $IncludeClipboard.IsPresent)
