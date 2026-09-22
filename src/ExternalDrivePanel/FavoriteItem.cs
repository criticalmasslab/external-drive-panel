namespace ExternalDrivePanel;

public sealed class FavoriteItem
{
    public FavoriteItem(string path, bool isDirectory)
    {
        Path = path;
        IsDirectory = isDirectory;
        Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = path.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        }
    }

    public string Path { get; }
    public bool IsDirectory { get; }
    public string Name { get; }
}