using System.Runtime.InteropServices;
using System.Text;

namespace ExternalDrivePanel;

/// <summary>Promotes only dialogs owned by this operation, never unrelated Explorer windows.</summary>
public sealed class NativeDialogForegroundScope : IDisposable
{
    private readonly IntPtr _owner;
    private readonly uint _thread = GetCurrentThreadId();
    private readonly WinEvent _callback;
    private readonly IntPtr _hook;
    private sealed class Pin { public int Count; public bool OriginalTopmost; }
    private static readonly object PinLock = new();
    private static readonly Dictionary<(IntPtr Window, uint Thread, uint Process), Pin> Pins = [];
    private readonly Dictionary<IntPtr, (uint Thread, uint Process)> _windows = [];
    private bool _disposed;
    public NativeDialogForegroundScope(IntPtr owner)
    {
        _owner = owner; _callback = OnShow;
        _hook = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, _callback, 0, 0, 0); // EVENT_OBJECT_SHOW, out of context
    }
    private void OnShow(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || window == IntPtr.Zero || window == _owner || objectId != 0 || childId != 0
            || GetAncestor(window, 2) != window || !IsWindowVisible(window) || _windows.ContainsKey(window)) return;
        var belongs = false;
        var parent = GetWindow(window, 4);
        for (var depth = 0; parent != IntPtr.Zero && depth < 16; parent = GetWindow(parent, 4), depth++)
            if (_owner != IntPtr.Zero && parent == _owner) { belongs = true; break; }
        if (!belongs && GetWindowThreadProcessId(window, out _) == _thread)
        {
            var name = new StringBuilder(128); GetClassNameW(window, name, name.Capacity);
            belongs = name.ToString() is "#32770" or "OperationStatusWindow";
        }
        if (!belongs) return;
        var windowThread = GetWindowThreadProcessId(window, out var process);
        _windows[window] = (windowThread, process);
        lock (PinLock)
        {
            var key = (window, windowThread, process);
            if (!Pins.TryGetValue(key, out var pin)) Pins[key] = pin = new Pin { OriginalTopmost = (GetWindowLongPtrW(window, -20).ToInt64() & 8) != 0 };
            pin.Count++;
        }
        SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x13); // TOPMOST, no move/size/activation
        SetForegroundWindow(window);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        foreach (var pair in _windows)
        {
            lock (PinLock)
            {
                var key = (pair.Key, pair.Value.Thread, pair.Value.Process);
                if (!Pins.TryGetValue(key, out var pin) || --pin.Count > 0) continue;
                Pins.Remove(key);
                if (!pin.OriginalTopmost && IsWindow(pair.Key) && GetWindowThreadProcessId(pair.Key, out var process) == pair.Value.Thread && process == pair.Value.Process)
                    SetWindowPos(pair.Key, new IntPtr(-2), 0, 0, 0, 0, 0x13);
            }
        }
        _windows.Clear(); GC.KeepAlive(_callback);
    }
    private delegate void WinEvent(IntPtr hook, uint type, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
