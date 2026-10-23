using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ExternalDrivePanel;

public static class PanelTransferPolicy
{
    public static DragDropEffects Effect(string sourceRoot, string destinationRoot, DragDropKeyStates keys, DragDropEffects allowed)
    {
        if ((keys & DragDropKeyStates.AltKey) != 0 || (keys & (DragDropKeyStates.ControlKey | DragDropKeyStates.ShiftKey))
            == (DragDropKeyStates.ControlKey | DragDropKeyStates.ShiftKey)) return DragDropEffects.None;
        var copy = (keys & DragDropKeyStates.ControlKey) != 0;
        var move = (keys & DragDropKeyStates.ShiftKey) != 0;
        var sameDrive = string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot)), StringComparison.OrdinalIgnoreCase);
        var effect = copy ? DragDropEffects.Copy : move || sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
        return (allowed & effect) != 0 ? effect : DragDropEffects.None;
    }
    public static string? Destination(string root, string? folder, DropTarget? target) => target is null ? folder ?? root : target.IsDirectory ? target.Path : null;
    public static bool SameParent(string path, string destination) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))) ?? ""),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase);
    public static string[] ActionableSources(IEnumerable<string> paths, string destination) =>
        Sources(paths).Where(path => !SameParent(path, destination)).ToArray();
    public static string[] Sources(IEnumerable<string> paths)
    {
        var all = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => (File.Exists(path) || Directory.Exists(path)) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is not null).ToArray();
        return all.Where(path => !all.Any(parent => !string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(parent) && FileCopyService.IsWithin(path, parent))).ToArray();
    }
}

internal sealed record PanelDragSession(string Id, string Root, Action RefreshSource);

internal sealed class PanelFileDragSource : IDisposable
{
    private const string Format = "ExternalDeskPanel.PanelDrag.v1";
    private static PanelDragSession? _active;
    private readonly Window _owner;
    private readonly string _root;
    private readonly Action _refresh;
    private readonly Selector[] _views;
    private Point? _anchor;
    private object? _deferredSelection;
    private string[] _dragPaths = [];
    private bool _dragging, _disposed;

    public PanelFileDragSource(Window owner, string root, Action refresh, params Selector[] views)
    {
        _owner = owner; _root = root; _refresh = refresh; _views = views;
        foreach (var view in views)
        {
            view.PreviewMouseLeftButtonDown += Down; view.PreviewMouseMove += Move; view.PreviewMouseLeftButtonUp += Up;
        }
    }
    internal static PanelDragSession? Session(IDataObject data) => _active is { } active
        && data.GetDataPresent(Format) && data.GetData(Format) is string id && id == active.Id ? active : null;

    private void Down(object sender, MouseButtonEventArgs e)
    {
        _anchor = null; _deferredSelection = null; _dragPaths = [];
        if (e.ClickCount != 1 || e.OriginalSource is not DependencyObject source) return;
        var item = DragDropService.FindTarget(source).Container?.DataContext;
        if (item is null) return;
        var view = (Selector)sender; _anchor = e.GetPosition(view);
        var selected = ItemContextMenuBuilder.SelectedItems(view);
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            var previous = selected.Cast<object>().ToArray();
            view.Focus();
            selected.Clear(); foreach (var entry in previous) selected.Add(entry);
            if (selected.Contains(item)) _deferredSelection = item;
            else { selected.Clear(); view.SelectedItem = item; }
            // Native ListBox drag-selection must not change the source as the pointer crosses another item.
            e.Handled = true;
        }
        var target = ItemMenuTarget.FromItem(item);
        var paths = ItemContextMenuBuilder.SelectedTargets(view).Select(entry => entry.Path);
        _dragPaths = PanelTransferPolicy.Sources(selected.Contains(item) ? paths
            : (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? paths.Append(target!.Path) : [target!.Path]);
    }
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelection is { } item && !_dragging)
        {
            var view = (Selector)sender; ItemContextMenuBuilder.SelectedItems(view).Clear(); view.SelectedItem = item;
        }
        _anchor = null; _deferredSelection = null;
    }
    private void Move(object sender, MouseEventArgs e)
    {
        if (_dragging || _disposed || _anchor is not { } anchor || e.LeftButton != MouseButtonState.Pressed) return;
        var view = (Selector)sender; var point = e.GetPosition(view);
        e.Handled = true;
        if (Math.Abs(point.X - anchor.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - anchor.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _anchor = null;
        var paths = _dragPaths;
        if (paths.Length == 0) return;
        _dragging = true; _deferredSelection = null; e.Handled = true;
        try
        {
            var data = new DataObject(); var list = new StringCollection(); list.AddRange(paths); data.SetFileDropList(list);
            _active = new(Guid.NewGuid().ToString("N"), _root, () => { if (!_disposed) _refresh(); });
            data.SetData(Format, _active.Id);
            DragDrop.DoDragDrop(view, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex) { DialogActivation.Show(_owner, () => MessageBox.Show(_owner, ex.Message, "파일 드래그", MessageBoxButton.OK, MessageBoxImage.Warning)); }
        finally { _active = null; _dragging = false; if (!_disposed) _refresh(); }
    }
    public void Dispose()
    {
        _disposed = true;
        foreach (var view in _views) { view.PreviewMouseLeftButtonDown -= Down; view.PreviewMouseMove -= Move; view.PreviewMouseLeftButtonUp -= Up; }
    }
}
