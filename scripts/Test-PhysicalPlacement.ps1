param([Parameter(Mandatory = $true)][string]$ExePath)
$ErrorActionPreference = 'Stop'

# Read native rectangles in the same physical-pixel coordinate space as the app.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class PlacementProbe {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Info { public int Size; public Rect Monitor, Work; public uint Flags; }
    public delegate bool MonitorProc(IntPtr h, IntPtr dc, IntPtr rect, IntPtr data);
    public delegate bool WindowProc(IntPtr h, IntPtr data);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr h, ref Info info);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    public static Info[] Monitors() {
        var result = new List<Info>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, dc, r, d) => {
            var info = new Info { Size = Marshal.SizeOf(typeof(Info)) };
            if (!GetMonitorInfoW(h, ref info)) throw new Exception("GetMonitorInfo failed");
            result.Add(info); return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
    public static IntPtr[] Windows(uint processId) {
        var result = new List<IntPtr>();
        EnumWindows((h, d) => { uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == processId && IsWindowVisible(h)) result.Add(h); return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
    public static Rect Bounds(IntPtr h) {
        Rect rect; if (!GetWindowRect(h, out rect)) throw new Exception("GetWindowRect failed"); return rect;
    }
}
'@

$previousContext = [PlacementProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))
$testProcess = $null
try {
    $monitors = @([PlacementProbe]::Monitors())
    $primary = @($monitors | Where-Object { ($_.Flags -band 1) -ne 0 })
    if ($primary.Count -ne 1) { throw 'Expected exactly one primary monitor.' }
    $monitors | ForEach-Object {
        Write-Output "Monitor primary=$(($_.Flags -band 1) -ne 0) physical=[$($_.Monitor.Left),$($_.Monitor.Top),$($_.Monitor.Right),$($_.Monitor.Bottom)] work=[$($_.Work.Left),$($_.Work.Top),$($_.Work.Right),$($_.Work.Bottom)]"
    }
    $testProcess = Start-Process -FilePath (Resolve-Path -LiteralPath $ExePath).Path -PassThru
    Start-Sleep -Seconds 4
    if ($testProcess.HasExited) { throw 'Published app exited during startup.' }
    $windows = @([PlacementProbe]::Windows($testProcess.Id))
    if ($windows.Count -eq 0) { throw 'No visible app windows found.' }
    $work = $primary[0].Work
    $expectedLeft = [int][Math]::Round($work.Left + ($work.Right - $work.Left) * 0.75)
    $topBarFound = $false
    foreach ($window in $windows) {
        $rect = [PlacementProbe]::Bounds($window)
        Write-Output "Window physical=[$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom)] dpi=$([PlacementProbe]::GetDpiForWindow($window))"
        if ($rect.Left -ne $expectedLeft -or $rect.Right -ne $work.Right) {
            throw 'Window is not aligned to the rightmost quarter of the primary work area.'
        }
        if ($rect.Top -lt $work.Top -or $rect.Bottom -gt $work.Bottom) { throw 'Window extends outside the primary work area.' }
        if ($rect.Top -eq $work.Top -and $rect.Bottom -eq ($work.Top + 32)) { $topBarFound = $true }
    }
    if (!$topBarFound) { throw 'Top bar rectangle not found.' }
    Write-Output "PASS: $($windows.Count) visible windows in the primary rightmost quarter; $($monitors.Count) monitors connected."
}
finally {
    if ($testProcess -and !$testProcess.HasExited) { Stop-Process -Id $testProcess.Id }
    [void][PlacementProbe]::SetThreadDpiAwarenessContext($previousContext)
}
