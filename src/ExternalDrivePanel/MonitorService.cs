using System.Runtime.InteropServices;

namespace ExternalDrivePanel;

public sealed class MonitorService
{
    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitors = new List<MonitorDescriptor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(handle, ref info))
            {
                var work = info.WorkArea;
                monitors.Add(new MonitorDescriptor(
                    work.Left,
                    work.Top,
                    work.Right,
                    work.Bottom,
                    work.Right - work.Left,
                    work.Bottom - work.Top,
                    (info.Flags & MonitorInfoPrimary) != 0));
            }
            return true;
        }, IntPtr.Zero);

        return monitors
            .OrderByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.Left)
            .ThenBy(monitor => monitor.Top)
            .ToList();
    }

    private const uint MonitorInfoPrimary = 0x1;
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr monitorRect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }
}

public sealed record MonitorDescriptor(
    int Left,
    int Top,
    int Right,
    int Bottom,
    int Width,
    int Height,
    bool IsPrimary)
{
    public string DisplayName => $"{(IsPrimary ? "주 " : string.Empty)}모니터 ({Width} × {Height}, {Left}, {Top})";
}