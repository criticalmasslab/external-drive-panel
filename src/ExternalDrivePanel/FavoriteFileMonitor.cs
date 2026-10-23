using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;

namespace ExternalDrivePanel;

/// <summary>Remove confirmed deletions, not inaccessible paths or disconnected volumes.</summary>
public sealed class FavoriteFileMonitor : IDisposable
{
    private readonly string _root;
    private readonly FavoriteCollection _favorites;
    private readonly Dispatcher _dispatcher;
    private readonly FileSystemWatcher? _watcher;
    private readonly ConcurrentQueue<(string Old, string? New)> _changes = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    private int _scheduled, _needsReconcile;
    public FavoriteFileMonitor(string root, FavoriteCollection favorites, Dispatcher dispatcher)
    {
        _root = Path.GetFullPath(root); _favorites = favorites; _dispatcher = dispatcher;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Drain(), dispatcher);
        _timer.Stop();
        try
        {
            _watcher = new FileSystemWatcher(_root) { IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 32768 };
            _watcher.Deleted += (_, e) => Queue(e.FullPath, null);
            _watcher.Renamed += (_, e) => Queue(e.OldFullPath, e.FullPath);
            _watcher.Error += (_, e) =>
            {
                System.Diagnostics.Debug.WriteLine(e.GetException());
                Interlocked.Exchange(ref _needsReconcile, 1); ScheduleDrain();
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { System.Diagnostics.Debug.WriteLine(ex); }
    }
    private void Queue(string oldPath, string? newPath)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _changes.Enqueue((oldPath, newPath));
        ScheduleDrain();
    }
    private void ScheduleDrain()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        try { _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!_disposed && !_timer.IsEnabled) _timer.Start(); })); }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _scheduled, 0); }
    }
    private void Drain()
    {
        _timer.Stop();
        Interlocked.Exchange(ref _scheduled, 0);
        using var update = _favorites.BeginUpdate();
        var available = !_disposed && RootAvailable();
        var processed = 0;
        while (processed++ < 256 && _changes.TryDequeue(out var change))
        {
            if (!available || _favorites.Items.Count == 0) continue;
            if (change.New is { } renamed && FileCopyService.IsWithin(renamed, _root)
                && (File.Exists(renamed) || Directory.Exists(renamed))) _favorites.RenameTree(change.Old, renamed);
            else ConfirmDeleted([change.Old]);
        }
        if (!_changes.IsEmpty) ScheduleDrain();
        else if (Interlocked.Exchange(ref _needsReconcile, 0) != 0 && available)
            ConfirmDeleted(_favorites.Items.Select(item => item.Path).ToArray());
    }
    public void ConfirmDeleted(IEnumerable<string> paths)
    {
        if (_disposed || !RootAvailable()) return;
        foreach (var path in paths)
        {
            if (!FileCopyService.IsWithin(path, _root) || string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(_root), StringComparison.OrdinalIgnoreCase)) continue;
            if (DefinitelyMissing(path)) _favorites.RemoveTree(path);
        }
    }
    public void ReconcileMissing()
    {
        Drain(); // Apply queued renames before checking their old paths.
        if (!_changes.IsEmpty) { Interlocked.Exchange(ref _needsReconcile, 1); return; }
        ConfirmDeleted(_favorites.Items.Select(item => item.Path).ToArray());
    }
    private bool RootAvailable()
    {
        try { return (File.GetAttributes(_root) & FileAttributes.Directory) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private static bool DefinitelyMissing(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose() { _disposed = true; _timer.Stop(); _watcher?.Dispose(); }
}
