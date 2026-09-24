using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;

namespace ExternalDrivePanel;

public partial class App : Application
{
    private readonly DriveService _driveService = new();
    private readonly MonitorService _monitorService = new();
    private readonly LayoutCalculator _layoutCalculator = new();
    private readonly EjectService _ejectService = new();
    private readonly ZoneSettings _zoneSettings = new();
    private readonly GlobalViewSettings _viewSettings = new();
    private readonly Dictionary<uint, DrivePanelWindow> _panels = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TaskbarIcon? _trayIcon;
    private TopBarWindow? _topBar;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _viewSettings.Changed += ViewSettings_OnChanged;
        _trayIcon = CreateTrayIcon();
        _topBar = new TopBarWindow(_viewSettings);
        _topBar.SettingsRequested += TopBar_OnSettingsRequested;
        _topBar.Show();

        _pollTimer.Tick += PollTimer_OnTick;
        _pollTimer.Start();
        RefreshPanels();
    }

    private TaskbarIcon CreateTrayIcon()
    {
        var exitItem = new MenuItem { Header = "종료" };
        exitItem.Click += (_, _) => ExitApplication();
        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(exitItem);

        return new TaskbarIcon
        {
            ToolTipText = "External Drive Panel",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenu = contextMenu
        };
    }

    private void PollTimer_OnTick(object? sender, EventArgs e) => RefreshPanels();

    private void RefreshPanels()
    {
        var snapshot = _driveService.SnapshotAllVolumes();
        foreach (var removedSerial in _panels.Keys.Except(snapshot.Keys).ToList())
        {
            _panels[removedSerial].Close();
            _panels.Remove(removedSerial);
        }

        foreach (var (serial, volume) in snapshot)
        {
            if (_panels.ContainsKey(serial))
            {
                continue;
            }

            var panel = new DrivePanelWindow(volume.Root, volume.Label, _viewSettings, _ejectService);
            panel.Closed += (_, _) =>
            {
                if (!_isExiting)
                {
                    _panels.Remove(serial);
                    RepositionWindows();
                }
            };
            _panels.Add(serial, panel);
            panel.Show();
        }

        RepositionWindows();
    }

    private void RepositionWindows()
    {
        var monitors = _monitorService.GetMonitors();
        if (monitors.Count == 0 || _topBar is null)
        {
            return;
        }

        _zoneSettings.MonitorIndex = Math.Clamp(_zoneSettings.MonitorIndex, 0, monitors.Count - 1);
        var monitor = monitors[_zoneSettings.MonitorIndex];
        var cell = _layoutCalculator.CalculateCell(monitor, _zoneSettings);
        _topBar.SetPlacement(_layoutCalculator.CalculateTopBar(cell));

        var panels = _panels.Values.ToList();
        var rectangles = _layoutCalculator.CalculatePanels(cell, panels.Count, _zoneSettings.SplitDirection);
        for (var index = 0; index < panels.Count; index++)
        {
            panels[index].SetPlacement(rectangles[index]);
        }
    }

    private void TopBar_OnSettingsRequested(object? sender, EventArgs e)
    {
        var monitors = _monitorService.GetMonitors();
        if (monitors.Count == 0)
        {
            MessageBox.Show("연결된 모니터 정보를 가져올 수 없습니다.", "화면 영역 설정", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settingsWindow = new ZoneSettingsWindow(_zoneSettings, monitors) { Owner = _topBar };
        settingsWindow.SettingsSaved += (_, _) => RepositionWindows();
        settingsWindow.ShowDialog();
    }

    private void ViewSettings_OnChanged(object? sender, EventArgs e)
    {
        foreach (var panel in _panels.Values)
        {
            panel.ApplyGlobalViewSettings();
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _pollTimer.Stop();
        foreach (var panel in _panels.Values.ToList())
        {
            panel.Close();
        }
        _panels.Clear();

        if (_topBar is not null)
        {
            _topBar.SettingsRequested -= TopBar_OnSettingsRequested;
            _topBar.Close();
            _topBar = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pollTimer.Stop();
        _viewSettings.Changed -= ViewSettings_OnChanged;
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}