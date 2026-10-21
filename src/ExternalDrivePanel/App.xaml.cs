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
    private ZoneSettings _zoneSettings = new();
    private readonly GlobalViewSettings _viewSettings = new();
    private readonly Dictionary<uint, DrivePanelWindow> _panels = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TaskbarIcon? _trayIcon;
    private TopBarWindow? _topBar;
    private bool _isExiting;
    private readonly ConfigStore _configStore;
    private readonly Func<Dictionary<uint, (string Root, string Label)>> _snapshotProvider;
    private AppConfig _config = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public App() : this(new ConfigStore()) { }

    public App(ConfigStore configStore, Func<Dictionary<uint, (string Root, string Label)>>? snapshotProvider = null)
    {
        _configStore = configStore;
        _snapshotProvider = snapshotProvider ?? _driveService.SnapshotAllVolumes;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _config = _configStore.Load();
        _zoneSettings = _config.Zone;
        _viewSettings.Restore(_config.GlobalView);
        _saveTimer.Tick += (_, _) => SaveConfig();

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
        var snapshot = _snapshotProvider();
        foreach (var removedSerial in _panels.Keys.Except(snapshot.Keys).ToList())
        {
            _panels[removedSerial].Close();
            _panels.Remove(removedSerial);
        }

        foreach (var (serial, volume) in snapshot)
        {
            if (_panels.TryGetValue(serial, out var existingPanel))
            {
                var key = serial.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (_config.Drives.TryGetValue(key, out var driveSettings) && driveSettings.LastKnownLabel != volume.Label)
                {
                    driveSettings.LastKnownLabel = volume.Label;
                    ScheduleSave();
                }
                if (string.Equals(existingPanel.RootPath, volume.Root, StringComparison.OrdinalIgnoreCase)) continue;
                existingPanel.Close();
            }

            var serialKey = serial.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _config.Drives.TryGetValue(serialKey, out var savedSettings);
            var panel = new DrivePanelWindow(volume.Root, volume.Label, _viewSettings, _ejectService, savedSettings);
            void CapturePanel()
            {
                var latestLabel = _config.Drives.TryGetValue(serialKey, out var latest) ? latest.LastKnownLabel : volume.Label;
                _config.Drives[serialKey] = panel.CaptureSettings(latestLabel);
                ScheduleSave();
            }
            panel.SettingsChanged += (_, _) => CapturePanel();
            panel.Closed += (_, _) =>
            {
                CapturePanel();
                if (!_isExiting)
                {
                    _panels.Remove(serial);
                    RepositionWindows();
                }
            };
            _panels.Add(serial, panel);
            _config.Drives[serialKey] = panel.CaptureSettings(volume.Label);
            CapturePanel();
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

        // A temporarily disconnected monitor must not overwrite the saved selection.
        var monitor = monitors[Math.Clamp(_zoneSettings.MonitorIndex, 0, monitors.Count - 1)];
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
        settingsWindow.SettingsSaved += (_, _) => { RepositionWindows(); ScheduleSave(); };
        settingsWindow.ShowDialog();
    }

    private void ViewSettings_OnChanged(object? sender, EventArgs e)
    {
        foreach (var panel in _panels.Values)
        {
            panel.ApplyGlobalViewSettings();
        }
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (_isExiting) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveConfig()
    {
        _saveTimer.Stop();
        _config.GlobalView = _viewSettings.Capture();
        _configStore.Save(_config);
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _pollTimer.Stop();
        _saveTimer.Stop();
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
        SaveConfig();
        _viewSettings.Changed -= ViewSettings_OnChanged;
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
