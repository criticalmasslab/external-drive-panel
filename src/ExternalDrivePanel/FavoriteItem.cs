namespace ExternalDrivePanel;

public sealed class FavoriteItem
{
    public FavoriteItem(string path, bool isDirectory, bool isBuiltIn = false, string? label = null)
    {
        Path = path;
        IsDirectory = isDirectory;
        IsBuiltIn = isBuiltIn;
        Name = label ?? System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = path.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        }
    }

    public string Path { get; }
    public bool IsDirectory { get; }
    public bool IsBuiltIn { get; }
    public string Name { get; }
    public string Icon => IsDirectory ? "📁" : "📄";
}