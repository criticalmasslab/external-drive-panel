using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExternalDrivePanel;

public sealed record DropTarget(string Path, bool IsDirectory);

public interface IDropDialogs
{
    void Inform(string message);
    bool ConfirmCopy(string destination);
    CopyConflictChoice ResolveConflict(string source, string target);
}

public sealed class DragDropService : IDisposable
{
    public static readonly DependencyProperty IsDropTargetProperty = DependencyProperty.RegisterAttached(
        "IsDropTarget", typeof(bool), typeof(DragDropService), new PropertyMetadata(false));
    public static bool GetIsDropTarget(DependencyObject obj) => (bool)obj.GetValue(IsDropTargetProperty);
    public static void SetIsDropTarget(DependencyObject obj, bool value) => obj.SetValue(IsDropTargetProperty, value);

    private readonly FrameworkElement _surface;
    private readonly string _root;
    private readonly FavoriteCollection _favorites;
    private readonly Action<string> _refresh;
    private readonly IDropDialogs _dialogs;
    private readonly CancellationTokenSource _lifetime = new();
    private FrameworkElement? _highlight;
    private bool _busy;
    private bool _disposed;

    public DragDropService(FrameworkElement surface, string root, FavoriteCollection favorites,
        Action<string> refresh, IDropDialogs dialogs)
    {
        _surface = surface;
        _root = root;
        _favorites = favorites;
        _refresh = refresh;
        _dialogs = dialogs;
        surface.AllowDrop = true;
        surface.PreviewDragOver += OnDragOver;
        surface.PreviewDragLeave += OnDragLeave;
        surface.PreviewDrop += OnDrop;
    }

    public static (FrameworkElement? Container, DropTarget? Target) FindTarget(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current is Visual
                 ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is not ListBoxItem && current is not DataGridRow) continue;
            var container = (FrameworkElement)current;
            var target = container.DataContext switch
            {
                FavoriteItem favorite => new DropTarget(favorite.Path, favorite.IsDirectory),
                FileSystemEntry entry => new DropTarget(entry.FullPath, entry.IsDirectory),
                _ => null
            };
            return (container, target);
        }
        return (null, null);
    }

    private static string[] Paths(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop)
        ? data.GetData(DataFormats.FileDrop) as string[] ?? [] : [];

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        SetHighlight(null);
        if (_busy || _disposed || (e.AllowedEffects & DragDropEffects.Copy) == 0) return;
        try
        {
            var paths = Paths(e.Data);
            if (paths.Length == 0) return;
            var hit = FindTarget(e.OriginalSource as DependencyObject);
            var local = paths.Any(path => FileCopyService.IsWithin(path, _root));
            var external = paths.Any(path => !FileCopyService.IsWithin(path, _root));
            var folder = hit.Target is { IsDirectory: true } target
                && FileCopyService.IsWithin(target.Path, _root) && Directory.Exists(target.Path);
            // Accept blank-space drops so the user can receive the requested guidance.
            if (local || folder || hit.Target is null) e.Effects = DragDropEffects.Copy;
            if (external && folder) SetHighlight(hit.Container);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
    }

    private void OnDragLeave(object sender, DragEventArgs e) => SetHighlight(null);

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        SetHighlight(null);
        if (_busy || _disposed || (e.AllowedEffects & DragDropEffects.Copy) == 0) return;
        try
        {
            var paths = Paths(e.Data);
            var target = FindTarget(e.OriginalSource as DependencyObject).Target;
            e.Effects = DragDropEffects.Copy; // Never request a move from Explorer.
            await ProcessDropAsync(paths, target);
        }
        catch (Exception ex)
        {
            if (!_disposed) _dialogs.Inform($"드롭 처리에 실패했습니다.\n{ex.Message}");
        }
    }

    public async Task ProcessDropAsync(IEnumerable<string> paths, DropTarget? target)
    {
        if (_busy || _disposed) return;
        _busy = true;
        string? destination = null;
        try
        {
            var external = new List<string>();
            foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (FileCopyService.IsWithin(path, _root))
                {
                    if (File.Exists(path) || Directory.Exists(path)) _favorites.Add(path, Directory.Exists(path));
                }
                else external.Add(path);
            }
            if (external.Count == 0) return;
            if (target is null)
            {
                _dialogs.Inform("이 외장하드로 복사하려면 폴더 위에 정확히 놓아주세요.");
                return;
            }
            if (!target.IsDirectory) return;
            if (!FileCopyService.IsWithin(target.Path, _root) || !Directory.Exists(target.Path))
                throw new IOException("복사 대상 폴더가 이 드라이브에 없거나 더 이상 존재하지 않습니다.");
            if (!_dialogs.ConfirmCopy(target.Path)) return;
            destination = target.Path;
            var report = await Task.Run(() => new FileCopyService().Copy(external, destination,
                (source, existing) => _surface.Dispatcher.Invoke(() => _disposed
                    ? CopyConflictChoice.Cancel : _dialogs.ResolveConflict(source, existing)), _lifetime.Token));
            if (!_disposed) _dialogs.Inform($"{(report.Cancelled ? "복사를 중단했습니다" : "복사가 완료되었습니다")}.\n파일 {report.FilesCopied}개 복사, 항목 {report.ItemsSkipped}개 건너뜀.\n이미 복사한 항목은 유지됩니다.");
        }
        catch (Exception ex)
        {
            if (!_disposed) _dialogs.Inform($"복사를 완료하지 못했습니다. 이미 복사한 항목은 유지됩니다.\n{ex.Message}");
        }
        finally
        {
            _busy = false;
            if (!_disposed && destination is not null) _refresh(destination);
        }
    }

    private void SetHighlight(FrameworkElement? item)
    {
        if (_highlight is not null) SetIsDropTarget(_highlight, false);
        _highlight = item;
        if (item is not null) SetIsDropTarget(item, true);
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        SetHighlight(null);
        _surface.PreviewDragOver -= OnDragOver;
        _surface.PreviewDragLeave -= OnDragLeave;
        _surface.PreviewDrop -= OnDrop;
    }
}

public sealed class DropDialogs(Window owner) : IDropDialogs
{
    public void Inform(string message) => MessageBox.Show(owner, message, "드래그 앤 드롭", MessageBoxButton.OK, MessageBoxImage.Information);
    public bool ConfirmCopy(string destination) => MessageBox.Show(owner, $"'{destination}' 폴더로 복사하시겠습니까?",
        "파일 복사 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public CopyConflictChoice ResolveConflict(string source, string target)
    {
        var dialog = new CopyConflictWindow(owner, source, target);
        dialog.ShowDialog();
        return dialog.Choice;
    }
}
