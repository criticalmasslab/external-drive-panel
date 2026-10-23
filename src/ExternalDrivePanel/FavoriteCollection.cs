using System.Collections.ObjectModel;

namespace ExternalDrivePanel;

public sealed class FavoriteCollection
{
    public ObservableCollection<FavoriteItem> Items { get; } = [];

    public event EventHandler? Changed;
    private int _updating;
    private bool _changed;

    public FavoriteCollection() => Items.CollectionChanged += (_, _) =>
    {
        if (_updating > 0) _changed = true;
        else Changed?.Invoke(this, EventArgs.Empty);
    };

    public IDisposable BeginUpdate()
    { _updating++; return new UpdateScope(this); }
    private sealed class UpdateScope(FavoriteCollection owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (--owner._updating == 0 && owner._changed)
            { owner._changed = false; owner.Changed?.Invoke(owner, EventArgs.Empty); }
        }
    }

    public void Add(string path, bool isDirectory, string? displayName = null)
    {
        path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (Items.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Items.Add(new FavoriteItem(path, isDirectory, displayName));
    }

    public bool Remove(FavoriteItem item) => Items.Remove(item);
    public int RemoveTree(string path)
    {
        using var update = BeginUpdate();
        var removed = Items.Where(item => FileCopyService.IsWithin(item.Path, path)).ToArray();
        foreach (var item in removed) Items.Remove(item);
        return removed.Length;
    }
    public void RenameTree(string oldPath, string newPath)
    {
        using var update = BeginUpdate();
        foreach (var item in Items.Where(item => FileCopyService.IsWithin(item.Path, oldPath)).ToArray())
        {
            var index = Items.IndexOf(item);
            var relative = System.IO.Path.GetRelativePath(oldPath, item.Path);
            var path = relative == "." ? newPath : System.IO.Path.Combine(newPath, relative);
            var oldName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(item.Path));
            Items[index] = new FavoriteItem(path, item.IsDirectory, item.Name == oldName ? null : item.Name);
        }
    }
}
