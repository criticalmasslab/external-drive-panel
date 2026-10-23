using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExternalDrivePanel;
using System.Windows.Interop;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        Logic();
        if (args.Contains("--logic-only")) return 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var result = 0;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { Icons(); await Native(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        }));
        app.Run(); return result;
    }
    private static void Logic()
    {
        LayoutRect group = new(0, 0, 300, 300);
        OcclusionWindow panel = new(1, group, true);
        LayoutRect[] screens = [new(-2000, -2000, 4000, 4000)];
        bool Accessible(params OcclusionWindow[] windows) => PanelOcclusionPolicy.HasAccessibleEdge(group, windows, screens);
        Require(Accessible(panel), "uncovered panel has an accessible edge");
        Require(!Accessible(new(2, group, false), panel), "fully covered panel needs button");
        Require(Accessible(panel, new(2, group, false)), "windows behind panel do not occlude it");
        Require(!Accessible(new(2, new(0, 0, 259, 300), false), panel), "41px strip is insufficient");
        Require(Accessible(new(2, new(0, 0, 258, 300), false), panel), "42px strip is sufficient");
        Require(!Accessible(new(2, new(0, 83, 300, 217), false), new(3, new(42, 0, 258, 83), false), panel), "42x83 corner is too short");
        Require(!Accessible(new(2, new(0, 0, 150, 300), false), new(3, new(150, 0, 150, 300), false), panel), "multiple covering windows combine");
        Require(PanelOcclusionPolicy.HasAccessibleEdge(new(0, 0, 100, 85), [new(2, new(42, 0, 58, 85), false),
            new(1, new(0, 0, 100, 42), true), new(3, new(0, 42, 100, 43), true)], screens), "adjoining panels form one 85px accessible strip");
        Require(!PanelOcclusionPolicy.HasAccessibleEdge(new(-299, 0, 300, 300), [new(1, new(-299, 0, 300, 300), true)], [new(0, 0, 1000, 1000)]), "offscreen pixels do not count");
        var delay = new RevealButtonDelay();
        Require(!delay.Update(true, TimeSpan.Zero) && !delay.Update(true, TimeSpan.FromMilliseconds(499))
            && delay.Update(true, TimeSpan.FromMilliseconds(500)), "500ms continuous occlusion delay");
        Require(!delay.Update(false, TimeSpan.FromSeconds(1)) && !delay.Update(true, TimeSpan.FromSeconds(2)), "uncovering resets delay");
        Console.WriteLine("PASS occlusion geometry: z-order, combined windows, edge thresholds, corner length, panel seams, offscreen and delay.");
    }
    private static void Icons()
    {
        var settings = new GlobalViewSettings(); var bar = new TopBarWindow(settings);
        var stateIcon = (SymbolIcon)bar.FindName("PreviewIcon");
        Require(!stateIcon.IsColorized, "preview off starts outlined");
        settings.TogglePreviews(); Require(stateIcon.IsColorized, "preview enabled uses color");
        settings.TogglePreviews(); Require(!stateIcon.IsColorized, "preview disabled restores outline");
        bar.Close();
        var icon = new SymbolIcon { Kind = "Preview", IsColorized = true, Width = 130, Height = 130, Foreground = Brushes.White };
        icon.Measure(new Size(130, 130)); icon.Arrange(new Rect(0, 0, 130, 130)); icon.UpdateLayout();
        var bitmap = new RenderTargetBitmap(130, 130, 96, 96, PixelFormats.Pbgra32); bitmap.Render(icon);
        var bytes = new byte[130 * 130 * 4]; bitmap.CopyPixels(bytes, 130 * 4, 0);
        bool Pixel(int x, int y, Color color) { var i = (y * 130 + x) * 4; return bytes[i] == color.B && bytes[i + 1] == color.G && bytes[i + 2] == color.R && bytes[i + 3] == 255; }
        Require(Pixel(30, 30, Colors.SkyBlue) && Pixel(85, 40, Colors.DarkOrange) && Pixel(50, 95, Colors.ForestGreen), "rendered sky, sun and mountains use requested solid colors");
        Require(Pixel(17, 30, Colors.SkyBlue) && Pixel(17, 95, Colors.ForestGreen), "color fills retain the former outer stroke footprint");
        Require(!Enumerable.Range(0, 130 * 130).Any(pixel => bytes[pixel * 4] == 255 && bytes[pixel * 4 + 1] == 255
            && bytes[pixel * 4 + 2] == 255 && bytes[pixel * 4 + 3] != 0), "active preview has no white outline pixels");
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "preview-colored.png")); png.Save(output);
        Console.WriteLine("PASS preview: toggle propagation and rendered sky-blue/orange/green pixels.");
    }
    private static async Task Native()
    {
        var monitor = new MonitorService().GetMonitors().First();
        var bounds = new LayoutRect(monitor.Left + 100, monitor.Top + 100, 300, 220);
        var panel = Fixture("Panel fixture"); var cover = Fixture("Cover fixture");
        PanelRevealService? service = null;
        try
        {
            Place(panel, bounds); Place(cover, bounds); cover.Activate();
            var reveals = 0;
            service = new PanelRevealService(() => [panel], () => bounds, () =>
            { reveals++; cover.Hide(); panel.Topmost = false; PanelActivation.Raise(panel, [panel]); }, ownForeground: () => false);
            service.Check(); Require(!service.IsButtonVisible, "no instant flash before delay");
            await Task.Delay(1000); service.Check();
            Require(service.IsButtonVisible, "native covering window triggers helper");
            var helper = Application.Current.Windows.OfType<PanelRevealWindow>().Single();
            var button = (Button)helper.Content; button.ApplyTemplate();
            var hover = button.Template.Triggers.OfType<Trigger>().Single(trigger => trigger.Property == UIElement.IsMouseOverProperty);
            var setter = hover.Setters.OfType<Setter>().Single();
            Require(setter.Property == UIElement.RenderTransformProperty && setter.TargetName == "IconPresenter"
                && setter.Value is ScaleTransform { ScaleX: 1.08, ScaleY: 1.08 }, "hover only scales the icon, without theme hover chrome");
            var presenter = (ContentPresenter)button.Template.FindName("IconPresenter", button);
            var restingSize = button.RenderSize;
            presenter.RenderTransform = (ScaleTransform)setter.Value;
            helper.UpdateLayout();
            Require(button.RenderSize == restingSize && button.FocusVisualStyle is null, "hover enlargement keeps button layout and hit target unchanged");
            presenter.ClearValue(UIElement.RenderTransformProperty);
            Require(helper.Topmost && !helper.ShowInTaskbar && !helper.ShowActivated && cover.IsActive, "helper stays accessible without stealing focus");
            GetWindowRect(new WindowInteropHelper(helper).Handle, out var rect);
            Require(rect.Right <= monitor.Right - 12 && rect.Bottom <= monitor.Bottom - 12 && rect.Right - rect.Left == 44, "helper is above taskbar inside work area");
            ((Button)helper.Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(reveals == 1 && !service.IsButtonVisible && !panel.Topmost, "click raises panels and hides helper, not permanent topmost panels");
            service.Check(); Require(!service.IsButtonVisible, "uncovered panel keeps helper hidden");
            panel.Topmost = true; cover.Show(); Place(cover, bounds with { Width = 259 });
            await Task.Delay(900); service.Check();
            Require(service.IsButtonVisible, "native 41px edge still needs helper");
            Place(cover, bounds with { Width = 258 });
            await Task.Delay(80); service.Check();
            Require(!service.IsButtonVisible, "native 42px edge hides helper");
            Console.WriteLine("PASS native: occlusion detection, delayed topmost/no-focus helper, placement and reveal click.");
        }
        finally { service?.Dispose(); cover.Close(); panel.Close(); }
    }
    private static Window Fixture(string title)
    {
        var window = new Window { Title = title, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Width = 300, Height = 220, Topmost = true, Background = Brushes.SlateGray };
        window.Show(); return window;
    }
    private static void Place(Window window, LayoutRect bounds) => SetWindowPos(new WindowInteropHelper(window).Handle, new nint(-1), (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, 0x10);
    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}
