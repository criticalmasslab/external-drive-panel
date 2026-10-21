namespace ExternalDrivePanel;

public static class PanelViewPolicy
{
    public static GlobalViewMode EffectiveMode(GlobalViewMode globalMode, GlobalViewMode? driveMode) => driveMode ?? globalMode;

    public static void ApplyGlobally(AppConfig config, GlobalViewMode mode)
    {
        config.GlobalView.ViewMode = mode;
        foreach (var drive in config.Drives.Values.Where(drive => drive.Registered)) drive.ViewMode = null;
    }

    public static bool ShowEmptyFavorites(bool isHome, int favoritesCount) => isHome && favoritesCount == 0;
}
