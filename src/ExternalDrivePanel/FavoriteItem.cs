namespace ExternalDrivePanel;

public sealed class FavoriteItem
{
    public FavoriteItem(string path, bool isDirectory, string? displayName = null)
    {
        Path = path;
        IsDirectory = isDirectory;
        Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = path.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        }
        if (!string.IsNullOrWhiteSpace(displayName)) Name = displayName;
    }

    public string Path { get; }
    public bool IsDirectory { get; }
    public string Name { get; }
    public string Icon => IsDirectory ? "📁" : "📄";
}
