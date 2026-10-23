using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ExternalDrivePanel;

/// <summary>A normal (not topmost) window group raised only by an actual panel click.</summary>
public static class PanelActivation
{
    public static void Attach(Window window, Func<IEnumerable<Window>> windows)
    {
        MouseButtonEventHandler click = (_, _) => Raise(window, windows());
        window.AddHandler(Mouse.PreviewMouseDownEvent, click, true);
        window.Closed += (_, _) => window.RemoveHandler(Mouse.PreviewMouseDownEvent, click);
    }

    public static void Raise(Window clicked, IEnumerable<Window> windows)
    {
        // NOACTIVATE while ordering siblings; the clicked window gets activation last.
        foreach (var window in windows.Distinct().Where(window => window != clicked && window.IsVisible))
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x13);
        }
        var target = new WindowInteropHelper(clicked).Handle;
        if (target != IntPtr.Zero) SetWindowPos(target, IntPtr.Zero, 0, 0, 0, 0, 0x13);
        clicked.Activate();
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
