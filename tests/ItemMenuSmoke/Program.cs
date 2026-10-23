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
            try { await Test(directory, !args.Contains("--no-clipboard")); }
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

    private static async Task Test(string directory, bool testClipboard)
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
            SetViewMode(panel, details);
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
            var ghost = new ItemMenuTarget(Path.Combine(directory, "missing.txt"), false);
            menus.Populate(list.ContextMenu, ghost);
            Require(list.ContextMenu.Items.OfType<MenuItem>().Where(item => item.IsEnabled).Select(item => (ItemMenuCommand)item.Tag)
                .Order().SequenceEqual(new[] { ItemMenuCommand.ToggleFavorite, ItemMenuCommand.CopyPath }.Order()), "missing favorite enables only remove/copy-path");
        }
        foreach (var details in new[] { false, true })
        {
            Call(panel, "NavigateTo", directory, true);
            SetViewMode(panel, details);
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
            Require(favorites.Items.Count == 2, "add preserves original favorite behavior without deleted paths");
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
        if (testClipboard)
        {
            await menus.ExecuteAsync(ItemMenuCommand.CopyPath, new(file, false));
            Require(Clipboard.GetText() == ShellActions.QuotedPath(file), "clipboard contains quoted absolute path");
        }
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
        var pair = new ItemMenuTarget[] { new(folder, true), new(file, false) };
        foreach (var home in new[] { true, false })
        foreach (var details in new[] { false, true })
        {
            if (home) Call(panel, "ShowFavorites", true); else Call(panel, "NavigateTo", directory, true);
            SetViewMode(panel, details);
            Selector list = details ? grid : home ? favoriteList : icons;
            list.UpdateLayout();
            var selected = ItemContextMenuBuilder.SelectedItems(list);
            selected.Clear();
            foreach (var item in list.Items.Cast<object>().Where(item => pair.Any(target => target.Path == ItemMenuTarget.FromItem(item)?.Path))) selected.Add(item);
            Require(selected.Count == 2, "all four views allow multiple full-item selections");
            var selectedIndex = list.Items.IndexOf(selected[1]);
            RightClick((UIElement)list.ItemContainerGenerator.ContainerFromIndex(selectedIndex));
            Require(selected.Count == 2, "right-click on selected item preserves all selections");
            menus.PopulateSelection(list.ContextMenu, ItemContextMenuBuilder.SelectedTargets(list));
            Require(!list.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, ItemMenuCommand.OpenWith) || Equals(item.Tag, ItemMenuCommand.Explore)), "mixed selection shows only common actions");
            SetViewMode(panel, !details);
            Selector changed = details ? home ? favoriteList : icons : grid;
            Require(ItemContextMenuBuilder.SelectedTargets(changed).Count == 2, "view switch preserves multiple selection");
        }
        if (testClipboard)
        {
            await menus.ExecuteSelectionAsync(ItemMenuCommand.CopyPath, pair);
            Require(Clipboard.GetText() == string.Join(Environment.NewLine, pair.Select(target => ShellActions.QuotedPath(target.Path))), "batch path copy preserves every quoted path");
        }
        await menus.ExecuteSelectionAsync(ItemMenuCommand.ToggleFavorite, pair);
        Require(!favorites.Items.Any(item => pair.Any(target => target.Path == item.Path)), "batch remove applies to every favorite");
        // One already-favorite plus one new item must add all, not toggle existing ones off.
        favorites.Add(folder, true);
        await menus.ExecuteSelectionAsync(ItemMenuCommand.ToggleFavorite, pair);
        Require(favorites.Items.Count(item => pair.Any(target => target.Path == item.Path)) == 2, "mixed favorite batch adds without removing existing favorites");
        Call(panel, "NavigateTo", directory, true);
        SetViewMode(panel, true);
        grid.SelectedItems.Clear();
        foreach (var item in grid.Items.Cast<FileSystemEntry>().Where(item => pair.Any(target => target.Path == item.FullPath))) grid.SelectedItems.Add(item);
        favorites.Remove(favorites.Items.Single(item => item.Path == file));
        favorites.Remove(favorites.Items.Single(item => item.Path == folder));
        Control<Button>(panel, "AddFavoritesButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(favorites.Items.Count(item => pair.Any(target => target.Path == item.Path)) == 2, "toolbar adds all selected items");
        grid.SelectedItems.Clear();
        Control<Button>(panel, "AddFavoritesButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(favorites.Items.Any(item => item.Path == Path.TrimEndingDirectorySeparator(directory)), "toolbar without selection adds current folder");
        await menus.ExecuteSelectionAsync(ItemMenuCommand.Zip, pair);
        Require(File.Exists(Path.Combine(directory, "folder.zip")) && File.Exists(Path.Combine(directory, "file (2).zip")), "batch ZIP processes every selected file and folder");
        var firstSet = new object[] { "a", "b" };
        Require(SelectionPolicy.Combine(firstSet, new object[] { "b", "c" }, ModifierKeys.None).SetEquals(new object[] { "b", "c" })
            && SelectionPolicy.Combine(firstSet, new object[] { "b", "c" }, ModifierKeys.Control).SetEquals(new object[] { "a", "c" })
            && SelectionPolicy.Combine(firstSet, new object[] { "b", "c" }, ModifierKeys.Shift).SetEquals(new object[] { "a", "b", "c" }), "rectangle replace/control toggle/shift extend policy");
        panel.Close();
        if (!testClipboard) Console.WriteLine("NOTE clipboard mutation assertions skipped; existing clipboard left untouched.");
        Console.WriteLine("PASS WPF: four-view multi-selection/right-click preservation/mode switch, common batch menus, all-item favorite add/remove/toolbar/ZIP, rectangle modifiers, plus single-item regressions. Clipboard checks: " + testClipboard);
    }

    private static void RightClick(UIElement element) => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = element });
    private static MenuItem Menu(Selector list, ItemMenuCommand command) => list.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, command));
    private static string Address(DrivePanelWindow panel) => Control<StackPanel>(panel, "BreadcrumbPanel").Children.OfType<TextBlock>().Single().Text;
    private static T Control<T>(FrameworkElement window, string name) => (T)window.FindName(name);
    private static void SetViewMode(DrivePanelWindow panel, bool details)
    {
        if ((panel.EffectiveViewMode == GlobalViewMode.Details) != details)
            Control<Button>(panel, "ViewModeButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
