using System.Collections.ObjectModel;

namespace ExternalDrivePanel;

public sealed class FavoriteCollection
{
    public ObservableCollection<FavoriteItem> Items { get; } = [];

    public event EventHandler? Changed;

    public FavoriteCollection() => Items.CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

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
}
