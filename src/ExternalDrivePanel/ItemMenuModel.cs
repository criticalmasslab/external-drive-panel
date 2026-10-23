using System.IO;

namespace ExternalDrivePanel;

public enum ItemMenuCommand { Open, OpenWith, Explore, Separator, ToggleFavorite, Compress, Zip, CopyPath, Properties, EditText }
public sealed record ItemMenuContext(bool IsDirectory, bool IsHome, bool IsFavorite, bool Exists, bool IsTextFile);
public sealed record ItemMenuEntry(ItemMenuCommand Command, string Label, bool Enabled = true,
    string Gesture = "", IReadOnlyList<ItemMenuEntry>? Children = null);
public sealed record ItemMenuTarget(string Path, bool IsDirectory)
{
    public static ItemMenuTarget? FromItem(object? item) => item switch
    {
        FavoriteItem favorite => new(favorite.Path, favorite.IsDirectory),
        FavoriteDetailsRow row => new(row.FullPath, row.IsDirectory),
        FileSystemEntry entry => new(entry.FullPath, entry.IsDirectory),
        _ => null
    };
}

public static class ItemMenuModel
{
    public const string TextExtensions = ".txt .md .log .json .xml .csv .ini .cfg .py .cs .js .ts .html .css .yml .yaml .bat .cmd .ps1 .sql";
    private static readonly HashSet<string> Extensions = new(TextExtensions.Split(' '), StringComparer.OrdinalIgnoreCase);
    public static bool IsTextFile(string path) => Extensions.Contains(Path.GetExtension(path));

    public static IReadOnlyList<ItemMenuEntry> Build(ItemMenuContext item)
    {
        var remove = item.IsHome || item.IsFavorite;
        var menu = new List<ItemMenuEntry>
        {
            new(ItemMenuCommand.Open, "열기", item.Exists, "Enter"),
            item.IsDirectory ? new(ItemMenuCommand.Explore, "탐색기에서 열기", item.Exists)
                : new(ItemMenuCommand.OpenWith, "연결 프로그램…", item.Exists),
            new(ItemMenuCommand.Separator, ""),
            new(ItemMenuCommand.ToggleFavorite, remove ? "즐겨찾기에서 제거" : "즐겨찾기에 추가", remove || item.Exists),
            new(ItemMenuCommand.Compress, "압축", item.Exists, Children: [new(ItemMenuCommand.Zip, "ZIP 파일로 압축", item.Exists)]),
            new(ItemMenuCommand.CopyPath, "경로로 복사", true, "Ctrl+Shift+C"),
            new(ItemMenuCommand.Properties, "속성", item.Exists, "Alt+Enter")
        };
        if (!item.IsDirectory && item.IsTextFile) menu.Add(new(ItemMenuCommand.EditText, "메모장에서 편집", item.Exists));
        return menu;
    }

    public static IReadOnlyList<ItemMenuEntry> BuildSelection(IReadOnlyList<ItemMenuContext> items)
    {
        if (items.Count == 0) return [];
        if (items.Count == 1) return Build(items[0]);
        // Only show operations shared by every selected item. Never partially toggle favorites.
        var remove = items.All(item => item.IsHome || item.IsFavorite);
        var menus = items.Select(Build).ToArray();
        return menus[0].Where(entry => menus.All(menu => menu.Any(other => other.Command == entry.Command)))
            .Select(entry => entry.Command == ItemMenuCommand.ToggleFavorite
                ? entry with { Label = remove ? "즐겨찾기에서 제거" : "즐겨찾기에 추가", Enabled = remove || items.All(item => item.Exists) }
                : entry with { Enabled = menus.All(menu => menu.Single(other => other.Command == entry.Command).Enabled),
                    Children = entry.Children?.Select(child => child with { Enabled = items.All(item => item.Exists) }).ToArray() })
            .ToArray();
    }

    public static string? RelativeKey(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Replace('/', '\\').TrimEnd('\\');
        return Path.IsPathRooted(relative) || relative.Split('\\').Contains("..") ? null : relative;
    }

    public static bool SameFavorite(string root, string first, string second)
        => RelativeKey(root, first) is { } key && string.Equals(key, RelativeKey(root, second), StringComparison.OrdinalIgnoreCase);
}
