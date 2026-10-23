using System.Windows;
using System.Windows.Interop;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ExternalDrivePanel;

public static class DialogActivation
{
    private sealed class State { public int Count; public bool OriginalTopmost; }
    private static readonly ConditionalWeakTable<Window, State> States = new();
    private sealed class Lease : IDisposable
    {
        private readonly Window _owner; private readonly State _state; private bool _disposed, _closed;
        public Lease(Window owner)
        {
            _owner = owner; _state = States.GetValue(owner, _ => new State());
            owner.Closed += OnClosed;
            if (_state.Count++ == 0) _state.OriginalTopmost = owner.Topmost;
            owner.Topmost = true; owner.Activate();
            var handle = new WindowInteropHelper(owner).Handle;
            if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            _owner.Closed -= OnClosed;
            if (--_state.Count == 0 && !_closed) _owner.Topmost = _state.OriginalTopmost;
        }
        private void OnClosed(object? sender, EventArgs e) => _closed = true;
    }
    public static T Show<T>(Window owner, Func<T> showDialog)
    {
        using var lease = new Lease(owner); return showDialog();
    }
    public static async Task<T> ShowAsync<T>(Window owner, Func<Task<T>> action)
    {
        // Bring the initiating panel forward once, but never pin it above other apps
        // for the duration of a copy/property operation. Native dialogs have their own scope.
        owner.Activate();
        var handle = new WindowInteropHelper(owner).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        return await action();
    }
    public static async Task<T> ShowAsync<T>(IntPtr owner, Func<Task<T>> action)
    {
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess()) return await await app.Dispatcher.InvokeAsync(() => ShowAsync(owner, action));
        var window = owner == IntPtr.Zero ? null : app?.Windows.OfType<Window>().FirstOrDefault(window => new WindowInteropHelper(window).Handle == owner);
        return window is null ? await action() : await ShowAsync(window, action);
    }
    public static void Show(IntPtr owner, Action action)
    {
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(() => Show(owner, action)); return; }
        var window = owner == IntPtr.Zero ? null : app?.Windows.OfType<Window>().FirstOrDefault(window => new WindowInteropHelper(window).Handle == owner);
        if (window is null) action(); else Show(window, () => { action(); return true; });
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
}
