using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var code = 0;
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Panel-PhysicalBounds-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                var monitors = new MonitorService().GetMonitors();
                foreach (var initial in monitors)
                {
                    var view = new GlobalViewSettings();
                    var header = new TopBarWindow(view) { ShowInTaskbar = true };
                    var panel = new DrivePanelWindow(root, "DPI regression fixture", view, new EjectService());
                    try
                    {
                        foreach (var monitor in new[] { initial }.Concat(monitors).Concat(monitors.Reverse()))
                        {
                            var top = new LayoutRect(monitor.Left + 40, monitor.Top + 20, 480, 42);
                            var drive = new LayoutRect(top.X, top.Y + 42, 480, 400);
                            header.SetPlacement(top); panel.SetPlacement(drive);
                            if (!header.IsVisible) { header.Show(); panel.Show(); }
                            await Idle();
                            Check(header, top); Check(panel, drive);
                            var hwnd = new WindowInteropHelper(header).Handle;
                            // Reproduce the reported native 480x42 -> 400x35 shrink
                            // without another SetPlacement call or a polling tick.
                            Require(SetWindowPos(hwnd, 0, (int)top.X, (int)top.Y, 400, 35, 0x14), "simulate late native resize");
                            await Idle(); Check(header, top);
                            Require(SetWindowPos(new WindowInteropHelper(panel).Handle, 0, (int)drive.X, (int)drive.Y, 400, 333, 0x14), "simulate panel DPI resize");
                            await Idle(); Check(panel, drive);
                            Console.WriteLine($"PASS HWND DPI {GetDpiForWindow(hwnd)}: initial/move and late native shrink restored to 480x42, panel 480x400.");
                        }
                        var settings = new ZoneSettingsWindow(new ZoneSettings(), monitors, new FakeStartup()) { Owner = header };
                        settings.Show(); await Idle();
                        var settingsHandle = new WindowInteropHelper(settings).Handle;
                        GetWindowRect(settingsHandle, out var rectangle);
                        var width = rectangle.Right - rectangle.Left + 20;
                        Require(SetWindowPos(settingsHandle, 0, rectangle.Left, rectangle.Top, width, rectangle.Bottom - rectangle.Top, 0x14), "manual dialog resize");
                        await Idle(); GetWindowRect(settingsHandle, out var resized);
                        Require(resized.Right - resized.Left == width, "movable/resizable dialog is not forced back"); settings.Close();
                    }
                    finally { panel.Close(); header.Close(); }
                }
                Console.WriteLine("PASS physical bounds: real mixed-DPI monitor moves, development taskbar header, native cache invalidation and dialog resize independence.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            finally { Directory.Delete(root, true); application.Shutdown(); }
        }));
        application.Run(); return code;
    }
    private static async Task Idle()
    {
        await Task.Delay(100);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }
    private static void Check(Window window, LayoutRect expected)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var actual);
        Require(actual.Left == (int)expected.X && actual.Top == (int)expected.Y
            && actual.Right - actual.Left == (int)expected.Width && actual.Bottom - actual.Top == (int)expected.Height,
            $"{window.Title}: expected {expected}, got {actual.Left},{actual.Top},{actual.Right - actual.Left},{actual.Bottom - actual.Top}");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class FakeStartup : IStartupRegistration { public bool IsEnabled { get; private set; } public void SetEnabled(bool enabled) => IsEnabled = enabled; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
