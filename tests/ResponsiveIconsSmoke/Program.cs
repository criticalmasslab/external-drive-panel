using System.IO;
using System.Reflection;
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
        var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Responsive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = new DriveConfig();
        for (var i = 0; i < 48; i++)
        {
            var folder = i % 4 == 0;
            var name = folder ? $"폴더 {i:00}" : $"한글 파일과 긴 이름 {i:00}.txt";
            if (folder) Directory.CreateDirectory(Path.Combine(root, name)); else File.WriteAllText(Path.Combine(root, name), "responsive test");
            settings.Favorites.Add(new() { RelativePath = name, DisplayName = name, IsDirectory = folder });
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var result = 0;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                foreach (var monitor in new MonitorService().GetMonitors())
                {
                    var view = new GlobalViewSettings();
                    var panel = new DrivePanelWindow(root, "Responsive", view, new EjectService(), settings);
                    var top = new TopBarWindow(view);
                    void Apply(object? sender, EventArgs e) => panel.ApplyGlobalViewSettings();
                    view.Changed += Apply;
                    try
                    {
                        panel.SetPlacement(new(monitor.Left + 30, monitor.Top + 100, 480, 650)); panel.Show();
                        top.SetPlacement(new(monitor.Left + 30, monitor.Top + 50, 480, LayoutCalculator.TopBarHeight)); top.Show();
                        foreach (var home in new[] { true, false })
                        {
                            if (home) Call(panel, "ShowFavorites", true); else Call(panel, "NavigateTo", root, true);
                            var list = (ListBox)panel.FindName(home ? "FavoriteItemsList" : "IconItemsList");
                            view.Restore(new GlobalViewConfig { IconSize = 64 });
                            var selected = list.Items[3]; list.SelectedItem = selected;
                            var columns = new List<int>(); var widths = new List<double>();
                            for (var size = 64; size >= 16; size -= 4)
                            {
                                await Layout(panel);
                                var wrap = Descendants<WrapPanel>(list).Single();
                                var count = Columns(list, wrap); columns.Add(count); widths.Add(wrap.ItemWidth);
                                Require(view.IconSize == size && ReferenceEquals(list.SelectedItem, selected), "minus updates shared size without losing selection");
                                Require(wrap.ItemWidth == size * 2 + 28 && wrap.ItemHeight == Math.Ceiling(size * 1.4) + 48, "complete tile slot follows icon size");
                                foreach (var container in list.Items.Cast<object>().Select(item => (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item)))
                                {
                                    var bounds = container.TransformToAncestor(wrap).TransformBounds(new Rect(container.RenderSize));
                                    Require(bounds.Left >= 0 && bounds.Right <= wrap.ActualWidth + .1, "no tile overflows horizontally");
                                }
                                if (size > 16) ((Button)top.FindName("SmallerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                            Require(columns.Zip(columns.Skip(1)).All(pair => pair.First <= pair.Second)
                                && columns.Last() >= columns.First() + 3, "shrinking icons produces progressively more columns");
                            Require(widths.Zip(widths.Skip(1)).All(pair => pair.First > pair.Second), "each minus click shrinks tile spacing too");
                            var responsiveColumns = new List<int>();
                            foreach (var width in new[] { 360, 480, 760 })
                            {
                                panel.SetPlacement(new(monitor.Left + 30, monitor.Top + 100, width, 650)); await Layout(panel);
                                responsiveColumns.Add(Columns(list, Descendants<WrapPanel>(list).Single()));
                            }
                            Require(responsiveColumns.Zip(responsiveColumns.Skip(1)).All(pair => pair.First < pair.Second), "panel width immediately reflows columns");
                            panel.SetPlacement(new(monitor.Left + 30, monitor.Top + 100, 480, 650));
                            ((Button)top.FindName("LargerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout(panel);
                            Require(view.IconSize == 20 && Columns(list, Descendants<WrapPanel>(list).Single()) <= columns.Last(), "plus expands the same tile grid");
                            SavePreview(panel, $"{(home ? "home" : "folder")}-{VisualTreeHelper.GetDpi(panel).DpiScaleX}-20.png");
                            Console.WriteLine($"PASS DPI {VisualTreeHelper.GetDpi(panel).DpiScaleX}, {(home ? "home" : "folder")}: icon 64→16 columns [{string.Join(",", columns.Distinct())}], widths 360/480/760 [{string.Join(",", responsiveColumns)}]; selection and horizontal bounds intact.");
                        }
                        ((Button)panel.FindName("ViewModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        ((Button)top.FindName("SmallerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(panel.EffectiveViewMode == GlobalViewMode.Details, "icon size changes preserve independent details mode");
                    }
                    finally { view.Changed -= Apply; panel.Close(); top.Close(); }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        }));
        try { app.Run(); } finally { Directory.Delete(root, true); }
        return result;
    }
    private static async Task Layout(Window window) => await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    private static int Columns(ListBox list, WrapPanel wrap)
    {
        var origins = list.Items.Cast<object>().Select(item => ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item)).TranslatePoint(new Point(), wrap)).ToArray();
        return origins.Count(point => Math.Abs(point.Y - origins[0].Y) < .1);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void SavePreview(Window window, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)Math.Round(window.ActualWidth * dpi.DpiScaleX), (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
    }
    private static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
}
