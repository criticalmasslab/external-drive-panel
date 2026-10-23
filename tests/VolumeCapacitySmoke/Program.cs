using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread] private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                Require(!new VolumeCapacitySnapshot(100, 20, 20).IsHighUsage, "exactly 80% is not over threshold");
                Require(new VolumeCapacitySnapshot(1000, 199, 199).IsHighUsage, "80.1% is over threshold");
                Require(new VolumeCapacitySnapshot(100, 999, 999).UsedFraction == 0, "invalid free count is clamped");
                Require(new VolumeCapacitySnapshot(0, 0, 0).UsedFraction == 0, "zero-size volume is safe");
                Require(new VolumeCapacitySnapshot(100, 0, 0).UsedFraction == 1, "full volume");
                Require(new VolumeCapacitySnapshot(100, 40, 20).UsedFraction == .6, "usage uses total free, availability can reflect quota");
                Require(VolumeCapacitySnapshot.FormatBytes(1024).EndsWith("KB") && VolumeCapacitySnapshot.FormatBytes(1UL << 40).EndsWith("TB"), "capacity units");
                var real = await Task.Run(() => VolumeCapacitySnapshot.Read(root));
                Require(real is { TotalBytes: > 0 } && real.FreeBytes <= real.TotalBytes, "real volume capacity is read without traversing files");
                foreach (var monitor in new MonitorService().GetMonitors())
                {
                    var panel = new DrivePanelWindow(root, "용량 바 테스트", new GlobalViewSettings(), new EjectService());
                    var bar = (VolumeCapacityBar)panel.FindName("CapacityBar");
                    bar.RootPath = ""; // No asynchronous polling may overwrite synthetic threshold snapshots.
                    panel.SetPlacement(new(monitor.Left + 20, monitor.Top + 50, 480, 500)); panel.Show();
                    try
                    {
                        var blue = Color.FromRgb(0x86, 0xBE, 0xE8); var red = Color.FromRgb(0xE6, 0x8E, 0x8E);
                        foreach (var width in new[] { 300, 360, 480, 760 })
                        {
                            panel.SetPlacement(new(monitor.Left + 20, monitor.Top + 50, width, 500));
                            foreach (var free in new ulong[] { 1000, 500, 200, 199, 0 })
                            {
                                var snapshot = new VolumeCapacitySnapshot(1000, free, free); bar.ApplySnapshot(snapshot);
                                await Layout(panel);
                                var dpi = VisualTreeHelper.GetDpi(panel);
                                var fill = bar.Children.OfType<Border>().Single();
                                Require(Math.Abs(bar.ActualHeight * dpi.DpiScaleY - 24) < 1, "capacity row is 24 physical pixels at every DPI");
                                Require(Math.Abs(fill.ActualWidth - bar.ActualWidth * snapshot.UsedFraction) * dpi.DpiScaleX <= 1.1, "used width follows fraction and panel resize");
                                Require(((SolidColorBrush)fill.Background).Color == (free < 200 ? red : blue), "blue until usage exceeds 80%, red thereafter");
                                Require(bar.Children.OfType<TextBlock>().Single().Text == snapshot.Caption && bar.ToolTip is string, "capacity text is inside the bar with full tooltip");
                            }
                        }
                        panel.SetPlacement(new(monitor.Left + 20, monitor.Top + 50, 480, 500));
                        const ulong gb = 1024UL * 1024 * 1024;
                        bar.ApplySnapshot(new(236 * gb, 90 * gb, 90 * gb)); await Layout(panel); Save(panel, "capacity-blue.png");
                        bar.ApplySnapshot(new(236 * gb, 30 * gb, 30 * gb)); await Layout(panel); Save(panel, "capacity-red.png");
                        bar.ApplySnapshot(null); await Layout(panel);
                        Require(bar.Children.OfType<TextBlock>().Single().Text.Contains("읽을 수 없습니다"), "disconnected/unreadable state is non-modal");
                        bar.RootPath = root; await bar.RefreshAsync();
                        Require(bar.Snapshot is { TotalBytes: > 0 }, "async capacity refresh recovers after unavailable state");
                        Console.WriteLine($"PASS capacity UI DPI {VisualTreeHelper.GetDpi(panel).DpiScaleX}: 300/360/480/760px, zero/full/80%/80.1%, label, unreadable/recovery.");
                    }
                    finally { panel.Close(); }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        }));
        app.Run(); Directory.Delete(root, true); return result;
    }
    private static Task Layout(Window window) => window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle).Task;
    private static void Require(bool value, string description) { if (!value) throw new Exception(description); }
    private static void Save(Window window, string filename)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Round(window.ActualWidth * dpi.DpiScaleX), (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, filename)); png.Save(stream);
    }
}
