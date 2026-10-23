using System.Windows;

namespace ExternalDrivePanel;

internal static class HeaderChromeMetrics
{
    // One physical hit target and pitch for the main bar and every panel, at any DPI.
    public static void Apply(Window window, DpiScale dpi)
    {
        window.Resources["ChromeButtonWidth"] = 34 / dpi.DpiScaleX;
        window.Resources["ChromeButtonHeight"] = 34 / dpi.DpiScaleY;
        window.Resources["ChromeButtonMargin"] = new Thickness(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY, 1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
        window.Resources["ChromeIconFontSize"] = 13 / dpi.DpiScaleY;
        window.Resources["ChromeIconSize"] = 22 / dpi.DpiScaleY;
        window.Resources["ChromeHeaderPadding"] = new Thickness(8 / dpi.DpiScaleX, 0, 4 / dpi.DpiScaleX, 0);
    }
}
