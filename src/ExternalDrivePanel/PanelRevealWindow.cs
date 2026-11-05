using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ExternalDrivePanel;

public sealed class PanelRevealWindow : Window
{
    private readonly PhysicalWindowPlacement _placement;
    public event EventHandler? RevealRequested;
    public PanelRevealWindow()
    {
        Title = Localization.T("패널 앞으로"); WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        AllowsTransparency = true; Background = Brushes.Transparent;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        var button = new Button { ToolTip = Localization.T("External Drive Panel 앞으로"), Padding = new Thickness(4),
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
        System.Windows.Automation.AutomationProperties.SetName(button, Localization.T("패널 앞으로"));
        button.Click += (_, _) => RevealRequested?.Invoke(this, EventArgs.Empty);
        Content = button; _placement = new PhysicalWindowPlacement(this);
        Localization.Attach(this, () =>
        {
            Title = Localization.T("패널 앞으로");
            button.ToolTip = Localization.T("External Drive Panel 앞으로");
            System.Windows.Automation.AutomationProperties.SetName(button, Localization.T("패널 앞으로"));
        });
    }
    public void SetPlacement(LayoutRect rectangle) => _placement.SetPlacement(rectangle);

    internal void ShowAboveCoveringWindows()
    {
        if (!IsVisible) Show();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var bounds)) return;
        var needsRaise = (GetWindowLongPtrW(handle, -20).ToInt64() & 0x8) == 0; // WS_EX_TOPMOST
        var visited = new HashSet<nint>();
        var window = GetWindow(handle, 3); // GW_HWNDPREV: only windows above this button.
        while (!needsRaise && window != 0 && visited.Count < 1024 && visited.Add(window))
        {
            // Keep the button's own tooltip/context popups above their owner.
            var owner = GetWindow(window, 4);
            var ownPopup = false;
            for (var depth = 0; owner != 0 && depth < 8; depth++, owner = GetWindow(owner, 4))
                if (owner == handle) { ownPopup = true; break; }
            if (!ownPopup && IsWindowVisible(window) && !IsIconic(window)
                && (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) != 0 || cloaked == 0)
                && GetWindowRect(window, out var other)
                && other.Left < bounds.Right && other.Right > bounds.Left && other.Top < bounds.Bottom && other.Bottom > bounds.Top)
                needsRaise = true;
            window = GetWindow(window, 3);
        }
        if (!needsRaise) return;
        if (!Topmost) Topmost = true;
        // Restore native topmost ordering without activation, moving or resizing.
        // The existing service tick calls this only while the helper is needed.
        if (!SetWindowPos(handle, new nint(-1), 0, 0, 0, 0, 0x213))
            System.Diagnostics.Debug.WriteLine("Could not restore reveal button z-order.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint relation);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
