using System.IO;
using System.Reflection;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExternalDrivePanel;

internal static partial class Program
{
    [STAThread]
    private static int Main()
    {
        TestModel();
        var temporary = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-FavoriteLayout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var config = new AppConfig { NotifyNewDrive = false, DriveOrder = ["1", "2"] };
            var snapshots = new List<DriveSnapshot>();
            foreach (var (serial, count) in new[] { (1u, 2), (2u, 16) })
            {
                var root = Directory.CreateDirectory(Path.Combine(temporary, serial.ToString())).FullName + "\\";
                snapshots.Add(new(serial, root, "Layout fixture", DriveType.Removable));
                var settings = new DriveConfig { Registered = true };
                for (var i = 0; i < count; i++)
                {
                    var name = "Long favorite folder name " + i;
                    Directory.CreateDirectory(Path.Combine(root, name));
                    settings.Favorites.Add(new() { RelativePath = name, IsDirectory = true });
                }
                config.Drives[serial.ToString()] = settings;
            }
            var folder = Path.Combine(snapshots[0].Root, "Long favorite folder name 0");
            var emptyFolder = Directory.CreateDirectory(Path.Combine(folder, "Nested empty folder")).FullName;
            for (var i = 0; i < 32; i++) File.WriteAllText(Path.Combine(folder, $"Visible item {i:00}.txt"), "fixture");
            var hidden = Path.Combine(folder, "Hidden item.txt"); File.WriteAllText(hidden, "fixture");
            File.SetAttributes(hidden, FileAttributes.Hidden);
            var store = new ConfigStore(Path.Combine(temporary, "config.json"));
            Require(store.Save(config), "fixture saved");
            var app = new App(store, () => snapshots.ToArray());
            var code = 0;
            app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                try
                {
                    var panels = Field<Dictionary<uint, DrivePanelWindow>>(app, "_panels");
                    var state = Field<PanelLayoutState>(app, "_panelLayout");
                    Require(panels.Count == 2 && panels[1].FavoriteCount == 2 && panels[2].FavoriteCount == 16, "restored counts before layout");
                    Require(state.Panels()[1].Height > state.Panels()[0].Height, "more favorites receive more height");
                    CheckBounds(panels[1], state.Panels()[0]); CheckBounds(panels[2], state.Panels()[1]);
                    var zone = Field<ZoneSettings>(app, "_zoneSettings");
                    var views = Field<GlobalViewSettings>(app, "_viewSettings");
                    await TestLocalDetails(app, state, panels, views);
                    for (var monitor = 0; monitor < new MonitorService().GetMonitors().Count; monitor++)
                    {
                        zone.MonitorIndex = monitor;
                        foreach (var iconSize in new[] { 24, 48 })
                        {
                            views.Restore(new GlobalViewConfig { IconSize = iconSize });
                            Call(app, "ResetManualLayout");
                            await Task.Delay(120);
                            var rectangles = state.Panels();
                            var profiles = panels.Values.Select(panel => panel.FavoriteLayoutProfile).ToArray();
                            var enough = profiles.Sum(profile => profile.Measure(state.Cell.Width).Desired) <= rectangles.Sum(rect => rect.Height);
                            for (var i = 0; i < 2; i++)
                            {
                                var profile = profiles[i]; var panel = panels[(uint)i + 1];
                                var list = (ListBox)panel.FindName("FavoriteItemsList");
                                var scroll = Descendant<ScrollViewer>(list)!;
                                if (enough) Require(scroll.ScrollableHeight < 1, "all captions/rows fit when area is sufficient");
                                if (rectangles.Sum(rect => rect.Height) >= profiles.Sum(p => p.Measure(state.Cell.Width).Minimum))
                                    Require(rectangles[i].Height + 1 >= profile.Measure(state.Cell.Width).Minimum, "at least one complete row survives");
                            }
                            if (iconSize == 24) SaveScreenshot(app, panels, state, monitor);
                            Console.WriteLine($"PASS WPF monitor {monitor}, DPI {profiles[0].DpiScaleY}, icon {iconSize}: complete rows/captions, no avoidable scrollbars.");
                        }
                        views.SetViewMode(GlobalViewMode.Details);
                        await Task.Delay(100);
                        var detailHeights = state.Panels();
                        var detailProfiles = panels.Values.Select(panel => panel.FavoriteLayoutProfile).ToArray();
                        if (detailProfiles.Sum(profile => profile.Measure(state.Cell.Width).Desired) <= detailHeights.Sum(rect => rect.Height))
                            foreach (var panel in panels.Values)
                                Require(Descendant<ScrollViewer>((DataGrid)panel.FindName("DetailsGrid"))!.ScrollableHeight < 1,
                                    "detail rows and column headers fit when area is sufficient");
                        Console.WriteLine($"PASS WPF monitor {monitor}: detail rows/headers included in allocation.");
                        await TestFolderLayout(app, state, panels, views, folder, emptyFolder, monitor);
                        await TestSizingOptions(app, state, panels, views, folder, monitor);
                        await TestSearchLayout(app, state, panels, views, folder, monitor);
                    }
                    var favorites = Field<FavoriteCollection>(panels[2], "_favorites");
                    var beforeRemoval = state.Panels().ToArray();
                    using (favorites.BeginUpdate()) favorites.Items.Clear();
                    Require(state.Panels()[0].Height > beforeRemoval[0].Height && state.Panels()[1].Height < beforeRemoval[1].Height,
                        "favorite changes immediately rebalance live windows");
                    CheckBounds(panels[1], state.Panels()[0]); CheckBounds(panels[2], state.Panels()[1]);
                    snapshots.RemoveAt(1); Call(app, "RefreshPanels");
                    Require(panels.Count == 1 && state.Panels().Count == 1, "disconnect expands remaining panel");
                    snapshots.Clear(); Call(app, "RefreshPanels");
                    Require(panels.Count == 0 && state.Panels().Count == 0, "no drives leaves top bar only");
                    Console.WriteLine("PASS WPF: initial content-aware heights, immediate favorite changes, physical HWND bounds, disconnect, zero drives.");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
                finally { app.Shutdown(code); }
            }));
            app.Run(); return code;
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static void TestModel()
    {
        var cell = new LayoutRect(-900, -300, 500, 1042);
        var state = new PanelLayoutState();
        state.Configure(cell, Profiles(2, 16), SplitDirection.Vertical);
        var panels = state.Panels();
        var needs = Profiles(2, 16).Select(profile => profile.Measure(cell.Width)).ToArray();
        var spare = 1000 - needs.Sum(need => need.Desired);
        Require(panels[0].Height == Math.Round(needs[0].Desired + spare * needs[0].Content / needs.Sum(need => need.Content)),
            "actual rows fit first, spare height follows content proportions");
        var whitespace0 = (panels[0].Height - needs[0].Desired) / needs[0].Content;
        var whitespace1 = (panels[1].Height - needs[1].Desired) / needs[1].Content;
        Require(Math.Abs(whitespace0 - whitespace1) < .01, "one-row and multi-row panels have equal relative whitespace after pixel rounding");
        Require(panels[0].Y + panels[0].Height == panels[1].Y && panels[1].Y + panels[1].Height == cell.Y + cell.Height, "exact contiguous boundaries");
        var gesture = state.Begin(new(ResizeEdges.None, 0), 0, 0);
        state.Drag(gesture, 0, 50); var manual = state.Panels().ToArray();
        state.Configure(cell, Profiles(2, 16), SplitDirection.Vertical);
        Require(state.Panels().SequenceEqual(manual), "polling preserves manual division");
        state.Configure(cell, Profiles(16, 2), SplitDirection.Vertical);
        Require(state.Panels()[0].Height > state.Panels()[1].Height, "count changes rebalance");
        var updated = state.Panels().ToArray(); state.Drag(gesture, 0, 70);
        Require(state.Panels().SequenceEqual(updated), "rebalancing invalidates old gestures");
        state.Configure(cell, Profiles(0, 100), SplitDirection.Vertical);
        Require(state.Panels()[0].Height >= Profiles(0)[0].Measure(cell.Width).Minimum, "empty panel keeps hint area while large neighbor scrolls");
        state.Configure(cell, Profiles(0, 0), SplitDirection.Vertical);
        Require(state.Panels().All(panel => panel.Height == 500), "all empty split equally");
        state.FooterHeight = 36; state.Reset(); state.Configure(cell, Profiles(2, 16), SplitDirection.Vertical);
        Require(state.Panels().Last().Y + state.Panels().Last().Height == state.Footer.Y, "update footer remains reserved");
        state.Configure(new(0, 0, 500, 278), Profiles(0, 1, 100), SplitDirection.Vertical);
        Require(state.Panels().All(panel => panel.Height > 0) && state.Panels().Sum(panel => panel.Height) == 200, "small area fits without extending desktop");
        state.Configure(cell, Profiles(1, 100), SplitDirection.Horizontal);
        Require(state.Panels().All(panel => panel.Width == 250), "horizontal setting unchanged");
        state.Configure(cell, Array.Empty<FavoriteLayoutProfile>(), SplitDirection.Vertical);
        Require(state.Panels().Count == 0, "zero drives");
        state.Configure(cell, Profiles(100), SplitDirection.Vertical);
        Require(state.Panels()[0].Height == 964, "single drive occupies available area");
        var sameRow = FavoritePanelLayout.Weights(500, 1000, Profiles(1, 4));
        Require(Math.Abs(sameRow[0] - sameRow[1]) < .001, "same rendered row count receives same height");
        var wide = Profiles(2, 16)[1].Measure(760).Desired;
        Require(wide < needs[1].Desired, "wider panels need fewer rows");
        var details = Profiles(2, 16).Select(profile => profile with { Details = true }).ToArray();
        Require(details[1].Measure(500).Desired > details[0].Measure(500).Desired, "details uses rows plus column headers");
        var withSearchStatus = details[0] with { ExtraHeaderHeight = 28 };
        Require(withSearchStatus.Measure(500).Desired - details[0].Measure(500).Desired == 28 * withSearchStatus.DpiScaleY
            && withSearchStatus.Measure(500).Content == details[0].Measure(500).Content,
            "search status height is reserved separately from result content");
        var exactColumns = Profiles(20)[0].Measure(480);
        Require(exactColumns.Content == 4 * 82 * 1.25, "five icon columns fit without reserving an unnecessary scrollbar");
        foreach (var profiles in new[] { Profiles(2, 16), Profiles(2, 20, 50), details, Profiles(0, 2, 16) })
        {
            var measured = profiles.Select(profile => profile.Measure(500)).ToArray();
            var height = measured.Sum(size => size.Desired) + 500;
            var weights = FavoritePanelLayout.Weights(500, height, profiles);
            var ratios = weights.Select((weight, i) => (weight * height - measured[i].Desired) / measured[i].Content).ToArray();
            Require(ratios.Max() - ratios.Min() < .00001, "relative whitespace stays equal across mixed counts/modes/empty panels");
        }
        var equal = new PanelLayoutState();
        equal.Configure(cell, Profiles(1, 100), SplitDirection.Vertical, responsive: false);
        var equalBounds = equal.Panels().ToArray();
        Require(equalBounds.All(rect => rect.Height == 500), "fixed mode ignores unequal content");
        equal.Configure(cell, Profiles(100, 1), SplitDirection.Vertical, responsive: false);
        Require(equal.Panels().SequenceEqual(equalBounds), "content changes do not affect fixed mode");
        Require(equal.HitTest(cell.X + 20, equalBounds[1].Y) is null, "fixed mode disables divider resize");
        Require(equal.HitTest(cell.X, cell.Y + 20)?.Edges == ResizeEdges.Left, "fixed mode keeps outer resize");
        var resize = equal.Begin(new(ResizeEdges.Right | ResizeEdges.Bottom), 0, 0);
        equal.Drag(resize, 100, 100);
        Require(Math.Abs(equal.Panels()[0].Height - equal.Panels()[1].Height) < 1, "outer resize keeps equal shares");
        equal.Configure(cell, Profiles(1, 100), SplitDirection.Vertical, responsive: true);
        Require(equal.Panels()[1].Height > equal.Panels()[0].Height, "switching to responsive recalculates proportions");
        Console.WriteLine("PASS model: rendered rows/width/DPI, chrome, spare height, overflow, empty/tiny area/footer/manual resize/reset/horizontal/zero/single.");
    }

    private static FavoriteLayoutProfile[] Profiles(params int[] counts) => counts.Select(count => new FavoriteLayoutProfile(count, false, 76, 82, 1.25, 1.25)).ToArray();

    private static async Task TestFolderLayout(App app, PanelLayoutState state, Dictionary<uint, DrivePanelWindow> panels,
        GlobalViewSettings views, string folder, string emptyFolder, int monitor)
    {
        var panel = panels[1];
        foreach (var mode in new[] { GlobalViewMode.Icon, GlobalViewMode.Details })
        {
            views.SetViewMode(mode); await Task.Delay(100);
            var homeHeights = state.Panels().Select(rect => rect.Height).ToArray();
            Require((bool)Call(panel, "NavigateTo", folder, true)!, "fixture folder opens");
            await Task.Delay(100);
            Require(panel.FavoriteCount == 2 && panel.LayoutItemCount == 33 && panel.FavoriteLayoutProfile.Count == 33,
                "folder allocation uses displayed folder entries, not its two favorites or hidden file");
            Require(state.Panels()[0].Height > homeHeights[0], "entering populated folder increases its panel height");
            CheckBounds(panel, state.Panels()[0]);
            views.ToggleHiddenItems(); await Task.Delay(100);
            Require(panel.LayoutItemCount == 34, "hidden setting updates allocation count");
            views.ToggleHiddenItems(); await Task.Delay(100);
            Require(panel.LayoutItemCount == 33, "hiding files updates allocation count");

            var filledHeight = state.Panels()[0].Height;
            Require((bool)Call(panel, "NavigateTo", emptyFolder, true)!, "nested empty folder opens");
            await Task.Delay(100);
            var emptyHeight = state.Panels()[0].Height;
            Require(panel.LayoutItemCount == 0 && emptyHeight < filledHeight, "empty folder shrinks without using favorites count");
            Require(((FrameworkElement)panel.FindName("EmptyFavoritesMessage")).Visibility == Visibility.Collapsed, "folder does not show favorites hint");
            if (monitor == 0 && mode == GlobalViewMode.Icon)
            {
                await Task.Delay(650); // Existing command-bar watcher follows the new directory.
                var paths = Enumerable.Range(0, 24).Select(i => Path.Combine(emptyFolder, $"Created item {i:00}.txt")).ToArray();
                foreach (var path in paths) File.WriteAllText(path, "fixture");
                await WaitFor(() => panel.LayoutItemCount == 24, "watcher refreshes added folder items");
                await Task.Delay(100);
                Require(state.Panels()[0].Height > emptyHeight, "watcher addition automatically increases height");
                foreach (var path in paths) File.Delete(path);
                await WaitFor(() => panel.LayoutItemCount == 0, "watcher refreshes deleted folder items");
                await Task.Delay(100);
                Require(Math.Abs(state.Panels()[0].Height - emptyHeight) < 1, "watcher deletion restores empty-folder height");
            }
            Click(panel, "BackButton"); await Task.Delay(100);
            Require(panel.LayoutItemCount == 33 && state.Panels()[0].Height > emptyHeight, "back navigation reallocates for parent content");
            Click(panel, "ForwardButton"); await Task.Delay(100);
            Require(panel.LayoutItemCount == 0 && Math.Abs(state.Panels()[0].Height - emptyHeight) < 1, "forward navigation restores empty allocation");
            Click(panel, "UpButton"); await Task.Delay(100);
            Require(panel.LayoutItemCount == 33, "up navigation updates visible count");
            Click(panel, "HomeButton"); await Task.Delay(100);
            Require(panel.LayoutItemCount == 2 && panel.FavoriteCount == 2, "home restores favorites-based allocation");
            Require(state.Panels().Select((rect, index) => Math.Abs(rect.Height - homeHeights[index])).All(delta => delta < 1),
                "home restores both panel proportions");
            Require(Field<AppConfig>(app, "_config").Drives["1"].Favorites.Count == 2,
                "folder navigation never changes saved favorites");
            Console.WriteLine($"PASS folder layout monitor {monitor}, {mode}: populated/empty folder, back/forward/up/home, hidden filtering, saved favorites unchanged.");
        }
    }

    private static void Click(DrivePanelWindow panel, string name) => ((Button)panel.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitFor(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Require(condition(), message);
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static void SaveScreenshot(App app, Dictionary<uint, DrivePanelWindow> panels, PanelLayoutState state, int monitor, string variant = "")
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var windows = new Window[] { Field<TopBarWindow>(app, "_topBar") }.Concat(panels.OrderBy(pair => pair.Key).Select(pair => pair.Value));
            var bounds = new[] { new LayoutCalculator().CalculateTopBar(state.Cell) }.Concat(state.Panels()).ToArray();
            foreach (var (window, rect) in windows.Zip(bounds))
            {
                window.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(window);
                var image = new RenderTargetBitmap((int)rect.Width, (int)rect.Height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                image.Render(window);
                drawing.DrawImage(image, new System.Windows.Rect(rect.X - state.Cell.X, rect.Y - state.Cell.Y, rect.Width, rect.Height));
            }
        }
        var output = new RenderTargetBitmap((int)state.Cell.Width, (int)state.Cell.Height, 96, 96, PixelFormats.Pbgra32);
        output.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output));
        var suffix = variant.Length == 0 ? "" : "-" + variant;
        var path = Path.Combine(AppContext.BaseDirectory, $"favorite-layout{suffix}-monitor{monitor}.png");
        using var stream = File.Create(path); encoder.Save(stream);
        Console.WriteLine("RENDER: " + path);
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    private static object? Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(value, args);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void CheckBounds(DrivePanelWindow panel, LayoutRect expected)
    {
        Require(GetWindowRect(new WindowInteropHelper(panel).Handle, out var rect), "native window exists");
        Require(rect.Top == expected.Y && rect.Bottom == expected.Y + expected.Height, "native height matches allocation");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
}
