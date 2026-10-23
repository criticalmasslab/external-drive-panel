using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ExternalDrivePanel;

/// <summary>Captures only a five-physical-pixel edge band; item selection and DnD keep their normal input.</summary>
internal sealed class LayoutResizeController(PanelLayoutState layout, Action apply, Func<Point?>? readCursor = null)
{
    private LayoutResizeGesture? _gesture;
    private Window? _captureOwner;
    private Point? _lastPoint;
    private readonly Func<Point?> _readCursor = readCursor ?? ReadCursor;
    private readonly HashSet<Window> _windows = [];
    private Window[] _interactiveWindows = [];
    // Sample input before layout/render so all affected windows can settle together.
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Input)
        { Interval = TimeSpan.FromMilliseconds(16) };

    public void Attach(Window window)
    {
        if (!_windows.Add(window)) return;
        // Use the tunneling mouse events, before ListBox/DataGrid re-raise and consume
        // the left-button convenience events for selection/marquee capture.
        window.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Move), true);
        window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Down), true);
        window.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Up), true);
        window.LostMouseCapture += LostCapture;
        window.MouseLeave += Leave;
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (_captureOwner == window) Cancel();
            window.RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Move));
            window.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Down));
            window.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Up));
            window.LostMouseCapture -= LostCapture; window.MouseLeave -= Leave;
        };
    }

    public void Cancel()
    {
        var owner = _captureOwner; _captureOwner = null; _gesture = null;
        _lastPoint = null; _timer.Stop(); _timer.Tick -= Tick;
        var windows = _interactiveWindows; _interactiveWindows = [];
        foreach (var window in windows) NotifySizing(window, 0x232); // WM_EXITSIZEMOVE
        if (owner is not null) { owner.Cursor = null; if (owner.IsMouseCaptured) owner.ReleaseMouseCapture(); }
    }

    private void Down(object sender, MouseButtonEventArgs e)
    {
        var window = (Window)sender;
        if (e.ChangedButton != MouseButton.Left) return;
        // Use the event's point, not the live cursor: queued input may be processed
        // after a fast drag has already moved the cursor away from the edge.
        var point = window.PointToScreen(e.GetPosition(window));
        if (layout.HitTest(point.X, point.Y) is not { } hit) return;
        if (Start(window, hit, point)) e.Handled = true;
    }

    private bool Start(Window window, LayoutResizeHit hit, Point point)
    {
        Cancel();
        if (!window.CaptureMouse()) return false;
        _captureOwner = window; _gesture = layout.Begin(hit, point.X, point.Y);
        _lastPoint = point;
        BeginInteractiveResize();
        _timer.Tick += Tick; _timer.Start();
        return true;
    }

    private void Move(object sender, MouseEventArgs e)
    {
        var window = (Window)sender;
        if (_gesture is not null)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { Complete(); return; }
            // Do not cache a client-relative input point: the captured window
            // itself moves when its top/left edge or the preceding divider moves.
            // Converting queued WPF input through that new origin feeds the
            // window movement back into the next resize (visible oscillation).
            e.Handled = true;
        }
        else if (e.LeftButton != MouseButtonState.Pressed)
        {
            var point = window.PointToScreen(e.GetPosition(window));
            window.Cursor = CursorFor(layout.HitTest(point.X, point.Y));
        }
    }

    private void Tick(object? sender, EventArgs e) => ApplyCursorPosition();

    private void ApplyCursorPosition()
    {
        // GetCursorPos is in physical desktop pixels in our per-monitor-aware
        // process, independent of which panel currently owns mouse capture.
        if (_gesture is null || _readCursor() is not { } point || point == _lastPoint) return;
        _lastPoint = point;
        layout.Drag(_gesture, point.X, point.Y); apply();
    }

    private void Complete()
    {
        try { ApplyCursorPosition(); }
        finally { Cancel(); }
    }

    private void BeginInteractiveResize()
    {
        // SetWindowPos alone is treated as a programmatic resize. WPF only
        // synchronizes the new HWND bounds with the composition frame during
        // user sizing (HwndTarget._userInputResize / CompleteRender). Notify ALL
        // affected HWNDs, not just the captured one, so divider neighbors also
        // use that path. Always balance these notifications in Cancel().
        _interactiveWindows = _windows.Where(window => window.IsVisible).ToArray();
        foreach (var window in _interactiveWindows) NotifySizing(window, 0x231); // WM_ENTERSIZEMOVE
    }

    private static void NotifySizing(Window window, uint message)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) SendMessageW(handle, message, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static Point? ReadCursor() => GetCursorPos(out var point) ? new Point(point.X, point.Y) : null;
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);

    private Cursor? CursorFor(LayoutResizeHit? hit)
    {
        if (hit is null) return null;
        if (hit.Boundary >= 0) return layout.Direction == SplitDirection.Vertical ? Cursors.SizeNS : Cursors.SizeWE;
        var horizontal = (hit.Edges & (ResizeEdges.Left | ResizeEdges.Right)) != 0;
        var vertical = (hit.Edges & (ResizeEdges.Top | ResizeEdges.Bottom)) != 0;
        if (horizontal && vertical) return hit.Edges == (ResizeEdges.Left | ResizeEdges.Top)
            || hit.Edges == (ResizeEdges.Right | ResizeEdges.Bottom) ? Cursors.SizeNWSE : Cursors.SizeNESW;
        return horizontal ? Cursors.SizeWE : Cursors.SizeNS;
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (_gesture is null || e.ChangedButton != MouseButton.Left) return;
        Complete(); e.Handled = true;
    }
    private void LostCapture(object sender, MouseEventArgs e) { if (sender == _captureOwner) Cancel(); }
    private void Leave(object sender, MouseEventArgs e) { if (_gesture is null) ((Window)sender).Cursor = null; }
}
