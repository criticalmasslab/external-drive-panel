using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExternalDrivePanel;

public sealed class PanelRevealWindow : Window
{
    private readonly PhysicalWindowPlacement _placement;
    public event EventHandler? RevealRequested;
    public PanelRevealWindow()
    {
        Title = "패널 앞으로"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        AllowsTransparency = true; Background = Brushes.Transparent;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        var button = new Button { ToolTip = "External Drive Panel 앞으로", Padding = new Thickness(3),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Content = new Image { Source = PanelTrayIcon.Render(64), Stretch = Stretch.Uniform } };
        System.Windows.Automation.AutomationProperties.SetName(button, "패널 앞으로");
        button.Click += (_, _) => RevealRequested?.Invoke(this, EventArgs.Empty);
        Content = button; _placement = new PhysicalWindowPlacement(this);
    }
    public void SetPlacement(LayoutRect rectangle) => _placement.SetPlacement(rectangle);
}
