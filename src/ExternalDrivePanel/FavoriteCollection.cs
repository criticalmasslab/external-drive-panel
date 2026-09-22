using System.Collections.ObjectModel;

namespace ExternalDrivePanel;

public sealed class FavoriteCollection
{
    public FavoriteCollection(string rootPath, string rootLabel)
    {
        Items.Add(new FavoriteItem(rootPath, true, true, rootLabel));
    }

    public ObservableCollection<FavoriteItem> Items { get; } = [];

    public void Add(string path, bool isDirectory)
    {
        if (Items.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Items.Add(new FavoriteItem(path, isDirectory));
    }

    public bool Remove(FavoriteItem item) => !item.IsBuiltIn && Items.Remove(item);
}