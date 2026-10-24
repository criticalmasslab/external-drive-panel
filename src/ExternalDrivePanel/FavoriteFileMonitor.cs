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
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentQueue<(string Old, string? New)> _changes = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    private volatile bool _suspended;
    private volatile int _generation;
    private int _scheduled, _needsReconcile;
    public FavoriteFileMonitor(string root, FavoriteCollection favorites, Dispatcher dispatcher)
    {
        _root = Path.GetFullPath(root); _favorites = favorites; _dispatcher = dispatcher;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Drain(), dispatcher);
        _timer.Stop();
        StartWatcher();
    }
    private void StartWatcher()
    {
        var generation = ++_generation;
        try
        {
            _watcher = new FileSystemWatcher(_root) { IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 32768 };
            _watcher.Deleted += (_, e) => { if (generation == _generation) Queue(e.FullPath, null); };
            _watcher.Renamed += (_, e) => { if (generation == _generation) Queue(e.OldFullPath, e.FullPath); };
            _watcher.Error += (_, e) =>
            {
                if (_suspended || _disposed || generation != _generation) return;
                System.Diagnostics.Debug.WriteLine(e.GetException());
                Interlocked.Exchange(ref _needsReconcile, 1); ScheduleDrain();
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _watcher?.Dispose(); _watcher = null; System.Diagnostics.Debug.WriteLine(ex); }
    }
    private void Queue(string oldPath, string? newPath)
    {
        if (_disposed || _suspended || _dispatcher.HasShutdownStarted) return;
        _changes.Enqueue((oldPath, newPath));
        ScheduleDrain();
    }
    private void ScheduleDrain()
    {
        if (_disposed || _suspended || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        try { _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!_disposed && !_suspended && !_timer.IsEnabled) _timer.Start(); })); }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _scheduled, 0); }
    }
    private void Drain()
    {
        _timer.Stop();
        Interlocked.Exchange(ref _scheduled, 0);
        if (_disposed || _suspended) return;
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
        if (_disposed || _suspended || !RootAvailable()) return;
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
    public void Suspend()
    {
        // Preserve already queued renames/deletions before closing the handle.
        Drain();
        _suspended = true; ++_generation; _timer.Stop();
        _watcher?.Dispose(); _watcher = null;
        Interlocked.Exchange(ref _scheduled, 0);
    }
    public void Resume()
    {
        if (_disposed || !_suspended) return;
        _suspended = false; StartWatcher();
        if (!_changes.IsEmpty || Volatile.Read(ref _needsReconcile) != 0) ScheduleDrain();
    }
    public void Dispose() { _disposed = true; Suspend(); }
}
