using System.Windows.Controls;

namespace ExternalDrivePanel;

public partial class App
{
    private void Language_OnChanged(object? sender, EventArgs e)
    {
        _config.Language = Localization.Language;
        if (_development && _topBar is { } bar)
        {
            bar.Title = Localization.T("External Drive Panel — 개발용");
            ((TextBlock)bar.FindName("AppNameText")).Text = Localization.T("개발용 · EXTERNAL DRIVE PANEL");
        }
        if (_trayIcon is { } tray)
            tray.ToolTipText = _development ? Localization.T("External Drive Panel — 개발용")
                : IsSoftwareRendering ? Localization.T("External Drive Panel — 소프트웨어 렌더링 (진단)") : "External Drive Panel";
        UpdateMaintenanceMenu();
        if (!_isExiting) ScheduleSave();
    }
}
