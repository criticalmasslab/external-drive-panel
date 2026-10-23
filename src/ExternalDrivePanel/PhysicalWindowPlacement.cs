using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ExternalDrivePanel;

/// <summary>Applies LayoutCalculator's physical desktop pixels without WPF DIP conversion.</summary>
internal sealed class PhysicalWindowPlacement
{
    private readonly Window _window;
    private LayoutRect? _pending;
    private HwndSource? _source;
    private bool _firstRenderComplete;
    private (IntPtr Handle, int Left, int Top, int Right, int Bottom)? _applied;
    [ThreadStatic] private static List<PhysicalWindowPlacement>? _batch;

    public static IDisposable BeginBatch() => new PlacementBatch();
    private sealed class PlacementBatch : IDisposable
    {
        private readonly List<PhysicalWindowPlacement>? _previous = _batch;
        public PlacementBatch() => _batch = [];
        public void Dispose()
        {
            var items = _batch!.Distinct().ToArray(); _batch = _previous;
            if (_previous is not null) { _previous.AddRange(items); return; }
            var changes = items.Where(item => item.NeedsPlacement()).ToArray();
            if (changes.Length == 0) return;
            var deferred = BeginDeferWindowPos(changes.Length);
            foreach (var item in changes)
            {
                if (deferred == IntPtr.Zero) break;
                var bounds = item.Bounds();
                deferred = DeferWindowPos(deferred, bounds.Handle, IntPtr.Zero, bounds.Left, bounds.Top,
                    Math.Max(1, bounds.Right - bounds.Left), Math.Max(1, bounds.Bottom - bounds.Top), PlacementFlags());
            }
            if (deferred != IntPtr.Zero && EndDeferWindowPos(deferred))
                foreach (var item in changes) item._applied = item.Bounds();
            else foreach (var item in changes) { item._applied = null; item.Apply(); }
        }
    }
    private (IntPtr Handle, int Left, int Top, int Right, int Bottom) Bounds()
    {
        var rect = _pending!;
        return (new WindowInteropHelper(_window).Handle, checked((int)Math.Round(rect.X)), checked((int)Math.Round(rect.Y)),
            checked((int)Math.Round(rect.X + rect.Width)), checked((int)Math.Round(rect.Y + rect.Height)));
    }
    private bool NeedsPlacement() => _pending is not null && new WindowInteropHelper(_window).Handle != IntPtr.Zero && _applied != Bounds();

    private static uint PlacementFlags() => SwpNoZOrder | SwpNoActivate;

    public PhysicalWindowPlacement(Window window)
    {
        _window = window;
        _window.SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
            _source?.AddHook(HandleWindowMessage);
            Apply();
        };
        _window.Loaded += (_, _) => Apply();
        _window.ContentRendered += (_, _) => { Apply(); _firstRenderComplete = true; };
        _window.Closed += (_, _) => { _source?.RemoveHook(HandleWindowMessage); _source = null; _pending = null; };
    }

    public void SetPlacement(LayoutRect rectangle)
    {
        _pending = rectangle;
        if (_batch is not null) { _batch.Add(this); return; }
        Apply();
    }

    public void CenterOnOwner(double widthInDips, double heightInDips)
    {
        var ownerHandle = new WindowInteropHelper(_window.Owner ?? _window).Handle;
        var monitor = MonitorFromWindow(ownerHandle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var dpi = GetDpiForWindow(ownerHandle);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var work = info.Work;
        var width = Math.Min(widthInDips * scale, work.Right - work.Left);
        var height = Math.Min(heightInDips * scale, work.Bottom - work.Top);
        SetPlacement(new LayoutRect(
            work.Left + (work.Right - work.Left - width) / 2,
            work.Top + (work.Bottom - work.Top - height) / 2,
            width, height));
    }

    private void Apply()
    {
        if (!NeedsPlacement()) return;
        if (_pending is not { } rectangle)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // Round shared edges, rather than widths, to avoid gaps between adjacent panels.
        var left = checked((int)Math.Round(rectangle.X));
        var top = checked((int)Math.Round(rectangle.Y));
        var right = checked((int)Math.Round(rectangle.X + rectangle.Width));
        var bottom = checked((int)Math.Round(rectangle.Y + rectangle.Height));
        if (!SetWindowPos(handle, IntPtr.Zero, left, top,
                Math.Max(1, right - left), Math.Max(1, bottom - top),
                PlacementFlags()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "창을 선택한 화면 영역에 배치하지 못했습니다.");
        }
        _applied = Bounds();
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    [DllImport("user32.dll")] private static extern IntPtr BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] private static extern IntPtr DeferWindowPos(IntPtr batch, IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndDeferWindowPos(IntPtr batch);

    private IntPtr HandleWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x24 && _pending is not null) // WM_GETMINMAXINFO
        {
            // Windows' default tracking limit is the virtual desktop plus its
            // frame. Our borderless group may intentionally span beyond that.
            // Do not let Windows silently clamp a requested physical width and
            // move the fixed opposite edge. Leave WPF's explicit Min/Max intact.
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var bounds = Bounds();
            limits.MaxTrack.X = Math.Max(limits.MaxTrack.X, bounds.Right - bounds.Left);
            limits.MaxTrack.Y = Math.Max(limits.MaxTrack.Y, bounds.Bottom - bounds.Top);
            Marshal.StructureToPtr(limits, lParam, false);
        }
        // WPF can otherwise show a desktop-sized default window before Loaded.
        // Constrain the first native show as well as SourceInitialized placement.
        if (message == 0x46 && !_firstRenderComplete && _pending is { } rectangle)
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            position.X = checked((int)Math.Round(rectangle.X)); position.Y = checked((int)Math.Round(rectangle.Y));
            position.Width = Math.Max(1, checked((int)Math.Round(rectangle.X + rectangle.Width)) - position.X);
            position.Height = Math.Max(1, checked((int)Math.Round(rectangle.Y + rectangle.Height)) - position.Y);
            position.Flags = (position.Flags & ~3u) | SwpNoActivate;
            Marshal.StructureToPtr(position, lParam, false);
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}
