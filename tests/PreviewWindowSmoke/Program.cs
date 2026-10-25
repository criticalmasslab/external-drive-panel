using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread] private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Test(root); Console.WriteLine("PASS: shell thumbnail/associated icons; off/on/cancellation; home/folder/detail rendering; JSON compatibility; click-only foreground group; async operation leaves panels non-topmost."); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        }));
        try { app.Run(); } finally { Directory.Delete(root, true); }
        return result;
    }

    private static async Task Test(string root)
    {
        foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64 })
        {
            var mark = PanelTrayIcon.Render(size);
            Require(mark.IsFrozen && mark.PixelWidth == size && mark.PixelHeight == size, "tray frames rendered at native resolution");
            var output = new PngBitmapEncoder(); output.Frames.Add(BitmapFrame.Create(mark));
            using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, $"tray-{size}.png")); output.Save(stream);
        }
        using (var tray = PanelTrayIcon.Create())
        using (var bitmap = tray.ToBitmap())
            Require(tray.Handle != IntPtr.Zero && bitmap.Width >= 16, "multi-frame tray icon can be loaded after source stream disposal");
        var solidHouse = new SymbolIcon { Kind = "HomeFill", Foreground = Brushes.Gold, Width = 26, Height = 26 };
        solidHouse.Measure(new Size(26, 26)); solidHouse.Arrange(new Rect(0, 0, 26, 26)); solidHouse.UpdateLayout();
        var houseBitmap = new RenderTargetBitmap(26, 26, 96, 96, PixelFormats.Pbgra32); houseBitmap.Render(solidHouse);
        var pixel = new byte[4]; houseBitmap.CopyPixels(new Int32Rect(8, 16, 1, 1), pixel, 4, 0);
        Require(pixel[3] == 255 && pixel[2] == 255 && pixel[1] == 215, "house interior is solid gold, not merely an outline");
        var png = Path.Combine(root, "미리보기 사진.png");
        var draw = new DrawingVisual();
        using (var dc = draw.RenderOpen())
        {
            dc.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 180, 120));
            dc.DrawEllipse(Brushes.Gold, null, new Point(125, 32), 18, 18);
            dc.DrawGeometry(Brushes.ForestGreen, null, Geometry.Parse("M0,120 L60,35 110,95 140,65 180,120 Z"));
        }
        var fixture = new RenderTargetBitmap(180, 120, 96, 96, PixelFormats.Pbgra32); fixture.Render(draw);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(fixture));
        using (var stream = File.Create(png)) encoder.Save(stream);
        var text = Path.Combine(root, "설명.txt"); File.WriteAllText(text, "preview fixture");
        var folder = Directory.CreateDirectory(Path.Combine(root, "폴더")).FullName;
        var thumbnail = await ShellPreviewService.LoadAsync(png, 96);
        Require(thumbnail is { IsFrozen: true } && thumbnail.PixelWidth > 0, "PNG thumbnail via shell");
        var associated = await ShellPreviewService.LoadAsync(text, 32);
        Require(associated is { IsFrozen: true }, "text associated icon via shell");
        Require(ReferenceEquals(thumbnail, await ShellPreviewService.LoadAsync(png, 96)), "bounded bitmap cache reused");
        Require(await ShellPreviewService.LoadAsync(Path.Combine(root, "missing.png"), 32) is null, "missing file safe fallback");
        using (var canceled = new CancellationTokenSource())
        { canceled.Cancel(); Require(await ShellPreviewService.LoadAsync(png, 64, canceled.Token) is null, "canceled request is not extracted"); }

        var view = new GlobalViewSettings();
        var saved = new DriveConfig { ViewMode = GlobalViewMode.Icon, Favorites = [
            new() { RelativePath = Path.GetFileName(png), DisplayName = "미리보기 사진", IsDirectory = false },
            new() { RelativePath = Path.GetFileName(text), DisplayName = "설명", IsDirectory = false },
            new() { RelativePath = "폴더", DisplayName = "폴더", IsDirectory = true }] };
        var panel = new DrivePanelWindow(root, "Preview", view, new EjectService(), saved);
        var top = new TopBarWindow(view);
        var sibling = new Window { Width = 260, Height = 140, ShowInTaskbar = false, Title = "Sibling fixture" };
        var external = new Window { Width = 280, Height = 160, ShowInTaskbar = false, Title = "External application fixture" };
        void Apply(object? sender, EventArgs args) => panel.ApplyGlobalViewSettings();
        view.Changed += Apply;
        PanelActivation.Attach(panel, () => new Window[] { panel, top, sibling });
        try
        {
            var monitor = new MonitorService().GetMonitors().First();
            panel.SetPlacement(new(monitor.Left + 30, monitor.Top + 100, 600, 560)); panel.Show();
            top.SetPlacement(new(monitor.Left + 30, monitor.Top + 55, 600, LayoutCalculator.TopBarHeight)); top.Show();
            await Layout(panel);
            Require(Previews(panel).Where(p => p.IsVisible).All(p => !p.PreviewEnabled && p.PreviewSource is null), "default display unchanged");
            var home = (Button)panel.FindName("HomeButton");
            Require(home.Background == Brushes.SlateGray && ((SymbolIcon)panel.FindName("HomeIcon")).Foreground == Brushes.Gold
                && ((SymbolIcon)panel.FindName("HomeIcon")).Kind == "HomeFill", "home uses solid gold house");
            var previewButton = (Button)top.FindName("PreviewButton");
            var buttons = ((StackPanel)previewButton.Parent).Children.OfType<Button>().ToList();
            Require(buttons.Take(4).Select(button => button.Name).SequenceEqual(new[] { "SettingsButton", "RefreshButton", "PreviewButton", "HiddenButton" }),
                "header order is settings, refresh, preview, hidden");
            Click(previewButton);
            await Wait(() => Previews(panel).Count(p => p.IsVisible && p.PreviewSource is not null) == 3, "favorites thumbnails rendered");
            Require(panel.EffectiveViewMode == GlobalViewMode.Icon, "preview toggle preserves panel override");
            Save(panel, "home-preview.png"); Save(top, "header-preview.png");
            Click(previewButton); await Layout(panel);
            Require(Previews(panel).Where(p => p.IsVisible).All(p => p.PreviewSource is null), "off returns to original glyphs");
            Click(previewButton); Click(previewButton); await Task.Delay(150); await Layout(panel);
            Require(!view.ShowPreviews && Previews(panel).Where(p => p.IsVisible).All(p => p.PreviewSource is null), "stale request cannot restore disabled previews");
            Click(previewButton);
            Call(panel, "NavigateTo", root, true); await Layout(panel);
            Require(home.Background == Brushes.Transparent && ((SymbolIcon)panel.FindName("HomeIcon")).Foreground == Brushes.White
                && ((SymbolIcon)panel.FindName("HomeIcon")).Kind == "Home", "folder restores outline home button");
            await Wait(() => Previews(panel).Count(p => p.IsVisible && p.PreviewSource is not null) == 3, "folder thumbnails rendered");
            Save(panel, "folder-preview.png");
            Click((Button)panel.FindName("ViewModeButton")); await Layout(panel);
            await Wait(() => Previews(panel).Any(p => p.IsVisible && p.PreviewSource is not null), "detail view associated icons rendered");
            Call(panel, "ShowFavorites", true); await Layout(panel);
            await Wait(() => Previews(panel).Count(p => p.IsVisible && p.PreviewSource is not null) == 3, "home detail icons rendered");
            Require(home.Background == Brushes.SlateGray, "history/home transitions restore home cue");

            var store = new ConfigStore(Path.Combine(root, "config.json")); var config = new AppConfig { GlobalView = view.Capture() };
            Require(store.Save(config) && store.Load().GlobalView.ShowPreviews, "preview persisted with config roundtrip");
            var restored = new GlobalViewSettings(); restored.Restore(store.Load().GlobalView);
            Require(restored.ShowPreviews, "preview restored at startup");
            File.WriteAllText(store.FilePath, "{\"version\":1,\"zone\":{},\"globalView\":{},\"drives\":{}}");
            Require(!store.Load().GlobalView.ShowPreviews, "old config defaults preview off");

            sibling.Show(); external.Show(); external.Activate(); await Layout(external);
            Require(Above(external, panel) && Above(external, top), "later external window above panel group");
            panel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            await Layout(panel);
            Require(Above(panel, external) && Above(top, external) && Above(sibling, external), "click raises complete normal panel group");
            Require(new[] { panel, top, sibling }.All(w => !w.Topmost), "panels are never permanently topmost");
            var gate = new TaskCompletionSource<bool>(); var operation = DialogActivation.ShowAsync(panel, () => gate.Task);
            external.Activate(); await Layout(external);
            Require(Above(external, panel) && !panel.Topmost, "external app can cover panel during asynchronous work");
            gate.SetResult(true); await operation;
        }
        finally { view.Changed -= Apply; external.Close(); sibling.Close(); panel.Close(); top.Close(); }
    }
    private static bool Above(Window first, Window second)
    {
        var target = new WindowInteropHelper(second).Handle;
        for (var window = new WindowInteropHelper(first).Handle; window != IntPtr.Zero; window = GetWindow(window, 2))
            if (window == target) return true;
        return false;
    }
    private static IEnumerable<FilePreview> Previews(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is FilePreview p) yield return p; foreach (var nested in Previews(child)) yield return nested; }
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static async Task Layout(Window window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static async Task Wait(Func<bool> condition, string message)
    { var until = DateTime.UtcNow.AddSeconds(15); while (!condition() && DateTime.UtcNow < until) await Task.Delay(40); Require(condition(), message); }
    private static void Save(Window window, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
    }
    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
}
