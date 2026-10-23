using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExternalDrivePanel;

// Interactive integration test: creates real WPF windows and invokes their routed
// button events in-process. Run on an unlocked Windows desktop.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 0;
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                if (args.Contains("--interactive"))
                {
                    // Same native popup, isolated from the user's persisted settings.
                    new ZoneSettingsWindow(new ZoneSettings(), new MonitorService().GetMonitors())
                        { Title = "화면 영역 설정 — 테스트" }.ShowDialog();
                }
                else Run();
            }
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
        foreach (var monitor in monitors)
        {
            var startup = new TopBarWindow(new GlobalViewSettings());
            var expected = layout.CalculateTopBar(layout.CalculateCell(monitor, new ZoneSettings()));
            startup.SetPlacement(expected);
            var observed = new List<NativeRect>();
            startup.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(startup).Handle).AddHook(
                (IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (message == 0x47 && (Marshal.PtrToStructure<WindowPosition>(lParam).Flags & 0x40) != 0)
                        observed.Add(Bounds(startup));
                    return IntPtr.Zero;
                });
            startup.Show();
            var frame = new DispatcherFrame();
            startup.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Console.WriteLine($"First show expected [{expected.X},{expected.Y},{expected.Width},{expected.Height}], observed " +
                string.Join(";", observed.Select(rect => $"[{rect.Left},{rect.Top},{rect.Right - rect.Left},{rect.Bottom - rect.Top}]")));
            Require(observed.Count > 0 && observed.All(rect => rect.Left == (int)Math.Round(expected.X)
                && rect.Top == (int)Math.Round(expected.Y) && rect.Right - rect.Left == (int)Math.Round(expected.Width)
                && rect.Bottom - rect.Top == (int)Math.Round(expected.Height)), "first native SHOWWINDOW already has final physical bounds; no desktop-sized flash");
            startup.Close();
        }
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
                        var header = (Border)dialog.FindName("SettingsHeader");
                        var dpi = VisualTreeHelper.GetDpi(dialog);
                        var handle = new WindowInteropHelper(dialog).Handle;
                        var clientOrigin = new NativePoint();
                        Require(ClientToScreen(handle, ref clientOrigin) && Math.Abs(clientOrigin.Y - bounds.Top) <= 1,
                            "client area starts at window top: no native white resize/title strip");
                        Require((bounds.Right - bounds.Left) / dpi.DpiScaleX <= 392
                            && (bounds.Bottom - bounds.Top) / dpi.DpiScaleY < 560,
                            "dialog uses compact width and content-based height instead of 500x700");
                        Require(System.Windows.Shell.WindowChrome.GetWindowChrome(dialog)?.ResizeBorderThickness.Left == 5,
                            "invisible resize border preserved without the native frame");
                        var resizePoint = new IntPtr(((bounds.Top + 2) << 16) | ((bounds.Left + (bounds.Right - bounds.Left) / 2) & 0xffff));
                        Require(SendMessageW(handle, 0x84, IntPtr.Zero, resizePoint).ToInt32() == 12,
                            "top edge still reports the native resize hit target");
                        Require(Math.Abs(header.ActualHeight * dpi.DpiScaleY - LayoutCalculator.TopBarHeight) < 1,
                            "settings header uses same physical height as panel headers");
                        Require(((RadioButton)((StackPanel)dialog.FindName("MonitorOptions")).Children[0]).Template is not null,
                            "monitor options use panel-family templates");
                        var grid = (System.Windows.Controls.Primitives.UniformGrid)dialog.FindName("GridPreview");
                        var selectedCell = grid.Children.OfType<Button>().Single(cell => ((SolidColorBrush)cell.Background).Color == Color.FromRgb(36, 52, 71));
                        Require(((SolidColorBrush)selectedCell.BorderBrush).Color == Colors.Gold,
                            "selected cell uses panel navy with the favorites gold accent");
                        if (pass == 0) SavePreview(dialog, $"settings-{index}.png");
                        for (var edit = 1; edit <= 2; edit++)
                        {
                            ((TextBox)dialog.FindName("GridColsText")).Text = edit.ToString();
                            Require(dialog.IsVisible && settings.GridCols == edit && applications == edit,
                                "editing immediately updates settings and keeps dialog open");
                            var expected = layout.CalculateCell(monitor, settings);
                            Require(Bounds(owner).Left == (int)Math.Round(expected.X)
                                && Bounds(panel).Left == (int)Math.Round(expected.X),
                                "Apply did not reposition owner and panel");
                        }
                        ((TextBox)dialog.FindName("GridColsText")).Text = "4";
                        Require(dialog.FindName("ApplyButton") is null && dialog.FindName("SaveButton") is null, "only cancel/close actions remain");
                        ((Button)dialog.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(!dialog.IsVisible && settings.GridCols == 4 && applications == 3,
                            "Close keeps live settings and closes dialog");
                    }
                    catch (Exception ex) { failure = ex; dialog.Close(); }
                };
                var result = dialog.ShowDialog();
                if (failure is not null) throw failure;
                Require(result == true, "Close did not set successful dialog result");
            }
            Exception? cancelFailure = null;
            var cancelDialog = new ZoneSettingsWindow(settings, monitors) { Owner = owner };
            cancelDialog.SettingsSaved += (_, _) => Reposition();
            cancelDialog.ContentRendered += (_, _) =>
            {
                try
                {
                    ((TextBox)cancelDialog.FindName("GridColsText")).Text = "5";
                    Require(settings.GridCols == 5, "live edit before cancellation");
                    ((TextBox)cancelDialog.FindName("GridColsText")).Text = "";
                    Require(settings.GridCols == 5 && !string.IsNullOrEmpty(((TextBlock)cancelDialog.FindName("ValidationMessage")).Text), "invalid intermediate input keeps last valid layout with inline feedback");
                    ((Button)cancelDialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { cancelFailure = ex; cancelDialog.Close(); }
            };
            Require(cancelDialog.ShowDialog() == false && settings.GridCols == 4, "Cancel restores opening settings");
            if (cancelFailure is not null) throw cancelFailure;
            Exception? chromeFailure = null;
            var chromeDialog = new ZoneSettingsWindow(settings, monitors) { Owner = owner };
            chromeDialog.SettingsSaved += (_, _) => Reposition();
            chromeDialog.ContentRendered += (_, _) =>
            {
                try
                {
                    ((CheckBox)chromeDialog.FindName("UniformMarginCheck")).IsChecked = false;
                    Require(((Grid)chromeDialog.FindName("IndividualMarginPanel")).IsVisible, "custom checkbox reveals individual margins");
                    ((TextBox)chromeDialog.FindName("MarginLeftText")).Text = "9";
                    ((RadioButton)chromeDialog.FindName("HorizontalSplitRadio")).IsChecked = true;
                    ((Button)((System.Windows.Controls.Primitives.UniformGrid)chromeDialog.FindName("GridPreview")).Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(settings.MarginLeft == 9 && !settings.MarginUniform && settings.SplitDirection == SplitDirection.Horizontal
                        && settings.CellCol == 1, "restyled input/choice/grid controls still apply immediately");
                    chromeDialog.UpdateLayout(); SavePreview(chromeDialog, $"settings-individual-{index}.png");
                    ((Button)chromeDialog.FindName("HeaderCloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { chromeFailure = ex; chromeDialog.Close(); }
            };
            Require(chromeDialog.ShowDialog() == true && settings.MarginLeft == 9, "header close preserves current settings");
            if (chromeFailure is not null) throw chromeFailure;
        }
        panel.Close();
        owner.Close();
        Console.WriteLine($"PASS: centered/reopened on {monitors.Count} monitors; live edits reposition both windows; invalid input is safe; Cancel rolls back and Close keeps changes.");
    }

    private static void SavePreview(Window window, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
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
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
