$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$executable = Join-Path $repo 'src\ExternalDrivePanel\bin\publish-development\ExternalDrivePanel.exe'
if (!(Test-Path -LiteralPath $executable)) { throw 'Build the development app with Start-Development.ps1 first.' }

# Close only this user's installed copy, using normal Windows session shutdown
# requests. No force flag: the app may veto while file operations are running.
$installed = Join-Path $env:LOCALAPPDATA 'ExternalDrivePanel\current\ExternalDrivePanel.exe'
$release = @(Get-Process ExternalDrivePanel -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $installed })
if ($release.Count -gt 0) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class PanelDevelopmentShutdown {
    [StructLayout(LayoutKind.Sequential)] public struct ProcessIdentity {
        public uint Pid;
        public System.Runtime.InteropServices.ComTypes.FILETIME Started;
    }
    [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] public static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] public static extern int RmRegisterResources(uint session, uint files, string[] names, uint count, ProcessIdentity[] apps, uint services, string[] serviceNames);
    [DllImport("rstrtmgr.dll")] public static extern int RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")] public static extern int RmEndSession(uint session);
}
'@
    [uint32]$session = 0
    $result = [PanelDevelopmentShutdown]::RmStartSession([ref]$session, 0, (New-Object Text.StringBuilder 64))
    if ($result -ne 0) { throw "Cannot start normal shutdown session: $result" }
    try {
        $identities = @($release | ForEach-Object {
            $identity = New-Object PanelDevelopmentShutdown+ProcessIdentity
            $identity.Pid = $_.Id
            $time = $_.StartTime.ToFileTime()
            $identity.Started.dwLowDateTime = [BitConverter]::ToInt32([BitConverter]::GetBytes($time), 0)
            $identity.Started.dwHighDateTime = [BitConverter]::ToInt32([BitConverter]::GetBytes($time), 4)
            $identity
        })
        $result = [PanelDevelopmentShutdown]::RmRegisterResources($session, 0, $null, $identities.Count, $identities, 0, $null)
        if ($result -ne 0) { throw "Cannot register installed app for normal shutdown: $result" }
        $result = [PanelDevelopmentShutdown]::RmShutdown($session, 0, [IntPtr]::Zero)
        if ($result -ne 0) { throw "Installed app is busy or declined normal shutdown: $result" }
        foreach ($process in $release) {
            if (!$process.WaitForExit(60000)) { throw 'Installed app is still busy. Finish its file operations and try again.' }
        }
    } finally { [void][PanelDevelopmentShutdown]::RmEndSession($session) }
}
if (!(Get-Process ExternalDrivePanel -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $executable })) {
    Start-Process -FilePath $executable -ArgumentList '--development'
}
