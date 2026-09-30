using System.Windows;

namespace ExternalDrivePanel;

public partial class TopBarWindow : Window
{
    private readonly GlobalViewSettings _viewSettings;
    private readonly PhysicalWindowPlacement _placement;

    public TopBarWindow(GlobalViewSettings viewSettings)
    {
        InitializeComponent();
        _placement = new PhysicalWindowPlacement(this);
        _viewSettings = viewSettings;
        _viewSettings.Changed += ViewSettings_OnChanged;
        UpdateStateHint();
    }

    public event EventHandler? SettingsRequested;

    public void SetPlacement(LayoutRect rectangle)
    {
        _placement.SetPlacement(rectangle);
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void HiddenButton_OnClick(object sender, RoutedEventArgs e) => _viewSettings.ToggleHiddenItems();
    private void SmallerButton_OnClick(object sender, RoutedEventArgs e) => _viewSettings.DecreaseIconSize();
    private void LargerButton_OnClick(object sender, RoutedEventArgs e) => _viewSettings.IncreaseIconSize();
    private void IconViewButton_OnClick(object sender, RoutedEventArgs e) => _viewSettings.SetViewMode(GlobalViewMode.Icon);
    private void DetailsViewButton_OnClick(object sender, RoutedEventArgs e) => _viewSettings.SetViewMode(GlobalViewMode.Details);

    private void ViewSettings_OnChanged(object? sender, EventArgs e) => UpdateStateHint();

    private void UpdateStateHint()
    {
        HiddenButton.ToolTip = _viewSettings.ShowHiddenItems ? "숨김 항목 숨기기" : "숨김 항목 표시";
        HiddenButton.Opacity = _viewSettings.ShowHiddenItems ? 1.0 : 0.65;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewSettings.Changed -= ViewSettings_OnChanged;
        base.OnClosed(e);
    }
}
