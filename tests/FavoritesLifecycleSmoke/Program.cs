using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread] private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-FavoriteLifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "즐겨찾기 파일.txt"); File.WriteAllText(file, "favorite");
        var other = Path.Combine(root, "일반 파일.txt"); File.WriteAllText(other, "ordinary");
        var folder = Directory.CreateDirectory(Path.Combine(root, "즐겨찾기 폴더")).FullName;
        var nested = Path.Combine(folder, "하위 파일.txt"); File.WriteAllText(nested, "nested");
        var config = new DriveConfig { Favorites = [new() { RelativePath = Path.GetFileName(file), IsDirectory = false },
            new() { RelativePath = Path.GetFileName(folder), IsDirectory = true }, new() { RelativePath = Path.GetRelativePath(root, nested), IsDirectory = false },
            new() { RelativePath = "알 수 없는 이전 경로.txt", IsDirectory = false }] };
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var code = 0;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var panel = new DrivePanelWindow(root, "Favorite lifecycle test", new GlobalViewSettings(), new EjectService(), config) { Width = 600, Height = 550 };
            panel.Show();
            try
            {
                var favorites = Field<FavoriteCollection>(panel, "_favorites");
                Require(favorites.Items.Count == 3, "startup removes already-deleted paths on an accessible drive");
                var changes = 0; panel.SettingsChanged += (_, _) => changes++;
                Call(panel, "NavigateTo", root, true); await Layout(panel);
                var list = (ListBox)panel.FindName("IconItemsList");
                Require(list.Items.Cast<FileSystemEntry>().Single(item => item.FullPath == file).IsFavorite
                    && !list.Items.Cast<FileSystemEntry>().Single(item => item.FullPath == other).IsFavorite, "folder metadata marks only registered favorites");
                Require(Badges(list) == 2, "solid star renders on favorite file/folder icons");
                foreach (var badge in Descendants(list).OfType<SymbolIcon>().Where(icon => icon.Kind == "StarFill" && icon.IsVisible))
                {
                    var parent = (FrameworkElement)VisualTreeHelper.GetParent(badge);
                    var point = badge.TranslatePoint(new Point(), parent);
                    Require(Math.Abs(point.Y) < .1 && Math.Abs(point.X + badge.ActualWidth - parent.ActualWidth) < .1
                        && badge.ActualWidth < parent.ActualWidth / 2, "badge is a small solid star at the icon's top-right, not centered");
                }
                SavePreview(panel, "favorite-badges.png");
                favorites.Add(other, false); await Layout(panel); Require(Badges(list) == 3, "badge updates immediately after add without reload");
                favorites.Remove(favorites.Items.Single(item => item.Path == other)); await Layout(panel); Require(Badges(list) == 2, "badge updates immediately after remove");
                ((Button)panel.FindName("ViewModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout(panel);
                Require(Badges((DataGrid)panel.FindName("DetailsGrid")) == 2, "details icons use the same solid badge");
                Call(panel, "ShowFavorites", true); await Layout(panel);
                Require(Badges((DataGrid)panel.FindName("DetailsGrid")) == 0, "favorites details home never shows badges");
                ((Button)panel.FindName("ViewModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout(panel);
                Require(Badges((ListBox)panel.FindName("FavoriteItemsList")) == 0, "favorites icon home never shows badges");
                var renamed = Path.Combine(root, "이름 변경한 폴더"); Directory.Move(folder, renamed);
                await Wait(() => favorites.Items.Any(item => item.Path == renamed), "rename follows registered folder");
                Require(favorites.Items.Any(item => item.Path == Path.Combine(renamed, "하위 파일.txt")), "parent rename preserves nested favorite paths");
                Directory.Delete(renamed, true); // Only disposable fixture data.
                await Wait(() => favorites.Items.Count == 1, "external parent deletion removes parent and nested favorites");
                File.Delete(file);
                await Wait(() => favorites.Items.Count == 0, "external file deletion refreshes the active favorites home");
                Require(((ListBox)panel.FindName("FavoriteItemsList")).Items.Count == 0 && changes >= 3, "deletions propagate to UI and debounce-save events");
                var store = new ConfigStore(Path.Combine(root, "config.json")); var saved = new AppConfig(); saved.Drives["1"] = panel.CaptureSettings("fixture");
                Require(store.Save(saved) && store.Load().Drives["1"].Favorites.Count == 0, "deleted favorites stay removed after JSON roundtrip");
                var offline = Directory.CreateDirectory(Path.Combine(root, "offline-root")).FullName;
                var offlineFile = Path.Combine(offline, "keep.txt"); File.WriteAllText(offlineFile, "keep");
                var offlineFavorites = new FavoriteCollection(); offlineFavorites.Add(offlineFile, false);
                using (var watcher = new FavoriteFileMonitor(offline, offlineFavorites, panel.Dispatcher))
                {
                    Directory.Move(offline, offline + "-disconnected"); watcher.ConfirmDeleted([offlineFile]);
                    Require(offlineFavorites.Items.Count == 1, "unavailable drive never erases favorites");
                }
                await TestForeground(panel);
                Console.WriteLine("PASS: live solid badges in folder icons/details only; deletion/parent deletion/rename/save propagation; disconnected-root safety; async/nested foreground restoration; native owned popup promotion and unrelated-window isolation.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            finally { panel.Close(); app.Shutdown(); }
        }));
        try { app.Run(); } finally { Directory.Delete(root, true); }
        return code;
    }
    private static async Task TestForeground(Window owner)
    {
        var gate1 = new TaskCompletionSource<bool>(); var gate2 = new TaskCompletionSource<bool>();
        var first = DialogActivation.ShowAsync(owner, () => gate1.Task); var second = DialogActivation.ShowAsync(owner, () => gate2.Task);
        Require(!owner.Topmost, "async operation does not pin the panel above external programs");
        gate1.SetResult(true); await first; Require(!owner.Topmost, "overlapping operation leaves normal window ordering intact");
        gate2.SetResult(true); await second; Require(!owner.Topmost, "final operation restores owner state");
        try { await DialogActivation.ShowAsync(owner, () => Task.FromException<bool>(new IOException("fixture"))); }
        catch (IOException) { }
        Require(!owner.Topmost, "exception restores owner state");
        var closingOwner = new Window { Width = 200, Height = 100, ShowInTaskbar = false }; closingOwner.Show();
        var closingGate = new TaskCompletionSource<bool>(); var closingTask = DialogActivation.ShowAsync(closingOwner, () => closingGate.Task);
        closingOwner.Close(); closingGate.SetResult(true); await closingTask;
        var handle = new WindowInteropHelper(owner).Handle;
        NativeDialogForegroundScope? native = null, nestedNative = null; IntPtr popup = IntPtr.Zero, unrelated = IntPtr.Zero;
        try
        {
            await ShellActions.RunStaAsync(() =>
            {
                native = new NativeDialogForegroundScope(handle);
                nestedNative = new NativeDialogForegroundScope(handle);
                popup = CreateWindowExW(0, "STATIC", "Owned popup foreground fixture", 0x90C00000, 40, 40, 300, 120, handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                unrelated = CreateWindowExW(0, "STATIC", "Unrelated fixture", 0x90C00000, 360, 40, 200, 120, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            });
            Require(popup != IntPtr.Zero && unrelated != IntPtr.Zero, "native popup fixtures created");
            await Wait(() => (GetWindowLongPtrW(popup, -20).ToInt64() & 8) != 0, "owned native popup becomes topmost");
            Require((GetWindowLongPtrW(unrelated, -20).ToInt64() & 8) == 0, "unrelated windows remain untouched");
            await ShellActions.RunStaAsync(() => native!.Dispose());
            Require((GetWindowLongPtrW(popup, -20).ToInt64() & 8) != 0, "another active native foreground scope retains promotion");
            await ShellActions.RunStaAsync(() => nestedNative!.Dispose());
            Require((GetWindowLongPtrW(popup, -20).ToInt64() & 8) == 0, "native popup topmost flag restored");
        }
        finally { await ShellActions.RunStaAsync(() => { native?.Dispose(); nestedNative?.Dispose(); if (popup != IntPtr.Zero) DestroyWindow(popup); if (unrelated != IntPtr.Zero) DestroyWindow(unrelated); }); }
    }
    private static int Badges(DependencyObject root) => Descendants(root).OfType<SymbolIcon>().Count(icon => icon.Kind == "StarFill" && icon.IsVisible);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
    private static async Task Layout(Window window) => await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    private static async Task Wait(Func<bool> condition, string message)
    {
        var until = DateTime.UtcNow.AddSeconds(5); while (!condition() && DateTime.UtcNow < until) await Task.Delay(40);
        Require(condition(), message);
    }
    private static void SavePreview(Window window, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint style, string type, string name, uint flags, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr module, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
}
