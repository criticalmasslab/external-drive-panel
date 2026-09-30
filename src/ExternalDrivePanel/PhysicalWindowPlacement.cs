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

    public PhysicalWindowPlacement(Window window)
    {
        _window = window;
        _window.Loaded += (_, _) => Apply();
        _window.Closed += (_, _) => _pending = null;
    }

    public void SetPlacement(LayoutRect rectangle)
    {
        _pending = rectangle;
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
        if (!_window.IsLoaded || _pending is not { } rectangle)
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
                SwpNoZOrder | SwpNoActivate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "창을 선택한 화면 영역에 배치하지 못했습니다.");
        }
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

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
