using System.Windows;

namespace ExternalDrivePanel;

public partial class UpdateNotificationWindow : Window
{
    public const double BarHeight = 44;
    private readonly PhysicalWindowPlacement _placement;
    private bool _allowClose;
    public event EventHandler? RestartRequested;
    public UpdateNotificationWindow()
    {
        InitializeComponent();
        _placement = new PhysicalWindowPlacement(this);
        SourceInitialized += (_, _) => UpdateScale(System.Windows.Media.VisualTreeHelper.GetDpi(this));
        Closing += (_, e) => { if (!_allowClose) e.Cancel = true; };
    }
    public void CloseForShutdown() { _allowClose = true; Close(); }
    private void UpdateScale(DpiScale dpi) { FontSize = 12 / dpi.DpiScaleY; RestartButton.FontSize = FontSize; }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); UpdateScale(newDpi); }
    public void SetPlacement(LayoutRect rectangle) => _placement.SetPlacement(rectangle);
    public void SetWaiting(bool waiting)
    {
        RestartButton.IsEnabled = !waiting;
        RestartButton.Content = waiting ? "작업 완료 기다리는 중…" : "업데이트 후 다시 시작";
    }
    private void Restart_OnClick(object sender, RoutedEventArgs e) => RestartRequested?.Invoke(this, EventArgs.Empty);
}
