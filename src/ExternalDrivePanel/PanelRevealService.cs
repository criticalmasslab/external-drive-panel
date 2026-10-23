using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ExternalDrivePanel;

/// <summary>Small bounded native geometry poll. Never captures pixels or activates on show.</summary>
public sealed class PanelRevealService : IDisposable
{
    private readonly Func<IEnumerable<Window>> _windows;
    private readonly Func<LayoutRect> _bounds;
    private readonly Action _reveal;
    private readonly Func<bool> _suspended;
    private readonly Func<bool>? _ownForeground;
    private readonly DispatcherTimer _timer;
    private readonly RevealButtonDelay _delay = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private PanelRevealWindow? _button;
    private bool _disposed;
    public bool IsButtonVisible => _button?.IsVisible == true;

    public PanelRevealService(Func<IEnumerable<Window>> windows, Func<LayoutRect> bounds, Action reveal, Func<bool>? suspended = null, Func<bool>? ownForeground = null)
    {
        _windows = windows; _bounds = bounds; _reveal = reveal; _suspended = suspended ?? (() => false);
        _ownForeground = ownForeground;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Check(); _timer.Start();
    }
    public void Check()
    {
        if (_disposed) return;
        try
        {
            var panels = _windows().Where(window => window.IsVisible).ToArray();
            var handles = panels.Select(window => new WindowInteropHelper(window).Handle).Where(handle => handle != 0).ToHashSet();
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out var foregroundProcess);
            var buttonHandle = _button is null ? 0 : new WindowInteropHelper(_button).Handle;
            var ownForeground = _ownForeground?.Invoke() ?? (foregroundProcess == Environment.ProcessId && foreground != buttonHandle);
            if (_suspended() || handles.Count == 0 || ownForeground)
            { Hide(); return; }
            var requested = _bounds();
            var left = Math.Round(requested.X); var top = Math.Round(requested.Y);
            var group = new LayoutRect(left, top, Math.Round(requested.X + requested.Width) - left, Math.Round(requested.Y + requested.Height) - top);
            var monitors = new MonitorService().GetMonitors();
            var screens = monitors.Select(monitor => new LayoutRect(monitor.Left, monitor.Top, monitor.Width, monitor.Height)).ToArray();
            var windows = new List<OcclusionWindow>();
            var visited = new HashSet<nint>();
            var remaining = new HashSet<nint>(handles);
            var panelOnCurrentDesktop = false;
            // A bounded, visited traversal cannot spin if another app destroys or
            // rearranges windows during the snapshot. Missing windows => retry.
            var current = GetTopWindow(0);
            while (current != 0 && visited.Count < 1024 && visited.Add(current))
            {
                var isPanel = handles.Contains(current);
                if (isPanel) remaining.Remove(current);
                var visible = IsWindowVisible(current);
                var onDesktop = DwmGetWindowAttribute(current, 14, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
                if (isPanel && visible && onDesktop) panelOnCurrentDesktop = true;
                if (current != buttonHandle && visible && onDesktop && !IsIconic(current))
                {
                    if (DwmGetWindowAttributeRect(current, 9, out var rect, Marshal.SizeOf<NativeRect>()) != 0) GetWindowRect(current, out rect);
                    if (rect.Right > rect.Left && rect.Bottom > rect.Top)
                        windows.Add(new(current, new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), isPanel));
                }
                if (remaining.Count == 0) break;
                current = GetWindow(current, 2);
            }
            if (remaining.Count != 0 || screens.Length == 0 || !panelOnCurrentDesktop) { Hide(); return; }
            var covered = !PanelOcclusionPolicy.HasAccessibleEdge(group, windows, screens);
            if (!_delay.Update(covered, _clock.Elapsed)) { _button?.Hide(); return; }
            var screen = screens.OrderByDescending(area =>
            { var intersection = PanelOcclusionPolicy.Intersection(area, group); return intersection is null ? 0 : intersection.Width * intersection.Height; }).First();
            _button ??= CreateButton();
            const double size = 44, margin = 12;
            _button.SetPlacement(new(screen.X + Math.Max(0, screen.Width - size - margin), screen.Y + Math.Max(0, screen.Height - size - margin), Math.Min(size, screen.Width), Math.Min(size, screen.Height)));
            if (!_button.IsVisible) _button.Show();
        }
        catch (Exception ex) { Debug.WriteLine($"Panel reveal: {ex}"); Hide(); }
    }
    private PanelRevealWindow CreateButton()
    {
        var window = new PanelRevealWindow();
        window.RevealRequested += (_, _) => { Hide(); _reveal(); };
        // Close is final, unlike Hide: recreate safely if Alt+F4 closes the helper.
        window.Closed += (_, _) => _button = null;
        return window;
    }
    private void Hide() { _delay.Update(false, _clock.Elapsed); _button?.Hide(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); _button?.Close(); _button = null; }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint relation);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeRect(nint window, int attribute, out NativeRect value, int size);
}
