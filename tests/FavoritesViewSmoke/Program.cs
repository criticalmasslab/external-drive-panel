using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-FavoritesView-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var root = Directory.CreateDirectory(Path.Combine(directory, "drive1")).FullName + Path.DirectorySeparatorChar;
        var root2 = Directory.CreateDirectory(Path.Combine(directory, "drive2")).FullName + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(Path.Combine(root, "Zeta"));
        File.WriteAllText(Path.Combine(root, "Alpha.txt"), "abc");
        var config = new AppConfig { NotifyNewDrive = false, DriveOrder = ["1", "2", "3"] };
        config.Drives["1"] = new DriveConfig { Registered = true, ViewMode = GlobalViewMode.Details, SortKey = "Size", SortReverse = true,
            Favorites = [new FavoriteConfig { RelativePath = "Zeta", DisplayName = "Zeta", IsDirectory = true },
                new FavoriteConfig { RelativePath = "Alpha.txt", DisplayName = "Alpha", IsDirectory = false },
                new FavoriteConfig { RelativePath = "Missing", DisplayName = "Missing", IsDirectory = true }] };
        config.Drives["2"] = new DriveConfig { Registered = true };
        config.Drives["3"] = new DriveConfig { Registered = true, ViewMode = GlobalViewMode.Details };
        var store = new ConfigStore(Path.Combine(directory, "config.json"));
        if (!store.Save(config)) throw new Exception("Fixture save failed");
        var app = new App(store, () => [new(1, root, "One", DriveType.Removable), new(2, root2, "Two", DriveType.Removable)]);
        var result = 0;
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try { await Test(app, store, root, root2, directory); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { Call(app, "ExitApplication"); }
        }));
        app.Run();
        Directory.Delete(directory, true);
        return result;
    }

    private static async Task Test(App app, ConfigStore store, string root, string root2, string directory)
    {
        var panels = Field<Dictionary<uint, DrivePanelWindow>>(app, "_panels");
        var first = panels[1];
        var second = panels[2];
        var config = Field<AppConfig>(app, "_config");
        var global = Field<GlobalViewSettings>(app, "_viewSettings");
        var top = Field<TopBarWindow>(app, "_topBar");
        top.UpdateLayout();
        foreach (var name in new[] { "IconViewButton", "DetailsViewButton" })
        {
            var button = Control<ToggleButton>(top, name);
            var origin = button.TranslatePoint(new Point(), top);
            Require(origin.Y >= 0 && origin.Y + button.ActualHeight <= top.ActualHeight + 1,
                "top bar mode buttons fit the DPI-scaled window height");
        }
        var grid = Control<DataGrid>(first, "DetailsGrid");
        var message = Control<TextBlock>(first, "EmptyFavoritesMessage");
        var favorites = Field<FavoriteCollection>(first, "_favorites");
        // Fixture roots are long temporary directory paths. Use real-drive-sized captions
        // when checking the actual toolbar layout and recording visual previews.
        Control<Button>(first, "DriveRootButton").Content = "D:";
        Control<TextBlock>(first, "TitleText").Text = "D: One";
        first.UpdateLayout();
        Require(!ToolBar.GetIsOverflowItem(Control<ToggleButton>(first, "IconViewButton"))
            && !ToolBar.GetIsOverflowItem(Control<ToggleButton>(first, "DetailsViewButton")), "both panel mode buttons fit the normal toolbar");
        Require(first.EffectiveViewMode == GlobalViewMode.Details && second.EffectiveViewMode == GlobalViewMode.Icon, "initial independent modes");
        Require(grid.Visibility == Visibility.Visible && !grid.CanUserSortColumns && grid.Items.Count == 3, "favorites use the shared details grid without sorting");
        Require(grid.Items.Cast<FavoriteDetailsRow>().Select(row => row.Name).SequenceEqual(new[] { "Zeta", "Alpha", "Missing" }), "favorite insertion order");
        Require(((FavoriteDetailsRow)grid.Items[2]).Type == "(경로 없음)", "missing row rendered");
        SavePreview(first);
        Require(Control<MenuItem>(first, "DetailsRemoveFavoriteMenuItem").Visibility == Visibility.Visible
            && Control<MenuItem>(first, "DetailsAddFavoriteMenuItem").Visibility == Visibility.Collapsed, "home context menu removes");
        Require(!Control<Button>(first, "UpButton").IsEnabled && Control<ToggleButton>(first, "DetailsViewButton").IsEnabled, "home navigation/view buttons");

        grid.SelectedItem = grid.Items[0];
        Require(Address(first) == Path.Combine(root, "Zeta"), "details single selection displays absolute path");
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = grid });
        Require(grid.SelectedItem is null && Address(first) == "즐겨찾기", "details blank click clears selection and address");
        Call(first, "DetailsGrid_OnSorting", grid, new DataGridSortingEventArgs(grid.Columns[0]));
        Require(first.CaptureSettings("").SortKey == "Size" && first.CaptureSettings("").SortReverse, "home header click cannot alter folder sort");

        grid.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
        Require(row is not null && row.ActualHeight > 0, "actual details row rendered");
        var target = DragDropService.FindTarget(row).Target;
        Require(target == new DropTarget(Path.Combine(root, "Zeta"), true), "favorite details row is a folder drop target");
        var external = Path.Combine(directory, "external.txt");
        File.WriteAllText(external, "external");
        var decorator = (AdornerDecorator)VisualTreeHelper.GetParent(Control<Grid>(first, "ContentArea"));
        var before = Render(decorator);
        var rowText = Descendants(row!).OfType<TextBlock>().First(text => text.Text.Contains("Zeta"));
        Require(DragDropService.FindTarget(rowText).Target == target, "inner cell text resolves to favorite folder row");
        Drag(rowText, external, DragDrop.PreviewDragOverEvent);
        Require(AdornerLayer.GetAdornerLayer(row!)?.GetAdorners(row!)?.Any(a => a.GetType().Name == "DropTargetAdorner") == true, "yellow adorner above favorite details row");
        var after = Render(decorator);
        Require(!before.SequenceEqual(after), "drop highlight changes rendered pixels");
        Drag(row!, external, DragDrop.PreviewDragLeaveEvent);
        Require(!DragDropService.GetIsDropTarget(row!), "drop highlight removed on leave");
        grid.SelectedItem = grid.Items[0];
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent, Source = row });
        Require(Field<string>(first, "_currentPath") == Path.Combine(root, "Zeta") && grid.CanUserSortColumns, "favorite folder double click navigates with folder sorting restored");
        favorites.Items.Clear();
        Require(message.Visibility == Visibility.Collapsed, "empty favorites message never overlays folder details");
        Click(first, "IconViewButton");
        Require(message.Visibility == Visibility.Collapsed, "empty favorites message never overlays folder icons");
        Call(first, "HomeButton_OnClick", first, new RoutedEventArgs());
        Require(message.Visibility == Visibility.Visible && !message.IsHitTestVisible && Panel.GetZIndex(message) > Panel.GetZIndex(grid),
            "empty home hint is drawn above list controls without intercepting input");
        Click(first, "DetailsViewButton");
        Require(grid.Visibility == Visibility.Visible && message.Visibility == Visibility.Visible, "empty home details show hint");

        var drag = Field<DragDropService>(first, "_dragDrop");
        Require(Field<Func<string?>>(drag, "_currentFolder")() is null, "home blank drop has no folder fallback");
        await drag.ProcessDropAsync([Path.Combine(root, "Zeta"), Path.Combine(root, "Alpha.txt")], null);
        Require(favorites.Items.Count == 2 && grid.Items.Count == 2 && message.Visibility == Visibility.Collapsed, "same-drive drop updates favorite details and hides hint");
        grid.SelectedItem = grid.Items[1];
        Control<MenuItem>(first, "DetailsRemoveFavoriteMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(favorites.Items.Count == 1 && grid.Items.Count == 1, "details remove menu removes selected favorite");

        Click(second, "DetailsViewButton");
        Click(first, "IconViewButton");
        Require(first.EffectiveViewMode == GlobalViewMode.Icon && second.EffectiveViewMode == GlobalViewMode.Details
            && global.ViewMode == GlobalViewMode.Icon, "panel clicks change only their own drive");
        Require(config.Drives["1"].ViewMode == GlobalViewMode.Icon && config.Drives["2"].ViewMode == GlobalViewMode.Details, "panel changes captured for debounce save");
        global.IncreaseIconSize(); global.ToggleHiddenItems();
        Require(first.EffectiveViewMode == GlobalViewMode.Icon && second.EffectiveViewMode == GlobalViewMode.Details, "shared size/hidden preserve individual modes");
        Require(Control<ToggleButton>(top, "IconViewButton").IsChecked == true && Control<ToggleButton>(second, "DetailsViewButton").IsChecked == true,
            "top bar highlights global default while panel highlights its actual mode");
        Click(top, "IconViewButton"); // Same global value: must still clear all registered overrides.
        Require(config.Drives.Values.All(drive => drive.ViewMode is null) && panels.Values.All(panel => panel.EffectiveViewMode == GlobalViewMode.Icon),
            "same-mode global click clears connected AND offline overrides and updates visible panels");
        Click(first, "DetailsViewButton");
        await Task.Delay(650);
        var loaded = store.Load();
        Require(loaded.Drives["1"].ViewMode == GlobalViewMode.Details && loaded.Drives["2"].ViewMode is null && loaded.Drives["3"].ViewMode is null,
            "debounced config persists the latest local override, omits global inheritance");
        var co = Field<DriveCoordinator>(app, "_coordinator");
        co.ToggleHidden("1"); Call(app, "RegistrationChanged");
        co.ToggleHidden("1"); Call(app, "RegistrationChanged");
        Require(panels[1].EffectiveViewMode == GlobalViewMode.Details, "hide/unhide restores saved individual mode");
        Console.WriteLine("PASS WPF: shared favorites detail rendering/order, selection/blank/double-click/remove, adorner pixel rendering, folder hint isolation, independent/local/global buttons, offline override clearing, debounce, hide/unhide restoration.");
    }

    private static byte[] Render(FrameworkElement element)
    {
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static void SavePreview(FrameworkElement element)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "favorites-details-smoke.png"));
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Drag(UIElement element, string path, RoutedEvent routedEvent)
    {
        var constructor = typeof(DragEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single(c => c.GetParameters().Length == 5);
        var args = (DragEventArgs)constructor.Invoke([new DataObject(DataFormats.FileDrop, new[] { path }), DragDropKeyStates.None, DragDropEffects.Copy, element, new Point(1, 1)]);
        args.RoutedEvent = routedEvent;
        element.RaiseEvent(args);
    }
    private static string Address(DrivePanelWindow panel) => Control<StackPanel>(panel, "BreadcrumbPanel").Children.OfType<TextBlock>().Single().Text;
    private static T Control<T>(FrameworkElement window, string name) => (T)window.FindName(name);
    private static void Click(FrameworkElement window, string name) => Control<ToggleButton>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
