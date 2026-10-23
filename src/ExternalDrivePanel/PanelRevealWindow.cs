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
        var button = new Button { ToolTip = "External Drive Panel 앞으로", Padding = new Thickness(4),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FocusVisualStyle = null,
            Content = new Image { Source = PanelTrayIcon.Render(64), Stretch = Stretch.Uniform } };
        // Replace the theme's rectangular hover chrome. RenderTransform does not
        // change layout or the 44px hit target; the enlarged icon still fits inside it.
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter), "IconPresenter");
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(UIElement.RenderTransformOriginProperty, new Point(.5, .5));
        presenter.SetValue(UIElement.RenderTransformProperty, new ScaleTransform(1, 1));
        surface.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(RenderTransformProperty, new ScaleTransform(1.2, 1.2), "IconPresenter"));
        template.Triggers.Add(hover); button.Template = template;
        System.Windows.Automation.AutomationProperties.SetName(button, "패널 앞으로");
        button.Click += (_, _) => RevealRequested?.Invoke(this, EventArgs.Empty);
        Content = button; _placement = new PhysicalWindowPlacement(this);
    }
    public void SetPlacement(LayoutRect rectangle) => _placement.SetPlacement(rectangle);
}
