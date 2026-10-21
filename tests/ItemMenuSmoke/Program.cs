using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--interactive")) return Interactive();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 0;
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Menu-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { await Test(directory); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); Directory.Delete(directory, true); }
        }));
        app.Run();
        return result;
    }

    // Manual desktop smoke fixture: no real config, registrations, or user files.
    private static int Interactive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Menu-UI-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "테스트 폴더"));
        File.WriteAllText(Path.Combine(directory, "메뉴 테스트.txt"), "Isolated shell menu smoke fixture.");
        var app = new Application();
        var state = new DriveConfig { Favorites = [new FavoriteConfig { RelativePath = "테스트 폴더", IsDirectory = true },
            new FavoriteConfig { RelativePath = "메뉴 테스트.txt", IsDirectory = false }] };
        var panel = new DrivePanelWindow(directory, "Item menu desktop smoke", new GlobalViewSettings(), new EjectService(), state);
        panel.Width = 760; panel.Height = 620;
        panel.ShowInTaskbar = true;
        panel.Title = "Item menu desktop smoke";
        panel.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        panel.NotificationRequested += (message, error) => Console.WriteLine($"Notification ({error}): {message}");
        panel.Show();
        app.Run();
        Directory.Delete(directory, true);
        return 0;
    }

    private static async Task Test(string directory)
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, "folder")).FullName;
        var file = Path.Combine(directory, "file.txt");
        File.WriteAllText(file, "menu test");
        var state = new DriveConfig { Favorites = [new FavoriteConfig { RelativePath = "folder", IsDirectory = true },
            new FavoriteConfig { RelativePath = "file.txt", IsDirectory = false }, new FavoriteConfig { RelativePath = "missing.txt", IsDirectory = false }] };
        var view = new GlobalViewSettings();
        var panel = new DrivePanelWindow(directory, "Menus", view, new EjectService(), state);
        panel.Width = 700; panel.Height = 600;
        panel.Show();
        await panel.Dispatcher.InvokeAsync(() => panel.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        var menus = Field<ItemContextMenuBuilder>(panel, "_itemMenus");
        var favorites = Field<FavoriteCollection>(panel, "_favorites");
        var favoriteList = Control<ListBox>(panel, "FavoriteItemsList");
        var grid = Control<DataGrid>(panel, "DetailsGrid");
        var icons = Control<ListBox>(panel, "IconItemsList");
        foreach (var details in new[] { false, true })
        {
            Call(panel, "ShowFavorites", true);
            Click(panel, details ? "DetailsViewButton" : "IconViewButton");
            Selector list = details ? grid : favoriteList;
            list.SelectedIndex = 0;
            list.UpdateLayout();
            var container = (UIElement)list.ItemContainerGenerator.ContainerFromIndex(1);
            RightClick(container);
            Require(ItemMenuTarget.FromItem(list.SelectedItem)?.Path == file && Address(panel) == file,
                "right click moves selection and address in both home modes");
            menus.Populate(list.ContextMenu, ItemMenuTarget.FromItem(list.SelectedItem)!);
            Require(Menu(list, ItemMenuCommand.ToggleFavorite).Header.Equals("즐겨찾기에서 제거")
                && Menu(list, ItemMenuCommand.OpenWith).IsEnabled && Menu(list, ItemMenuCommand.EditText).IsEnabled, "shared home file menu");
            var ghost = ItemMenuTarget.FromItem(list.Items[2])!;
            menus.Populate(list.ContextMenu, ghost);
            Require(list.ContextMenu.Items.OfType<MenuItem>().Where(item => item.IsEnabled).Select(item => (ItemMenuCommand)item.Tag)
                .Order().SequenceEqual(new[] { ItemMenuCommand.ToggleFavorite, ItemMenuCommand.CopyPath }.Order()), "missing favorite enables only remove/copy-path");
        }
        foreach (var details in new[] { false, true })
        {
            Call(panel, "NavigateTo", directory, true);
            Click(panel, details ? "DetailsViewButton" : "IconViewButton");
            Selector list = details ? grid : icons;
            list.UpdateLayout();
            var index = list.Items.Cast<FileSystemEntry>().ToList().FindIndex(item => item.FullPath == file);
            RightClick((UIElement)list.ItemContainerGenerator.ContainerFromIndex(index));
            Require(ItemMenuTarget.FromItem(list.SelectedItem)?.Path == file && Address(panel) == file, "folder mode right-click selects and updates address");
            menus.Populate(list.ContextMenu, new(file, false));
            Require(Menu(list, ItemMenuCommand.ToggleFavorite).Header.Equals("즐겨찾기에서 제거"), "folder item already favorite offers removal");
            await menus.ExecuteAsync(ItemMenuCommand.ToggleFavorite, new(file, false));
            menus.Populate(list.ContextMenu, new(file, false));
            Require(Menu(list, ItemMenuCommand.ToggleFavorite).Header.Equals("즐겨찾기에 추가"), "removal reflected on rebuild");
            await menus.ExecuteAsync(ItemMenuCommand.ToggleFavorite, new(file, false));
            Require(favorites.Items.Count == 3, "add preserves original favorite behavior");
        }
        Require(ItemContextMenuBuilder.Shortcut(Key.Enter, Key.None, ModifierKeys.None) == ItemMenuCommand.Open
            && ItemContextMenuBuilder.Shortcut(Key.System, Key.Enter, ModifierKeys.Alt) == ItemMenuCommand.Properties
            && ItemContextMenuBuilder.Shortcut(Key.C, Key.None, ModifierKeys.Control | ModifierKeys.Shift) == ItemMenuCommand.CopyPath
            && ItemContextMenuBuilder.Shortcut(Key.C, Key.None, ModifierKeys.Control) is null, "shortcut routing including Key.System");
        var folderItem = grid.Items.Cast<FileSystemEntry>().Single(entry => entry.FullPath == folder);
        grid.SelectedItem = folderItem;
        grid.Focus();
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(key);
        Require(key.Handled && Field<string>(panel, "_currentPath") == folder, "Enter preview suppresses DataGrid default and opens inside panel");
        await menus.ExecuteAsync(ItemMenuCommand.CopyPath, new(file, false));
        Require(Clipboard.GetText() == ShellActions.QuotedPath(file), "clipboard contains quoted absolute path");
        Call(panel, "NavigateTo", directory, true);
        var notices = new List<(string Message, bool Error)>();
        panel.NotificationRequested += (message, error) => notices.Add((message, error));
        await menus.ExecuteAsync(ItemMenuCommand.Zip, new(file, false));
        Require(notices.Count == 2 && notices[0].Message == "압축 중…" && notices[1].Message.Contains("압축 완료")
            && !notices[1].Error && grid.Items.Cast<FileSystemEntry>().Any(entry => entry.Name == "file.zip"), "ZIP start/completion notifications and current folder refresh");
        var locked = Path.Combine(directory, "locked.txt");
        File.WriteAllText(locked, "locked");
        using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await menus.ExecuteAsync(ItemMenuCommand.Zip, new(locked, false));
        Require(notices.Last().Error && notices.Last().Message.Contains("압축 실패") && !File.Exists(Path.Combine(directory, "locked.zip")), "ZIP failure notification and cleanup");
        panel.Close();
        Console.WriteLine("PASS WPF: common menus across all four views, right-click selection/address, missing favorites, add/remove, Enter/default handling, system-key shortcut mapping, ZIP notifications/refresh/failure.");
    }

    private static void RightClick(UIElement element) => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = element });
    private static MenuItem Menu(Selector list, ItemMenuCommand command) => list.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, command));
    private static string Address(DrivePanelWindow panel) => Control<StackPanel>(panel, "BreadcrumbPanel").Children.OfType<TextBlock>().Single().Text;
    private static T Control<T>(FrameworkElement window, string name) => (T)window.FindName(name);
    private static void Click(FrameworkElement window, string name) => Control<ToggleButton>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
