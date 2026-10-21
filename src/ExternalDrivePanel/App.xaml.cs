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
    private readonly Func<IReadOnlyList<DriveSnapshot>> _snapshotProvider;
    private AppConfig _config = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private DriveCoordinator _coordinator = null!;
    private readonly Queue<DriveNotice> _notices = new();
    private bool _noticePumpScheduled;
    private bool _noticeShowing;
    private NewDriveWindow? _newDriveWindow;
    private DriveNotice? _activeNotice;

    public App() : this(new ConfigStore()) { }

    public App(ConfigStore configStore, Func<IReadOnlyList<DriveSnapshot>>? snapshotProvider = null)
    {
        _configStore = configStore;
        _snapshotProvider = snapshotProvider ?? _driveService.SnapshotDrives;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _config = _configStore.Load();
        _coordinator = new DriveCoordinator(_config);
        _zoneSettings = _config.Zone;
        _viewSettings.Restore(_config.GlobalView);
        _saveTimer.Tick += (_, _) => SaveConfig();

        _viewSettings.Changed += ViewSettings_OnChanged;
        _viewSettings.ViewModeApplied += ViewSettings_OnViewModeApplied;
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
        var contextMenu = new ContextMenu();
        contextMenu.Opened += (_, _) =>
        {
            RefreshPanels();
            TrayMenuBuilder.Populate(contextMenu, _coordinator, _config,
                () => TopBar_OnSettingsRequested(this, EventArgs.Empty), RegisterDrive,
                key => { if (_coordinator.ToggleHidden(key)) RegistrationChanged(); },
                UnregisterDrive, ExitApplication);
        };
        contextMenu.Closed += (_, _) => ScheduleNoticePump();

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
        if (_isExiting) return;
        var plan = _coordinator.Evaluate(_snapshotProvider());
        if (plan.ConfigChanged) ScheduleSave();
        foreach (var notice in plan.Notices) _notices.Enqueue(notice);
        if (_activeNotice is not null && !_coordinator.CanPrompt(_activeNotice) && _newDriveWindow?.IsVisible == true)
            _newDriveWindow.Close();
        var visible = plan.VisibleDrives.ToDictionary(drive => drive.Serial);
        foreach (var serial in _panels.Keys.ToList())
            if (!visible.TryGetValue(serial, out var drive)
                || !string.Equals(_panels[serial].RootPath, drive.Root, StringComparison.OrdinalIgnoreCase))
                _panels[serial].Close();

        foreach (var volume in plan.VisibleDrives)
        {
            var serial = volume.Serial;
            if (_panels.ContainsKey(serial)) continue;
            var serialKey = DriveCoordinator.Key(serial);
            var panel = new DrivePanelWindow(volume.Root, volume.Label, _viewSettings, _ejectService, _config.Drives[serialKey]);
            panel.NotificationRequested += ShowNotification;
            void CapturePanel()
            {
                // Do not resurrect a config entry deleted by Unregister. Keep registration
                // and hidden flags when capturing the panel's favorites/sort state.
                if (!_config.Drives.TryGetValue(serialKey, out var saved) || !saved.Registered) return;
                var state = panel.CaptureSettings(saved.LastKnownLabel);
                saved.Favorites = state.Favorites;
                saved.SortKey = state.SortKey;
                saved.SortReverse = state.SortReverse;
                saved.ViewMode = state.ViewMode;
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
            panel.Show();
        }

        RepositionWindows();
        ScheduleNoticePump();
    }

    private void RegistrationChanged()
    {
        RefreshPanels();
        ScheduleSave();
    }

    private void ShowNotification(string message, bool error)
    {
        if (_isExiting || _trayIcon is null) return;
        try
        {
            _trayIcon.ShowBalloonTip("External Drive Panel", message.Length > 240 ? message[..237] + "…" : message,
                error ? BalloonIcon.Error : BalloonIcon.Info);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Tray notification: {ex}"); }
    }

    private void RegisterDrive(uint serial)
    {
        RefreshPanels(); // Validate that the menu candidate is still connected.
        if (_coordinator.Register(serial)) RegistrationChanged();
    }

    private void UnregisterDrive(string key)
    {
        if (_topBar is null || !_config.Drives.TryGetValue(key, out var drive) || !drive.Registered) return;
        var name = _coordinator.DisplayName(key);
        var answer = DialogActivation.Show(_topBar, () => MessageBox.Show(_topBar,
            $"'{name}' 등록을 해제할까요?\n이 외장하드의 즐겨찾기도 함께 삭제됩니다. 파일은 삭제되지 않아요.",
            "외장하드 등록 해제", MessageBoxButton.YesNo, MessageBoxImage.Question));
        if (answer == MessageBoxResult.Yes && _coordinator.Unregister(key)) RegistrationChanged();
    }

    private void ScheduleNoticePump()
    {
        if (_isExiting || _noticeShowing || _noticePumpScheduled || _notices.Count == 0) return;
        _noticePumpScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _noticePumpScheduled = false;
            ShowNextNotice();
        }));
    }

    private void ShowNextNotice()
    {
        if (_isExiting || _noticeShowing || _topBar is null || !_topBar.IsEnabled
            || _trayIcon?.ContextMenu?.IsOpen == true) return;
        while (_notices.TryDequeue(out var notice))
        {
            if (!_coordinator.CanPrompt(notice)) continue;
            _noticeShowing = true;
            _activeNotice = notice;
            try
            {
                var dialog = new NewDriveWindow(_topBar, _coordinator.Connected[notice.Serial]);
                _newDriveWindow = dialog;
                DialogActivation.Show(_topBar, () => dialog.ShowDialog());
                if (!_isExiting && _coordinator.CanPrompt(notice))
                {
                    if (dialog.Choice == NewDriveChoice.Register) RegisterDrive(notice.Serial);
                    else if (dialog.Choice == NewDriveChoice.Ignore && _coordinator.Ignore(notice)) ScheduleSave();
                }
            }
            finally
            {
                _newDriveWindow = null;
                _activeNotice = null;
                _noticeShowing = false;
            }
            // Defer each dialog to a separate dispatch; ticks may run inside ShowDialog.
            ScheduleNoticePump();
            return;
        }
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

        var panels = _coordinator.RegisteredOrder.Select(key => uint.Parse(key, System.Globalization.CultureInfo.InvariantCulture))
            .Where(serial => _panels.ContainsKey(serial)).Select(serial => _panels[serial]).ToList();
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
            if (_topBar is not null) DialogActivation.Show(_topBar,
                () => MessageBox.Show(_topBar, "연결된 모니터 정보를 가져올 수 없습니다.", "화면 영역 설정", MessageBoxButton.OK, MessageBoxImage.Warning));
            return;
        }

        var settingsWindow = new ZoneSettingsWindow(_zoneSettings, monitors) { Owner = _topBar };
        settingsWindow.SettingsSaved += (_, _) => { RepositionWindows(); ScheduleSave(); };
        if (_topBar is not null) DialogActivation.Show(_topBar, () => settingsWindow.ShowDialog());
        ScheduleNoticePump();
    }

    private void ViewSettings_OnChanged(object? sender, EventArgs e)
    {
        foreach (var panel in _panels.Values)
        {
            panel.ApplyGlobalViewSettings();
        }
        ScheduleSave();
    }

    private void ViewSettings_OnViewModeApplied(object? sender, EventArgs e)
    {
        PanelViewPolicy.ApplyGlobally(_config, _viewSettings.ViewMode);
        foreach (var panel in _panels.Values) panel.ClearViewModeOverride();
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
        _notices.Clear();
        _newDriveWindow?.Close();
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
        _viewSettings.ViewModeApplied -= ViewSettings_OnViewModeApplied;
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
