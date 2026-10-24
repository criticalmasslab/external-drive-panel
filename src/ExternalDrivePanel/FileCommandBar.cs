using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ExternalDrivePanel;

public enum PanelFileCommand { Cut, Copy, Paste, Rename, Share, Delete, NewFolder, NewText, WindowsNewMenu, Refresh }

// A panel owns one command router; toolbar, shortcuts and native menu use the same operations.
internal sealed class FileCommandBar : IDisposable
{
    private readonly Window _owner;
    private readonly Func<IReadOnlyList<ItemMenuTarget>> _selection;
    private readonly Func<string> _destination;
    private readonly Action _refresh;
    private readonly Dictionary<PanelFileCommand, Button> _buttons = [];
    private readonly Selector[] _views;
    private readonly DispatcherTimer _timer, _refreshTimer;
    private FileSystemWatcher? _watcher;
    private string? _watched;
    private bool _busy, _disposed;
    private volatile bool _suspended;
    private bool _validSelection, _shareSelection, _mutableSelection;
    private int _refreshScheduled;
    public Action<IReadOnlyList<string>>? DeletedPaths { get; set; }

    public FileCommandBar(Window owner, Func<IReadOnlyList<ItemMenuTarget>> selection, Func<string> destination,
        Action refresh, Selector[] views)
    {
        _owner = owner; _selection = selection; _destination = destination; _refresh = refresh; _views = views;
        foreach (var view in views) view.SelectionChanged += SelectionChanged;
        foreach (var (command, name) in new[] { (PanelFileCommand.Cut, "CutButton"), (PanelFileCommand.Copy, "CopyButton"),
            (PanelFileCommand.Paste, "PasteButton"), (PanelFileCommand.Rename, "RenameButton"), (PanelFileCommand.Share, "ShareButton"), (PanelFileCommand.Delete, "DeleteButton") })
        {
            var button = (Button)owner.FindName(name);
            _buttons[command] = button;
            button.Click += async (_, _) => await ExecuteAsync(command, _selection());
        }
        var create = (Button)owner.FindName("NewItemButton");
        var menu = new ContextMenu { PlacementTarget = create, Placement = PlacementMode.Bottom };
        foreach (var (command, label) in new[] { (PanelFileCommand.NewFolder, "폴더…"), (PanelFileCommand.NewText, "텍스트 문서…"),
            (PanelFileCommand.WindowsNewMenu, "Windows 새로 만들기 메뉴…") })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await ExecuteAsync(command, []);
            menu.Items.Add(item);
        }
        create.ContextMenu = menu;
        create.Click += (_, _) => { menu.IsOpen = true; };
        _owner.PreviewKeyDown += KeyDown;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Interlocked.Exchange(ref _refreshScheduled, 0); if (!_disposed && !_suspended) { _refresh(); Update(); } };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Update(false), owner.Dispatcher);
        Update();
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => Update();
    public void RefreshState() => Update();
    private static bool HasClipboardFiles()
    {
        try { return Clipboard.ContainsFileDropList(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); return false; }
    }
    private void Update(bool refreshSelection = true)
    {
        if (_disposed || _suspended) return;
        if (refreshSelection)
        {
            var selected = _selection();
            _validSelection = selected.Count > 0 && selected.All(item => File.Exists(item.Path) || Directory.Exists(item.Path));
            _shareSelection = selected.All(item => !item.IsDirectory);
            _mutableSelection = selected.All(item => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(item.Path)) is not null);
        }
        var path = _destination();
        var exists = Directory.Exists(path);
        foreach (var (command, button) in _buttons)
            button.IsEnabled = !_busy && (command == PanelFileCommand.Paste
                ? exists && HasClipboardFiles()
                : _validSelection && (command != PanelFileCommand.Share || _shareSelection)
                    && (command is not (PanelFileCommand.Delete or PanelFileCommand.Rename or PanelFileCommand.Cut)
                        || _mutableSelection));
        ((Button)_owner.FindName("NewItemButton")).IsEnabled = !_busy && exists;
        if (string.Equals(path, _watched, StringComparison.OrdinalIgnoreCase)) return;
        _watcher?.Dispose(); _watcher = null; _watched = path;
        try
        {
            if (!Directory.Exists(path)) return;
            _watcher = new FileSystemWatcher(path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Created += Changed; _watcher.Deleted += Changed; _watcher.Changed += Changed; _watcher.Renamed += Changed;
            _watcher.Error += (_, _) => RequestRefresh();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
    private void Changed(object sender, FileSystemEventArgs e) => RequestRefresh();
    private void RequestRefresh()
    {
        if (_disposed || _suspended || _owner.Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _refreshScheduled, 1) != 0) return;
        try
        {
            _owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || _suspended) return;
                if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
            }));
        }
        catch (InvalidOperationException ex) { Interlocked.Exchange(ref _refreshScheduled, 0); System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.IsRepeat || Keyboard.FocusedElement is TextBoxBase) return;
        if (!_views.Any(view => view.IsKeyboardFocusWithin) && !_owner.IsKeyboardFocusWithin) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifier = Keyboard.Modifiers;
        var command = (key, modifier) switch
        {
            (Key.X, ModifierKeys.Control) => PanelFileCommand.Cut,
            (Key.C, ModifierKeys.Control) => PanelFileCommand.Copy,
            (Key.V, ModifierKeys.Control) => PanelFileCommand.Paste,
            (Key.Delete, ModifierKeys.None) => PanelFileCommand.Delete,
            (Key.F2, ModifierKeys.None) => PanelFileCommand.Rename,
            (Key.F5, ModifierKeys.None) => PanelFileCommand.Refresh,
            (Key.N, ModifierKeys.Control | ModifierKeys.Shift) => PanelFileCommand.NewFolder,
            _ => (PanelFileCommand?)null
        };
        if (command is null) return;
        e.Handled = true;
        await ExecuteAsync(command.Value, _selection());
    }

    public async Task ExecuteAsync(PanelFileCommand command, IReadOnlyList<ItemMenuTarget> targets)
    {
        if (_disposed || _busy) return;
        var paths = targets.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var destination = _destination();
        if (command is PanelFileCommand.Paste or PanelFileCommand.NewFolder or PanelFileCommand.NewText or PanelFileCommand.WindowsNewMenu
            && !Directory.Exists(destination)) return;
        var hwnd = new WindowInteropHelper(_owner).Handle;
        _busy = true; Update();
        try
        {
            switch (command)
            {
                case PanelFileCommand.Cut:
                case PanelFileCommand.Copy: await ShellFileOperations.SetClipboardAsync(paths, command == PanelFileCommand.Cut); break;
                case PanelFileCommand.Paste:
                    if (ShellFileOperations.ReadClipboard() is { } clipboard)
                    {
                        if (await ShellFileOperations.PasteAsync(hwnd, clipboard, destination) && clipboard.Move)
                        {
                            // Do not replace a clipboard the user changed while the operation was running.
                            var current = ShellFileOperations.ReadClipboard();
                            if (current?.Move == true && current.Paths.SequenceEqual(clipboard.Paths, StringComparer.OrdinalIgnoreCase))
                                await ShellFileOperations.ClearClipboardAsync();
                        }
                    }
                    break;
                case PanelFileCommand.Delete:
                    if (paths.Length == 0) break;
                    var confirmed = DialogActivation.Show(_owner, () => MessageBox.Show(_owner,
                        $"선택한 {paths.Length}개 항목을 삭제할까요?\n드라이브에 따라 휴지통을 사용할 수 없으면 Windows에서 추가로 확인합니다.\n즐겨찾기 등록만 제거하려면 ‘패널 즐겨찾기에서 제거’를 사용하세요.",
                        "파일/폴더 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No));
                    if (confirmed == MessageBoxResult.Yes)
                    {
                        try { await ShellFileOperations.DeleteAsync(hwnd, paths); }
                        finally { DeletedPaths?.Invoke(paths); } // Includes successfully deleted items before partial cancellation.
                    }
                    break;
                case PanelFileCommand.Rename:
                    if (paths.Length == 0) break;
                    var name = NamePromptWindow.Ask(_owner, paths.Length == 1 ? "이름 바꾸기" : "일괄 이름 바꾸기 (번호 자동 추가)",
                        paths.Length == 1 ? Path.GetFileName(paths[0]) : "새 이름", paths.Length == 1 && !targets[0].IsDirectory);
                    if (name is not null)
                    {
                        if (paths.Length == 1 && !targets[0].IsDirectory && !string.Equals(Path.GetExtension(name), Path.GetExtension(paths[0]), StringComparison.OrdinalIgnoreCase)
                            && DialogActivation.Show(_owner, () => MessageBox.Show(_owner, "확장자를 바꾸면 파일을 열지 못할 수 있습니다. 변경할까요?", "확장자 변경",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) != MessageBoxResult.Yes) break;
                        await ShellFileOperations.RenameAsync(hwnd, paths, name);
                    }
                    break;
                case PanelFileCommand.NewFolder:
                case PanelFileCommand.NewText:
                    var directory = command == PanelFileCommand.NewFolder;
                    var newName = NamePromptWindow.Ask(_owner, $"{(directory ? "새 폴더" : "새 텍스트 문서")} — {destination}",
                        ShellFileOperations.UniqueName(destination, directory ? "새 폴더" : "새 텍스트 문서.txt"));
                    if (newName is not null) await ShellFileOperations.NewAsync(hwnd, destination, newName, directory);
                    break;
                case PanelFileCommand.WindowsNewMenu:
                    using (var background = new WindowsShellMenu(hwnd, [destination], background: true))
                    {
                        var selected = background.Show(_owner, WindowsShellMenu.CursorPoint());
                        if (selected != 0) background.Invoke(hwnd, selected);
                    }
                    break;
                case PanelFileCommand.Share:
                    if (paths.Length == 0) break;
                    if (!WindowsShellMenu.CanCombine(paths))
                        throw new IOException("서로 다른 폴더의 파일은 각 항목의 Windows 메뉴에서 공유해주세요.");
                    using (var share = new WindowsShellMenu(hwnd, paths, connectSite: true))
                    {
                        var choice = share.Commands().FirstOrDefault(item => item.Enabled && (item.Verb.Contains("modernshare", StringComparison.OrdinalIgnoreCase)
                            || item.Verb.Equals("share", StringComparison.OrdinalIgnoreCase)));
                        if (choice is null) throw new IOException("이 항목에는 Windows 공유 기능이 제공되지 않습니다. 우클릭 Windows 메뉴에서 이용 가능한 공유 기능을 확인해주세요.");
                        share.Invoke(hwnd, choice.Id); // Windows sharing validates the owner HWND's thread.
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            if (!_disposed) DialogActivation.Show(_owner, () => MessageBox.Show(_owner, ex.Message, "파일 작업", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        finally
        {
            _busy = false;
            if (!_disposed) { _refresh(); Update(); }
        }
    }

    public void Suspend()
    {
        _suspended = true; _timer.Stop(); _refreshTimer.Stop();
        _watcher?.Dispose(); _watcher = null; _watched = null; Interlocked.Exchange(ref _refreshScheduled, 0);
    }
    public void Resume()
    {
        if (_disposed || !_suspended) return;
        _suspended = false; Update(); _timer.Start();
    }
    public void Dispose()
    {
        _disposed = true;
        _timer.Stop(); _refreshTimer.Stop(); _watcher?.Dispose();
        _owner.PreviewKeyDown -= KeyDown;
        foreach (var view in _views) view.SelectionChanged -= SelectionChanged;
    }
}
