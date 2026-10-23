using System.Text.Json.Serialization;

namespace ExternalDrivePanel;

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public ZoneSettings Zone { get; set; } = new();
    public GlobalViewConfig GlobalView { get; set; } = new();
    public Dictionary<string, DriveConfig> Drives { get; set; } = new();
    public bool NotifyNewDrive { get; set; } = true;
    public List<string> DriveOrder { get; set; } = [];
    public List<string> IgnoredDrives { get; set; } = [];
}

public sealed class GlobalViewConfig
{
    public GlobalViewMode ViewMode { get; set; } = GlobalViewMode.Icon;
    public double IconSize { get; set; } = 24;
    public bool ShowHidden { get; set; }
    public bool ShowPreviews { get; set; }
}

public sealed class DriveConfig
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(DriveViewModeConverter))]
    public GlobalViewMode? ViewMode { get; set; }
    public bool Registered { get; set; }
    public bool Hidden { get; set; }
    public string LastKnownLabel { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreferredLabel { get; set; }
    public List<FavoriteConfig> Favorites { get; set; } = [];
    public string SortKey { get; set; } = nameof(FileSystemEntry.Name);
    public bool SortReverse { get; set; }

    public static bool IsValidSortKey(string key) => key is nameof(FileSystemEntry.Name)
        or nameof(FileSystemEntry.ModifiedDate) or nameof(FileSystemEntry.Size) or nameof(FileSystemEntry.Type);
}

public sealed class FavoriteConfig
{
    public string RelativePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    // Preserve the icon/open behavior even when the saved folder no longer exists.
    public bool? IsDirectory { get; set; }
}
