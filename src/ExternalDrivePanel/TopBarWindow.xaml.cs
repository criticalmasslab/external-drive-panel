using System.Windows;

namespace ExternalDrivePanel;

public partial class TopBarWindow : Window
{
    private readonly GlobalViewSettings _viewSettings;
    private readonly PhysicalWindowPlacement _placement;

    public TopBarWindow(GlobalViewSettings viewSettings)
    {
        InitializeComponent();
        HeaderChromeMetrics.Apply(this, new DpiScale(1, 1));
        SourceInitialized += (_, _) => HeaderChromeMetrics.Apply(this, System.Windows.Media.VisualTreeHelper.GetDpi(this));
        Loaded += (_, _) => HeaderChromeMetrics.Apply(this, System.Windows.Media.VisualTreeHelper.GetDpi(this));
        _placement = new PhysicalWindowPlacement(this);
        _viewSettings = viewSettings;
        _viewSettings.Changed += ViewSettings_OnChanged;
        UpdateStateHint();
    }

    public event EventHandler? SettingsRequested;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        HeaderChromeMetrics.Apply(this, newDpi);
    }

    public void SetPlacement(LayoutRect rectangle)
    {
        _placement.SetPlacement(rectangle);
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void HiddenButton_OnClick(object sender, RoutedEventArgs e) => _viewSettings.ToggleHiddenItems();
    private void PreviewButton_OnClick(object sender, RoutedEventArgs e) => _viewSettings.TogglePreviews();
    private void SmallerButton_OnClick(object sender, RoutedEventArgs e) => _viewSettings.DecreaseIconSize();
    private void LargerButton_OnClick(object sender, RoutedEventArgs e) => _viewSettings.IncreaseIconSize();
    private void ViewModeButton_OnClick(object sender, RoutedEventArgs e) =>
        _viewSettings.SetViewMode(_viewSettings.ViewMode == GlobalViewMode.Icon ? GlobalViewMode.Details : GlobalViewMode.Icon);

    private void ViewSettings_OnChanged(object? sender, EventArgs e) => UpdateStateHint();

    private void UpdateStateHint()
    {
        PreviewButton.ToolTip = _viewSettings.ShowPreviews ? "파일 미리보기 켜짐 — 클릭하면 끄기" : "파일 미리보기 끔 — 클릭하면 켜기";
        PreviewButton.Opacity = _viewSettings.ShowPreviews ? 1 : .65;
        PreviewIcon.IsColorized = _viewSettings.ShowPreviews;
        PreviewButton.Background = System.Windows.Media.Brushes.Transparent;
        HiddenButton.ToolTip = _viewSettings.ShowHiddenItems ? "숨김 항목 숨기기" : "숨김 항목 표시";
        HiddenButton.Opacity = _viewSettings.ShowHiddenItems ? 1.0 : 0.65;
        var details = _viewSettings.ViewMode == GlobalViewMode.Details;
        ViewModeIcon.Kind = details ? "List" : "Grid";
        ViewModeButton.ToolTip = details ? "전체 기본값: 상세 보기 — 클릭하면 전체 패널 아이콘 보기" : "전체 기본값: 아이콘 보기 — 클릭하면 전체 패널 상세 보기";
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewSettings.Changed -= ViewSettings_OnChanged;
        base.OnClosed(e);
    }
}
