using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace ExternalDrivePanel;

public sealed class ItemContextMenuBuilder : IDisposable
{
    private static readonly ZipCompressionService Zip = new();
    private readonly Window _owner;
    private readonly string _root;
    private readonly FavoriteCollection _favorites;
    private readonly Func<bool> _isHome;
    private readonly Action<ItemMenuTarget> _open;
    private readonly Action<string> _showAddress;
    private readonly Action<string> _archiveCreated;
    private readonly Action<string, bool> _notify;
    private readonly Selector[] _views;
    private readonly Dictionary<Selector, ItemMenuTarget?> _clicked = [];
    private bool _disposed;
    private bool _nativeMenuOpen;
    public Func<PanelFileCommand, IReadOnlyList<ItemMenuTarget>, Task>? FileCommandRequested { get; set; }

    public ItemContextMenuBuilder(Window owner, string root, FavoriteCollection favorites, Func<bool> isHome,
        Action<ItemMenuTarget> open, Action<string> showAddress, Action<string> archiveCreated,
        Action<string, bool> notify, params Selector[] views)
    {
        _owner = owner; _root = root; _favorites = favorites; _isHome = isHome;
        _open = open; _showAddress = showAddress; _archiveCreated = archiveCreated; _notify = notify; _views = views;
        foreach (var view in views)
        {
            view.ContextMenu = new ContextMenu();
            view.PreviewMouseRightButtonDown += OnRightButton;
            view.ContextMenuOpening += OnOpening;
        }
        owner.PreviewKeyDown += OnKeyDown;
    }

    private void OnRightButton(object sender, MouseButtonEventArgs e)
    {
        var view = (Selector)sender;
        var container = DragDropService.FindTarget(e.OriginalSource as DependencyObject).Container;
        var item = container?.DataContext;
        var target = ItemMenuTarget.FromItem(item);
        _clicked[view] = target;
        if (target is null) return;
        if (!SelectedItems(view).Contains(item))
        {
            SelectedItems(view).Clear();
            view.SelectedItem = item;
        }
        var selection = SelectedItems(view).Cast<object>().ToArray();
        container?.Focus();
        var selected = SelectedItems(view);
        foreach (var other in selected.Cast<object>().Where(other => !selection.Contains(other)).ToArray()) selected.Remove(other);
        foreach (var other in selection.Where(other => !selected.Contains(other))) selected.Add(other);
        _showAddress(target.Path);
        e.Handled = true; // WPF's default right-click must not collapse an existing multi-selection.
    }

    private void OnOpening(object sender, ContextMenuEventArgs e)
    {
        var view = (Selector)sender;
        var target = e.CursorLeft < 0 ? ItemMenuTarget.FromItem(view.SelectedItem)
            : ItemMenuTarget.FromItem(DragDropService.FindTarget(e.OriginalSource as DependencyObject).Container?.DataContext)
                ?? _clicked.GetValueOrDefault(view);
        if (target is null) { e.Handled = true; return; }
        var targets = SelectedTargets(view);
        var snapshot = targets.Count == 0 ? new[] { target } : targets.ToArray();
        if (snapshot.All(item => Context(item).Exists) && WindowsShellMenu.CanCombine(snapshot.Select(item => item.Path).ToArray()))
        {
            e.Handled = true;
            var point = e.CursorLeft < 0 ? view.PointToScreen(new Point(20, 30)) : WindowsShellMenu.CursorPoint();
            _owner.Dispatcher.BeginInvoke(new Action(async () => await ShowWindowsMenuAsync(snapshot, point)));
        }
        else PopulateSelection(view.ContextMenu, snapshot);
    }

    public async Task ShowWindowsMenuAsync(IReadOnlyList<ItemMenuTarget> targets, Point screenPoint)
    {
        if (_disposed || _nativeMenuOpen) return;
        _nativeMenuOpen = true;
        try
        {
            var hwnd = new WindowInteropHelper(_owner).Handle;
            using var menu = new WindowsShellMenu(hwnd, targets.Select(item => item.Path).ToArray());
            menu.AddPanelCommands(targets.All(item => _isHome() || Favorite(item.Path) is not null));
            var command = menu.Show(_owner, screenPoint);
            if (command == 0) return;
            if (command is WindowsShellMenu.PanelFavorite or WindowsShellMenu.PanelCopyPath or WindowsShellMenu.PanelZip)
                await ExecuteSelectionAsync(command == WindowsShellMenu.PanelFavorite ? ItemMenuCommand.ToggleFavorite
                    : command == WindowsShellMenu.PanelCopyPath ? ItemMenuCommand.CopyPath : ItemMenuCommand.Zip, targets);
            else
            {
                var verb = menu.Verb(command).ToLowerInvariant();
                var action = verb switch { "cut" => PanelFileCommand.Cut, "copy" => PanelFileCommand.Copy,
                    "delete" => PanelFileCommand.Delete, "rename" => PanelFileCommand.Rename,
                    "windows.modernshare" or "share" => PanelFileCommand.Share, _ => (PanelFileCommand?)null };
                if (action is not null && FileCommandRequested is not null) await FileCommandRequested(action.Value, targets);
                else if (verb is "open" or "explore" && targets.Count == 1 && targets[0].IsDirectory) _open(targets[0]);
                else menu.Invoke(hwnd, command); // Shell UI handlers (notably ModernShare) require the HWND's STA thread.
                if (!_disposed) foreach (var parent in targets.Select(item => Path.GetDirectoryName(item.Path)).OfType<string>().Distinct()) _archiveCreated(parent);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            if (!_disposed)
            {
                var fallback = new ContextMenu { PlacementTarget = _owner, Placement = PlacementMode.MousePoint };
                PopulateSelection(fallback, targets);
                fallback.Items.Insert(0, new MenuItem { Header = "Windows 메뉴를 열 수 없어 기본 메뉴로 표시합니다", IsEnabled = false, ToolTip = ex.Message });
                fallback.IsOpen = true;
            }
        }
        finally { _nativeMenuOpen = false; }
    }

    private ItemMenuContext Context(ItemMenuTarget target) => new(target.IsDirectory, _isHome(),
        Favorite(target.Path) is not null, target.IsDirectory ? Directory.Exists(target.Path) : File.Exists(target.Path),
        ItemMenuModel.IsTextFile(target.Path));
    private FavoriteItem? Favorite(string path) => _favorites.Items.FirstOrDefault(item => ItemMenuModel.SameFavorite(_root, item.Path, path));

    public static System.Collections.IList SelectedItems(Selector view) => view switch
    {
        ListBox list => list.SelectedItems,
        DataGrid grid => grid.SelectedItems,
        _ => throw new ArgumentException("Unsupported selection view", nameof(view))
    };

    public static IReadOnlyList<ItemMenuTarget> SelectedTargets(Selector view)
    {
        var items = SelectedItems(view);
        if (items.Count == 0) return [];
        if (items.Count == 1) return ItemMenuTarget.FromItem(items[0]) is { } target ? [target] : [];
        var selected = items.Cast<object>().ToHashSet();
        return view.Items.Cast<object>().Where(selected.Contains).Select(ItemMenuTarget.FromItem)
            .OfType<ItemMenuTarget>().ToArray();
    }

    public void Populate(ContextMenu menu, ItemMenuTarget target) => PopulateSelection(menu, new[] { target });
    public void PopulateSelection(ContextMenu menu, IReadOnlyList<ItemMenuTarget> targets)
    {
        var snapshot = targets.ToArray(); // Favorite changes and refreshes may rebuild ItemsSource during a batch.
        menu.Items.Clear();
        foreach (var entry in ItemMenuModel.BuildSelection(snapshot.Select(Context).ToArray())) menu.Items.Add(Create(entry));
        if (snapshot.Length > 0 && snapshot.All(item => Context(item).Exists) && FileCommandRequested is not null)
        {
            menu.Items.Add(new Separator());
            foreach (var (command, label) in new[] { (PanelFileCommand.Cut, "잘라내기"), (PanelFileCommand.Copy, "복사"),
                (PanelFileCommand.Rename, "이름 바꾸기…"), (PanelFileCommand.Share, "공유…"), (PanelFileCommand.Delete, "삭제…") })
            {
                var item = new MenuItem { Header = label };
                item.Click += async (_, _) => await FileCommandRequested(command, snapshot);
                menu.Items.Add(item);
            }
            var native = new MenuItem { Header = "Windows 메뉴 (항목별)" };
            foreach (var target in snapshot)
            {
                var child = new MenuItem { Header = Path.GetFileName(target.Path) };
                child.Click += async (_, _) => await ShowWindowsMenuAsync([target], WindowsShellMenu.CursorPoint());
                native.Items.Add(child);
            }
            menu.Items.Add(native);
        }
        object Create(ItemMenuEntry entry)
        {
            if (entry.Command == ItemMenuCommand.Separator) return new Separator();
            var item = new MenuItem { Header = entry.Label, IsEnabled = entry.Enabled, InputGestureText = entry.Gesture, Tag = entry.Command };
            if (entry.Children is not null)
                foreach (var child in entry.Children) item.Items.Add(Create(child));
            else item.Click += async (_, e) => { e.Handled = true; await ExecuteSelectionAsync(entry.Command, snapshot); };
            return item;
        }
    }

    public static ItemMenuCommand? Shortcut(Key key, Key systemKey, ModifierKeys modifiers)
    {
        if (key == Key.System) key = systemKey;
        if (key == Key.Enter && modifiers == ModifierKeys.None) return ItemMenuCommand.Open;
        if (key == Key.Enter && modifiers == ModifierKeys.Alt) return ItemMenuCommand.Properties;
        if (key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) return ItemMenuCommand.CopyPath;
        return null;
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_disposed || e.IsRepeat) return;
        var command = Shortcut(e.Key, e.SystemKey, Keyboard.Modifiers);
        if (command is null) return;
        var view = _views.FirstOrDefault(view => view.IsVisible && view.IsKeyboardFocusWithin);
        if (view is null) return;
        var targets = SelectedTargets(view);
        if (targets.Count == 0) return;
        e.Handled = true; // Preview handles Enter before DataGrid/ListBox defaults.
        await ExecuteSelectionAsync(command.Value, targets);
    }

    public async Task ExecuteSelectionAsync(ItemMenuCommand command, IReadOnlyList<ItemMenuTarget> targets)
    {
        if (_disposed || targets.Count == 0) return;
        var snapshot = targets.DistinctBy(target => target.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = ItemMenuModel.BuildSelection(snapshot.Select(Context).ToArray());
        if (!entries.SelectMany(entry => entry.Children is null ? new[] { entry } : entry.Children.Prepend(entry))
            .Any(entry => entry.Command == command && entry.Enabled)) return;
        if (command == ItemMenuCommand.CopyPath)
        {
            try { await ShellActions.CopyPathsAsync(snapshot.Select(target => target.Path)); }
            catch (Exception) { /* A locked clipboard must stay quiet. */ }
            return;
        }
        if (command == ItemMenuCommand.ToggleFavorite)
        {
            using var update = _favorites.BeginUpdate();
            var remove = snapshot.All(target => _isHome() || Favorite(target.Path) is not null);
            foreach (var target in snapshot)
            {
                if (remove) { if (Favorite(target.Path) is { } favorite) _favorites.Remove(favorite); }
                else if (Favorite(target.Path) is null) _favorites.Add(target.Path, target.IsDirectory);
            }
            return;
        }
        foreach (var target in snapshot)
        {
            if (_disposed) break;
            // One panel can show only one folder. Multiple folders open in Explorer, not
            // a series of invisible navigations whose last folder silently wins.
            var action = command == ItemMenuCommand.Open && snapshot.Length > 1 && target.IsDirectory
                ? ItemMenuCommand.Explore : command;
            await ExecuteAsync(action, target);
        }
    }

    public async Task ExecuteAsync(ItemMenuCommand command, ItemMenuTarget target)
    {
        if (_disposed) return;
        var context = Context(target);
        var allowed = ItemMenuModel.Build(context).SelectMany(entry => entry.Children is null ? new[] { entry } : entry.Children.Prepend(entry))
            .Any(entry => entry.Command == command && entry.Enabled);
        if (!allowed) return;
        try
        {
            switch (command)
            {
                case ItemMenuCommand.Open: _open(target); break;
                case ItemMenuCommand.Explore: ShellActions.OpenInExplorer(target.Path); break;
                case ItemMenuCommand.OpenWith:
                    await DialogActivation.ShowAsync(_owner, async () => { await ShellActions.OpenWithAsync(new WindowInteropHelper(_owner).Handle, target.Path); return true; }); break;
                case ItemMenuCommand.Properties:
                    await DialogActivation.ShowAsync(_owner, async () => { await ShellActions.ShowPropertiesAsync(new WindowInteropHelper(_owner).Handle, target.Path); return true; }); break;
                case ItemMenuCommand.CopyPath: await ShellActions.CopyPathAsync(target.Path); break;
                case ItemMenuCommand.EditText: ShellActions.EditInNotepad(target.Path); break;
                case ItemMenuCommand.ToggleFavorite:
                    if (Favorite(target.Path) is { } favorite) _favorites.Remove(favorite);
                    else if (!_isHome()) _favorites.Add(target.Path, target.IsDirectory);
                    break;
                case ItemMenuCommand.Zip:
                    var task = Zip.TryCreateAsync(target.Path, target.IsDirectory);
                    if (task is null) return;
                    _notify("압축 중…", false);
                    var result = await task;
                    if (result.OutputPath is { } output)
                    {
                        _notify("압축 완료: " + Path.GetFileName(output), false);
                        if (!_disposed) _archiveCreated(Path.GetDirectoryName(output)!);
                    }
                    else _notify("압축 실패: " + result.Error, true);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (command == ItemMenuCommand.CopyPath) return; // Clipboard failures must stay quiet.
            if (!_disposed) DialogActivation.Show(_owner, () => MessageBox.Show(_owner, ex.Message, "항목 작업", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _owner.PreviewKeyDown -= OnKeyDown;
        foreach (var view in _views)
        {
            view.PreviewMouseRightButtonDown -= OnRightButton;
            view.ContextMenuOpening -= OnOpening;
        }
    }
}
