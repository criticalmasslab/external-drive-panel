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
        var item = DragDropService.FindTarget(e.OriginalSource as DependencyObject).Container?.DataContext;
        var target = ItemMenuTarget.FromItem(item);
        _clicked[view] = target;
        if (target is null) return;
        if (view is DataGrid grid) grid.UnselectAll();
        view.SelectedItem = item;
        view.Focus();
        _showAddress(target.Path);
    }

    private void OnOpening(object sender, ContextMenuEventArgs e)
    {
        var view = (Selector)sender;
        var target = e.CursorLeft < 0 ? ItemMenuTarget.FromItem(view.SelectedItem)
            : ItemMenuTarget.FromItem(DragDropService.FindTarget(e.OriginalSource as DependencyObject).Container?.DataContext)
                ?? _clicked.GetValueOrDefault(view);
        if (target is null) { e.Handled = true; return; }
        Populate(view.ContextMenu, target);
    }

    private ItemMenuContext Context(ItemMenuTarget target) => new(target.IsDirectory, _isHome(),
        Favorite(target.Path) is not null, target.IsDirectory ? Directory.Exists(target.Path) : File.Exists(target.Path),
        ItemMenuModel.IsTextFile(target.Path));
    private FavoriteItem? Favorite(string path) => _favorites.Items.FirstOrDefault(item => ItemMenuModel.SameFavorite(_root, item.Path, path));

    public void Populate(ContextMenu menu, ItemMenuTarget target)
    {
        menu.Items.Clear();
        foreach (var entry in ItemMenuModel.Build(Context(target))) menu.Items.Add(Create(entry));
        object Create(ItemMenuEntry entry)
        {
            if (entry.Command == ItemMenuCommand.Separator) return new Separator();
            var item = new MenuItem { Header = entry.Label, IsEnabled = entry.Enabled, InputGestureText = entry.Gesture, Tag = entry.Command };
            if (entry.Children is not null)
                foreach (var child in entry.Children) item.Items.Add(Create(child));
            else item.Click += async (_, e) => { e.Handled = true; await ExecuteAsync(entry.Command, target); };
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
        if (view is null || ItemMenuTarget.FromItem(view.SelectedItem) is not { } target) return;
        e.Handled = true; // Preview handles Enter before DataGrid/ListBox defaults.
        await ExecuteAsync(command.Value, target);
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
                    _owner.Activate(); await ShellActions.OpenWithAsync(new WindowInteropHelper(_owner).Handle, target.Path); break;
                case ItemMenuCommand.Properties:
                    _owner.Activate(); await ShellActions.ShowPropertiesAsync(new WindowInteropHelper(_owner).Handle, target.Path); break;
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
