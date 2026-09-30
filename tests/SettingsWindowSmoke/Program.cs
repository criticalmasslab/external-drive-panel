using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ExternalDrivePanel;

// Interactive integration test: creates real WPF windows and invokes their routed
// button events in-process. Run on an unlocked Windows desktop.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 0;
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try { Run(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { app.Shutdown(); }
        }));
        app.Run();
        return exitCode;
    }

    private static void Run()
    {
        var monitors = new MonitorService().GetMonitors();
        Require(monitors.Count > 0, "No monitors available");
        var layout = new LayoutCalculator();
        var view = new GlobalViewSettings();
        var owner = new TopBarWindow(view);
        var panel = new DrivePanelWindow(@"C:\", "Smoke test", view, new EjectService());
        owner.Show();
        panel.Show();

        for (var index = 0; index < monitors.Count; index++)
        {
            var monitor = monitors[index];
            var settings = new ZoneSettings { MonitorIndex = index };
            void Reposition()
            {
                var cell = layout.CalculateCell(monitor, settings);
                owner.SetPlacement(layout.CalculateTopBar(cell));
                panel.SetPlacement(layout.CalculatePanels(cell, 1, settings.SplitDirection)[0]);
            }
            Reposition();
            // Test reopening after moving the owner, as well as repeated Apply.
            for (var pass = 0; pass < 2; pass++)
            {
                Exception? failure = null;
                var dialog = new ZoneSettingsWindow(settings, monitors) { Owner = owner };
                var applications = 0;
                dialog.SettingsSaved += (_, _) => { applications++; Reposition(); };
                dialog.ContentRendered += (_, _) =>
                {
                    try
                    {
                        var bounds = Bounds(dialog);
                        Require(bounds.Left >= monitor.Left && bounds.Top >= monitor.Top
                            && bounds.Right <= monitor.Right && bounds.Bottom <= monitor.Bottom,
                            "Dialog extends outside owner monitor work area");
                        Require(Math.Abs(bounds.Left + bounds.Right - monitor.Left - monitor.Right) <= 2
                            && Math.Abs(bounds.Top + bounds.Bottom - monitor.Top - monitor.Bottom) <= 2,
                            "Dialog not centered on owner monitor");
                        Console.WriteLine($"Monitor {index}, reopen {pass}: dialog [{bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}]");
                        for (var edit = 1; edit <= 2; edit++)
                        {
                            ((TextBox)dialog.FindName("GridColsText")).Text = edit.ToString();
                            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Require(dialog.IsVisible && settings.GridCols == edit && applications == edit,
                                "Apply must update settings and keep dialog open");
                            var expected = layout.CalculateCell(monitor, settings);
                            Require(Bounds(owner).Left == (int)Math.Round(expected.X)
                                && Bounds(panel).Left == (int)Math.Round(expected.X),
                                "Apply did not reposition owner and panel");
                        }
                        ((TextBox)dialog.FindName("GridColsText")).Text = "4";
                        ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(!dialog.IsVisible && settings.GridCols == 4 && applications == 3,
                            "Save must apply and close dialog");
                    }
                    catch (Exception ex) { failure = ex; dialog.Close(); }
                };
                var result = dialog.ShowDialog();
                if (failure is not null) throw failure;
                Require(result == true, "Save did not set successful dialog result");
            }
        }
        panel.Close();
        owner.Close();
        Console.WriteLine($"PASS: centered/reopened on {monitors.Count} monitors; repeated Apply updates both windows; Save applies and closes.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static NativeRect Bounds(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var rect))
            throw new InvalidOperationException("GetWindowRect failed");
        return rect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}
