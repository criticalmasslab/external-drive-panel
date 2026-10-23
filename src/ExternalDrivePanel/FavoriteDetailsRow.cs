using System.IO;

namespace ExternalDrivePanel;

public sealed class FavoriteDetailsRow
{
    public FavoriteItem Favorite { get; }
    public string FullPath => Favorite.Path;
    public string Name => Favorite.Name;
    public bool IsFavorite => false; // Badges belong to folder browsing, never the favorites home.
    public bool IsDirectory => Favorite.IsDirectory;
    public string Icon => Favorite.Icon;
    public string DisplayName => $"{Icon} {Name}";
    public DateTime? ModifiedDate { get; }
    public long? Size { get; }
    public string SizeDisplay { get; } = string.Empty;
    public string Type { get; } = "(경로 없음)";

    public FavoriteDetailsRow(FavoriteItem favorite)
    {
        Favorite = favorite;
        try
        {
            if (!File.Exists(favorite.Path) && !Directory.Exists(favorite.Path)) return;
            var entry = FileSystemEntry.FromPath(favorite.Path);
            ModifiedDate = entry.ModifiedDate;
            Size = entry.Size;
            SizeDisplay = entry.SizeDisplay;
            Type = entry.Type;
        }
        catch (UnauthorizedAccessException) { Type = "(접근 불가)"; }
        catch (IOException) { /* Keep the saved favorite when a path disappears mid-read. */ }
    }
}
