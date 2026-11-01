param([switch]$RegisterStartup)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src\ExternalDrivePanel\ExternalDrivePanel.csproj'
$output = Join-Path $repo 'src\ExternalDrivePanel\bin\publish-development'
$executable = Join-Path $output 'ExternalDrivePanel.exe'

# Compile before asking an existing development instance to exit.
dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Development build failed.' }
$running = @(Get-Process ExternalDrivePanel -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $executable })
if ($running.Count -gt 0) {
    Start-Process -FilePath $executable -ArgumentList '--development-exit' -WindowStyle Hidden -Wait
    foreach ($process in $running) {
        if (!$process.WaitForExit(60000)) { throw 'Development app is still busy. Finish its operation or close its dialog, then retry.' }
    }
}
dotnet publish $project -p:PublishProfile=Portable "-p:PublishDir=$output\"
if ($LASTEXITCODE -ne 0) { throw 'Development publish failed.' }
$developmentConfig = Join-Path $env:APPDATA 'ExternalDeskPanel-Development\config.json'
$releaseConfig = Join-Path $env:APPDATA 'ExternalDeskPanel\config.json'
if (!(Test-Path -LiteralPath $developmentConfig) -and (Test-Path -LiteralPath $releaseConfig)) {
    New-Item -ItemType Directory -Path (Split-Path $developmentConfig) -Force | Out-Null
    Copy-Item -LiteralPath $releaseConfig -Destination $developmentConfig
}
if ($RegisterStartup) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $valueName = 'ExternalDrivePanel'
    $backupPath = Join-Path (Split-Path $developmentConfig) 'startup-registration-backup.json'
    if (!(Test-Path -LiteralPath $backupPath)) {
        New-Item -ItemType Directory -Path (Split-Path $backupPath) -Force | Out-Null
        $previous = Get-ItemPropertyValue -LiteralPath $runKey -Name $valueName -ErrorAction SilentlyContinue
        @{ valueName = $valueName; previousCommand = $previous } |
            ConvertTo-Json | Set-Content -LiteralPath $backupPath -Encoding UTF8
    }
    New-Item -Path $runKey -Force | Out-Null
    $launcher = Join-Path $PSScriptRoot 'Start-DevelopmentSession.ps1'
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $command = '"{0}" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{1}"' -f $powershell, $launcher
    New-ItemProperty -LiteralPath $runKey -Name 'ExternalDrivePanelDevelopment' -PropertyType String -Value $command -Force | Out-Null
    Remove-ItemProperty -LiteralPath $runKey -Name $valueName -ErrorAction SilentlyContinue
    Write-Host 'Windows startup now launches the development app instead of the installed app.'
}
& (Join-Path $PSScriptRoot 'Start-DevelopmentSession.ps1')
$shortcutPath = Join-Path $repo 'dist\ExternalDrivePanel-Development.lnk'
New-Item -ItemType Directory -Path (Split-Path $shortcutPath) -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f (Join-Path $PSScriptRoot 'Start-DevelopmentSession.ps1')
$shortcut.IconLocation = "$executable,0"
$shortcut.WorkingDirectory = $output
$shortcut.Description = 'External Drive Panel - 개발용 (별도 설정)'
$shortcut.Save()
Write-Host "Development app: $executable"
Write-Host "Separate config: $developmentConfig"
Write-Host "Launch shortcut: $shortcutPath"
