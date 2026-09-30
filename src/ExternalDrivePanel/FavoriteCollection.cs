using System.Collections.ObjectModel;

namespace ExternalDrivePanel;

public sealed class FavoriteCollection
{
    public ObservableCollection<FavoriteItem> Items { get; } = [];

    public void Add(string path, bool isDirectory)
    {
        path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (Items.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Items.Add(new FavoriteItem(path, isDirectory));
    }

    public bool Remove(FavoriteItem item) => Items.Remove(item);
}
