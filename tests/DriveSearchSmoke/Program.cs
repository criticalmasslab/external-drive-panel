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
        var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Backend(root); await Windows(root); Console.WriteLine("PASS drive search: backend, cancellation, responsive toolbar, both views, navigation, shortcuts, stale results and cleanup."); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        }));
        app.Run(); Directory.Delete(root, true); return result;
    }

    private static async Task Backend(string temporary)
    {
        Require(DriveSearchService.Matches("한글 보고서.PDF", false, "보고서"), "partial Unicode name");
        foreach (var query in new[] { "*.pdf", ".pdf", "ext:PDF", "ext:.pdf", "ext:*.pdf" })
            Require(DriveSearchService.Matches("보고서.PDF", false, query), "extension query: " + query);
        Require(DriveSearchService.Matches("my photo.jpg", false, "my photo"), "spaces preserved in name query");
        Require(DriveSearchService.Matches("a1.txt", false, "a?.txt"), "question wildcard");
        Require(!DriveSearchService.Matches("looks.pdf", true, ".pdf") && !DriveSearchService.Matches("x.txt", false, "ext:"), "extensions only match files and need a value");
        Require(!DriveSearchService.Matches("anything", false, " "), "blank query does not traverse everything");
        var root = Directory.CreateDirectory(Path.Combine(temporary, "drive")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(root, "하위 폴더")).FullName;
        File.WriteAllText(Path.Combine(root, "보고서.PDF"), "search fixture");
        File.WriteAllText(Path.Combine(nested, "report.pdf"), "search fixture");
        File.WriteAllText(Path.Combine(nested, "photo.JPG"), "search fixture");
        File.WriteAllText(Path.Combine(nested, "readme.txt"), "search fixture");
        var hidden = Path.Combine(root, "hidden.pdf"); File.WriteAllText(hidden, "hidden"); File.SetAttributes(hidden, FileAttributes.Hidden);
        var found = await DriveSearchService.SearchAsync(root, "*.pdf", false);
        Require(found.Items.Count == 2 && found.Items.All(item => item.FullPath.StartsWith(root)), "whole-root recursive search without hidden files");
        Require((await DriveSearchService.SearchAsync(root, ".pdf", true)).Items.Count == 3, "hidden visibility honored");
        Require((await DriveSearchService.SearchAsync(root, "하위", false)).Items.Single().IsDirectory, "directory name search");
        Require((await DriveSearchService.SearchAsync(root, "not found", false)).Items.Count == 0, "no matches");
        var outside = Directory.CreateDirectory(Path.Combine(temporary, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "outside.pdf"), "must not be followed");
        try
        {
            var link = Path.Combine(root, "outside link"); Directory.CreateSymbolicLink(link, outside);
            Require((await DriveSearchService.SearchAsync(root, "outside.pdf", true)).Items.Count == 0, "junction/symlink targets are not searched");
            Directory.Delete(link);
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("NOTE symbolic link fixture unavailable without privilege."); }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 1314) { Console.WriteLine("NOTE symbolic link fixture unavailable without privilege."); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await DriveSearchService.SearchAsync(root, "*", true, cancellationToken: cancelled.Token); throw new Exception("cancel ignored"); }
        catch (OperationCanceledException) { }
        var many = Directory.CreateDirectory(Path.Combine(temporary, "many")).FullName;
        for (var i = 0; i <= DriveSearchService.MaximumResults; i++) File.WriteAllText(Path.Combine(many, $"item{i:00000}.txt"), "");
        var limited = await DriveSearchService.SearchAsync(many, "*.txt", false);
        Require(limited.Limited && limited.Items.Count == DriveSearchService.MaximumResults, "bounded results report truncation");
        Console.WriteLine("PASS backend: names, extensions, wildcards, Unicode, hidden entries, recursive scope, cancellation, 5,000 result limit. Link test is conditional on OS privilege.");
    }

    private static async Task Windows(string temporary)
    {
        var root = Path.Combine(temporary, "drive");
        foreach (var monitor in new MonitorService().GetMonitors())
        {
            var settings = new GlobalViewSettings();
            var panel = new DrivePanelWindow(root, "Search smoke", settings, new EjectService());
            panel.SetPlacement(new(monitor.Left + 20, monitor.Top + 50, 480, 600)); panel.Show();
            try
            {
                double previous = 0;
                foreach (var width in new[] { 300, 360, 480, 760 })
                {
                    panel.SetPlacement(new(monitor.Left + 20, monitor.Top + 50, width, 600)); await Layout(panel);
                    var search = Bounds(panel, "SearchBoxBorder"); var delete = Bounds(panel, "DeleteButton");
                    Require(search.Left >= delete.Right && search.Right <= panel.ActualWidth && search.Width > 0, "search consumes remaining width without covering actions");
                    if (width == 760) Require(search.Width > previous, "search expands in wider panel");
                    ((TextBox)panel.FindName("SearchTextBox")).Text = ".pdf"; await Layout(panel);
                    if (width >= 480) Require(((TextBox)panel.FindName("SearchTextBox")).ActualWidth * VisualTreeHelper.GetDpi(panel).DpiScaleX >= 30,
                        "search input uses available width after full-sized toolbar buttons");
                    ((TextBox)panel.FindName("SearchTextBox")).Clear();
                    Require(Math.Abs((search.Top + search.Bottom) / 2 - (delete.Top + delete.Bottom) / 2) < 1, "search and toolbar buttons vertically aligned");
                    previous = search.Width;
                }
                panel.SetPlacement(new(monitor.Left + 20, monitor.Top + 50, 480, 600));
                await panel.SearchDriveAsync("*.pdf"); await Layout(panel);
                var icons = (ListBox)panel.FindName("IconItemsList"); var grid = (DataGrid)panel.FindName("DetailsGrid");
                Require(icons.IsVisible && icons.Items.Count == 2, "search from home uses existing icon view");
                Require(!((Button)panel.FindName("NewItemButton")).IsEnabled && !((Button)panel.FindName("PasteButton")).IsEnabled,
                    "search results have no implicit paste/create destination");
                Require(!((TextBlock)panel.FindName("EmptyFavoritesMessage")).IsVisible, "empty home hint never overlays search");
                Click(panel, "ViewModeButton"); await Layout(panel);
                Require(grid.IsVisible && grid.Items.Count == 2 && ((DataGridColumn)panel.FindName("SearchLocationColumn")).Visibility == Visibility.Visible, "details search shares entries and adds location");
                grid.SelectedItem = grid.Items[0]; await Layout(panel);
                Require(((StackPanel)panel.FindName("BreadcrumbPanel")).Children.OfType<TextBlock>().Single().Text.EndsWith(".PDF"), "selection shows full real path");
                settings.Restore(new GlobalViewConfig { IconSize = 24, ShowPreviews = true }); panel.ApplyGlobalViewSettings(); await Layout(panel);
                Require(grid.Items.Count == 2, "icon/preview options preserve search results");
                settings.Restore(new GlobalViewConfig { ShowHidden = true }); panel.ApplyGlobalViewSettings();
                await Task.WhenAll(((HashSet<Task>)Field(panel, "_searchTasks")!).ToArray()); await Layout(panel);
                Require(grid.Items.Count == 3, "hidden option restarts active search with updated visibility");
                settings.Restore(new GlobalViewConfig { ShowHidden = false }); panel.ApplyGlobalViewSettings();
                await Task.WhenAll(((HashSet<Task>)Field(panel, "_searchTasks")!).ToArray()); await Layout(panel);
                Require(grid.Items.Count == 2, "turning hidden off removes hidden search entries");
                Save(panel, "search-details.png");
                Click(panel, "SearchClearButton"); await Layout(panel);
                Require(!((Border)panel.FindName("SearchStatus")).IsVisible && ((TextBlock)panel.FindName("EmptyFavoritesMessage")).IsVisible, "clear returns to original home");
                Require(((Button)panel.FindName("NewItemButton")).IsEnabled, "normal creation destination restores after search");
                Call(panel, "NavigateTo", Path.Combine(root, "하위 폴더"), true);
                await panel.SearchDriveAsync("*.pdf"); Click(panel, "BackButton"); await Layout(panel);
                Require((string)Field(panel, "_currentPath")! == Path.Combine(root, "하위 폴더") && !((Border)panel.FindName("SearchStatus")).IsVisible, "back returns to folder where search began");
                var old = panel.SearchDriveAsync("*"); Click(panel, "HomeButton"); await old; await Layout(panel);
                Require(!((Border)panel.FindName("SearchStatus")).IsVisible && !((ListBox)panel.FindName("IconItemsList")).IsVisible, "late result cannot overwrite navigation");
                var first = panel.SearchDriveAsync("*.pdf"); var second = panel.SearchDriveAsync("*.jpg"); await Task.WhenAll(first, second); await Layout(panel);
                Require(grid.Items.Count == 1 && ((FileSystemEntry)grid.Items[0]).Name == "photo.JPG", "new query wins over old completion");
                ((TextBox)panel.FindName("SearchTextBox")).Clear(); await Layout(panel);
                Require(!((Border)panel.FindName("SearchStatus")).IsVisible, "clearing text exits search");
                var input = (TextBox)panel.FindName("SearchTextBox"); input.Text = ".pdf"; panel.Activate(); input.Focus();
                var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, System.Windows.Input.Key.Enter)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                input.RaiseEvent(key); Require(key.Handled, "Enter starts search rather than opening selected file");
                await Task.WhenAll(((HashSet<Task>)Field(panel, "_searchTasks")!).ToArray()); await Layout(panel);
                Require(grid.Items.Count == 2, "Enter searches the whole drive");
                var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                panel.RaiseEvent(escape); await Layout(panel);
                Require(escape.Handled && !((Border)panel.FindName("SearchStatus")).IsVisible, "Escape exits search from the panel");
                var pending = panel.SearchDriveAsync("*"); panel.Close(); await pending;
                Require(!panel.IsVisible, "closing cancels traversal and ignores late results");
                Console.WriteLine($"PASS WPF DPI {VisualTreeHelper.GetDpi(panel).DpiScaleX}: toolbar widths 300/360/480/760, icons/details, location, navigation, Enter and stale completion.");
            }
            finally { panel.Close(); }
        }
    }
    private static Task Layout(Window window) => window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle).Task;
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Click(Window panel, string name) => ((Button)panel.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Rect Bounds(Window panel, string name) { var element = (FrameworkElement)panel.FindName(name); return new(element.TranslatePoint(new(), panel), element.RenderSize); }
    private static void Require(bool condition, string description) { if (!condition) throw new Exception(description); }
    private static void Save(Window window, string filename)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Round(window.ActualWidth * dpi.DpiScaleX), (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, filename)); png.Save(stream);
    }
}
