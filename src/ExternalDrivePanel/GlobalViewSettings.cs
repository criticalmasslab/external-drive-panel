namespace ExternalDrivePanel;

public sealed class GlobalViewSettings
{
    public bool ShowHiddenItems { get; private set; }
    public bool ShowPreviews { get; private set; }
    public double IconSize { get; private set; } = 24;
    public GlobalViewMode ViewMode { get; private set; } = GlobalViewMode.Icon;

    public event EventHandler? Changed;
    public event EventHandler? ViewModeApplied;

    public void Restore(GlobalViewConfig config)
    {
        ViewMode = config.ViewMode;
        IconSize = config.IconSize;
        ShowHiddenItems = config.ShowHidden;
        ShowPreviews = config.ShowPreviews;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public GlobalViewConfig Capture() => new()
    {
        ViewMode = ViewMode, IconSize = IconSize, ShowHidden = ShowHiddenItems, ShowPreviews = ShowPreviews
    };

    public void ToggleHiddenItems()
    {
        ShowHiddenItems = !ShowHiddenItems;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePreviews()
    {
        ShowPreviews = !ShowPreviews;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void IncreaseIconSize()
    {
        IconSize = Math.Min(64, IconSize + 4);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DecreaseIconSize()
    {
        IconSize = Math.Max(16, IconSize - 4);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetViewMode(GlobalViewMode mode)
    {
        ViewMode = mode;
        ViewModeApplied?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public enum GlobalViewMode
{
    Icon,
    Details
}
