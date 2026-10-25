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
    private readonly PanelLayoutState _panelLayout = new();
    private DrivePanelWindow[] _layoutPanels = [];
    private bool _layoutRefreshScheduled;
    private LayoutResizeController _resizeController = null!;
    private readonly EjectService _ejectService;
    private readonly DeviceReconnectService _reconnectService;
    private ZoneSettings _zoneSettings = new();
    private readonly GlobalViewSettings _viewSettings = new();
    private readonly Dictionary<uint, DrivePanelWindow> _panels = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TaskbarIcon? _trayIcon;
    private System.Drawing.Icon? _trayImage;
    private TopBarWindow? _topBar;
    private bool _isExiting;
    private bool _isHelperProcess;
    private bool _exitRequested;
    private SingleInstanceGuard? _instance;
    private readonly AutoUpdateService _updates;
    private readonly bool _enableUpdates;
    private readonly bool _development;
    private DevelopmentSession? _developmentSession;
    private UpdateNotificationWindow? _updateBar;
    private readonly IUpdateDialogs _updateDialogs;
    private bool _updateArmed;
    private PanelRevealService? _panelReveal;
    private DiagnosticLog? _diagnostics;
    private string LogDirectory => _development ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_configStore.FilePath)!, "Logs") : DiagnosticLog.DefaultDirectory;
    public bool IsSoftwareRendering { get; private set; }
    private readonly ConfigStore _configStore;
    private readonly Func<IReadOnlyList<DriveSnapshot>> _snapshotProvider;
    private readonly bool _customSnapshotProvider;
    private bool _polling;
    private Task<IReadOnlyList<DriveSnapshot>>? _pollWork;
    private bool _ejectInProgress;
    private bool _driveRefreshInProgress;
    private bool DeviceTransitionInProgress => _ejectInProgress || _driveRefreshInProgress;
    private long _snapshotRevision;
    private AppConfig _config = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private DriveCoordinator _coordinator = null!;
    private readonly Queue<DriveNotice> _notices = new();
    private bool _noticePumpScheduled;
    private bool _noticeShowing;
    private NewDriveWindow? _newDriveWindow;
    private DriveNotice? _activeNotice;

    public App() : this(new ConfigStore()) { }

    public App(ConfigStore configStore, Func<IReadOnlyList<DriveSnapshot>>? snapshotProvider = null, AutoUpdateService? updates = null, IUpdateDialogs? updateDialogs = null, EjectService? ejectService = null, bool development = false, DeviceReconnectService? reconnectService = null)
    {
        _configStore = configStore;
        _ejectService = ejectService ?? new EjectService();
        _reconnectService = reconnectService ?? new DeviceReconnectService();
        _snapshotProvider = snapshotProvider ?? _driveService.SnapshotDrives;
        _customSnapshotProvider = snapshotProvider is not null;
        _updates = updates ?? new AutoUpdateService();
        _development = development;
        _enableUpdates = !development && (updates is not null || !_customSnapshotProvider);
        _updateDialogs = updateDialogs ?? new UpdateDialogs();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (PdfPreviewWorker.IsRequest(e.Args))
        {
            _isHelperProcess = true;
            Shutdown(PdfPreviewWorker.Run(e.Args));
            return;
        }
        if (VolumeLabelService.TryRunHelper(e.Args, out var helperExitCode))
        {
            _isHelperProcess = true;
            Shutdown(helperExitCode);
            return;
        }
        if (DeviceReconnectService.TryRunHelper(e.Args, out var reconnectExitCode))
        {
            _isHelperProcess = true;
            Shutdown(reconnectExitCode);
            return;
        }
        try { _instance = SingleInstanceGuard.TryAcquire(_configStore.FilePath); }
        catch (Exception ex)
        {
            _isHelperProcess = true;
            MessageBox.Show("설정 저장 위치에 접근할 수 없습니다.\n" + ex.Message, "External Drive Panel");
            Shutdown(1); return;
        }
        if (_instance is null) { _isHelperProcess = true; Shutdown(); return; }
        if (!_customSnapshotProvider)
        {
            _diagnostics = new DiagnosticLog(LogDirectory);
            DiagnosticLog.Current = _diagnostics;
            DiagnosticLog.Record("app.start.v" + typeof(App).Assembly.GetName().Version?.ToString(3));
            DispatcherUnhandledException += App_OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += App_OnDomainException;
        }
        // One-run diagnostic switch: isolate GPU rendering without changing the
        // user's saved settings, window geometry or normal-launch defaults.
        IsSoftwareRendering = e.Args.Contains("--software-rendering", StringComparer.OrdinalIgnoreCase);
        if (IsSoftwareRendering)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SessionEnding += App_OnSessionEnding;

        _config = _configStore.Load();
        _coordinator = new DriveCoordinator(_config);
        _zoneSettings = _config.Zone;
        _viewSettings.Restore(_config.GlobalView);
        _saveTimer.Tick += (_, _) => SaveConfig();

        _viewSettings.Changed += ViewSettings_OnChanged;
        _viewSettings.ViewModeApplied += ViewSettings_OnViewModeApplied;
        _trayIcon = CreateTrayIcon();
        _topBar = new TopBarWindow(_viewSettings);
        if (_development)
        {
            _topBar.Title = "External Drive Panel — 개발용";
            _topBar.ShowInTaskbar = true;
            ((TextBlock)_topBar.FindName("AppNameText")).Text = "개발용 · EXTERNAL DRIVE PANEL";
            _developmentSession = new DevelopmentSession(() =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                try { Dispatcher.BeginInvoke(new Action(ExitApplication)); }
                catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted) { }
            });
        }
        PanelActivation.Attach(_topBar, PanelWindows);
        _resizeController = new LayoutResizeController(_panelLayout, RepositionWindows);
        _resizeController.Attach(_topBar);
        _topBar.SettingsRequested += TopBar_OnSettingsRequested;
        _topBar.RefreshRequested += async (_, _) => await RefreshDrivesAsync();
        MainWindow = _topBar;
        RepositionWindows(); // Save physical bounds before the first HWND becomes visible.
        _topBar.Show();

        _pollTimer.Tick += PollTimer_OnTick;
        _pollTimer.Start();
        RefreshPanels();
        if (!_customSnapshotProvider)
            _panelReveal = new PanelRevealService(PanelWindows, () => _panelLayout.Cell, RestorePanels, () => _isExiting || _exitRequested);
        _updates.UpdateReady += Updates_OnReady;
        _updates.StateChanged += Updates_OnStateChanged;
        if (_enableUpdates) _updates.Start(ShowNotification);
    }

    private TaskbarIcon CreateTrayIcon()
    {
        var contextMenu = new ContextMenu();
        contextMenu.Opened += (_, _) =>
        {
            RefreshPanels();
            TrayMenuBuilder.Populate(contextMenu, _coordinator, _config,
                () => TopBar_OnSettingsRequested(this, EventArgs.Empty), RegisterDrive,
                key => { if (!_exitRequested && _coordinator.ToggleHidden(key)) RegistrationChanged(); },
                UnregisterDrive, ExitApplication);
            AddMaintenanceMenu(contextMenu);
        };
        contextMenu.Closed += (_, _) => ScheduleNoticePump();

        _trayImage = PanelTrayIcon.Create();
        return new TaskbarIcon
        {
            ToolTipText = _development ? "External Drive Panel — 개발용" : IsSoftwareRendering ? "External Drive Panel — 소프트웨어 렌더링 (진단)" : "External Drive Panel",
            Icon = _trayImage,
            ContextMenu = contextMenu
        };
    }

    private async void PollTimer_OnTick(object? sender, EventArgs e)
    {
        if (_customSnapshotProvider) { RefreshPanels(); return; }
        if (_polling || _isExiting || DeviceTransitionInProgress) return;
        _polling = true; var revision = _snapshotRevision;
        try
        {
            _pollWork = Task.Run(_snapshotProvider);
            var snapshot = await _pollWork;
            if (!_isExiting && revision == _snapshotRevision) RefreshPanelsFromSnapshot(snapshot);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Drive polling: {ex}"); DiagnosticLog.Record("drives.poll.failed", ex); }
        finally { _polling = false; _pollWork = null; }
    }

    private void RefreshPanels()
    {
        if (_isExiting || DeviceTransitionInProgress) return;
        _snapshotRevision++;
        RefreshPanelsFromSnapshot(_snapshotProvider());
    }

    private void RefreshPanelsFromSnapshot(IReadOnlyList<DriveSnapshot> snapshot)
    {
        if (_isExiting || DeviceTransitionInProgress) return;
        var plan = _coordinator.Evaluate(snapshot);
        if (plan.ConfigChanged) ScheduleSave();
        foreach (var notice in plan.Notices) _notices.Enqueue(notice);
        if (_activeNotice is not null && !_coordinator.CanPrompt(_activeNotice) && _newDriveWindow?.IsVisible == true)
            _newDriveWindow.Close();
        var visible = plan.VisibleDrives.ToDictionary(drive => drive.Serial);
        var newPanels = new List<DrivePanelWindow>();
        foreach (var serial in _panels.Keys.ToList())
            if (!visible.TryGetValue(serial, out var drive)
                || !string.Equals(_panels[serial].RootPath, drive.Root, StringComparison.OrdinalIgnoreCase))
                _panels[serial].Close();

        foreach (var volume in plan.VisibleDrives)
        {
            var serial = volume.Serial;
            if (_panels.TryGetValue(serial, out var existingPanel)) { existingPanel.UpdateVolumeLabel(volume.Label); continue; }
            var serialKey = DriveCoordinator.Key(serial);
            var panel = new DrivePanelWindow(volume.Root, volume.Label, _viewSettings, _ejectService, _config.Drives[serialKey]);
            panel.EjectRequested = EjectDriveAsync;
            PanelActivation.Attach(panel, PanelWindows);
            _resizeController.Attach(panel);
            panel.LayoutMetricsChanged += (_, _) => ScheduleLayoutRefresh();
            panel.NotificationRequested += ShowNotification;
            panel.VolumeLabelChanged += label => { if (_config.Drives.TryGetValue(serialKey, out var saved)) { saved.LastKnownLabel = label; ScheduleSave(); } };
            void CapturePanel()
            {
                // Do not resurrect a config entry deleted by Unregister. Keep registration
                // and hidden flags when capturing the panel's favorites/sort state.
                if (!_config.Drives.TryGetValue(serialKey, out var saved) || !saved.Registered) return;
                var state = panel.CaptureSettings(saved.LastKnownLabel);
                var layoutChanged = saved.Favorites.Count != state.Favorites.Count || saved.ViewMode != state.ViewMode;
                saved.Favorites = state.Favorites;
                saved.SortKey = state.SortKey;
                saved.SortReverse = state.SortReverse;
                saved.ViewMode = state.ViewMode;
                saved.PreferredLabel = state.PreferredLabel;
                ScheduleSave();
                if (layoutChanged && !_isExiting && !_exitRequested) RepositionWindows();
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
            newPanels.Add(panel);
        }

        RepositionWindows();
        foreach (var panel in newPanels) panel.Show();
        if (newPanels.Count > 0) RepositionWindows(); // HWNDs now report their target monitor DPI.
        ScheduleNoticePump();
    }

    private IEnumerable<Window> PanelWindows() => _panels.Values.Cast<Window>()
        .Concat(new Window?[] { _topBar, _updateBar }.OfType<Window>());

    private void RestorePanels()
    {
        var windows = PanelWindows().Where(window => window.IsVisible).ToArray();
        foreach (var window in windows)
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        var target = Windows.OfType<Window>().LastOrDefault(window => window.IsVisible && window.IsEnabled && window is not PanelRevealWindow && !windows.Contains(window))
            ?? windows.FirstOrDefault(window => window.IsEnabled);
        if (target is not null) PanelActivation.Raise(target, windows.Concat(Windows.OfType<Window>().Where(window => window.IsVisible && window is not PanelRevealWindow)));
    }

    private void Updates_OnReady(object? sender, EventArgs e)
    {
        if (_isExiting || DeviceTransitionInProgress || _updateBar is not null) return;
        _resizeController.Cancel();
        _updateBar = new UpdateNotificationWindow();
        _updateBar.RestartRequested += async (_, _) => await ShutdownApplicationAsync(update: true);
        PanelActivation.Attach(_updateBar, PanelWindows);
        _resizeController.Attach(_updateBar);
        _panelLayout.FooterHeight = UpdateNotificationWindow.BarHeight;
        RepositionWindows();
        _updateBar.Show();
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
        if (_exitRequested) return;
        RefreshPanels(); // Validate that the menu candidate is still connected.
        if (_coordinator.Register(serial)) RegistrationChanged();
    }

    private void UnregisterDrive(string key)
    {
        if (_exitRequested) return;
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

    private void ScheduleLayoutRefresh()
    {
        if (_isExiting || _exitRequested || _layoutRefreshScheduled) return;
        _layoutRefreshScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _layoutRefreshScheduled = false;
            if (!_isExiting && !_exitRequested) RepositionWindows();
        }));
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
        var panels = _coordinator.RegisteredOrder.Select(key => uint.Parse(key, System.Globalization.CultureInfo.InvariantCulture))
            .Where(serial => _panels.ContainsKey(serial)).Select(serial => _panels[serial]).ToList();
        if (!_layoutPanels.SequenceEqual(panels))
        {
            _resizeController.Cancel();
            _panelLayout.ResetDistribution();
            _layoutPanels = panels.ToArray();
        }
        _panelLayout.Configure(cell, panels.Select(panel => panel.FavoriteLayoutProfile).ToArray(), _zoneSettings.SplitDirection, _zoneSettings.ResponsivePanels);
        using var placementBatch = PhysicalWindowPlacement.BeginBatch();
        _topBar.SetPlacement(_layoutCalculator.CalculateTopBar(_panelLayout.Cell));
        var rectangles = _panelLayout.Panels();
        for (var index = 0; index < panels.Count; index++)
        {
            panels[index].SetPlacement(rectangles[index]);
        }
        _updateBar?.SetPlacement(_panelLayout.Footer);
    }

    private void TopBar_OnSettingsRequested(object? sender, EventArgs e)
    {
        if (_exitRequested) return;
        var monitors = _monitorService.GetMonitors();
        if (monitors.Count == 0)
        {
            if (_topBar is not null) DialogActivation.Show(_topBar,
                () => MessageBox.Show(_topBar, "연결된 모니터 정보를 가져올 수 없습니다.", "화면 영역 설정", MessageBoxButton.OK, MessageBoxImage.Warning));
            return;
        }

        var settingsWindow = new ZoneSettingsWindow(_zoneSettings, monitors) { Owner = _topBar };
        if (_development) settingsWindow.DisableStartupForDevelopment();
        settingsWindow.SettingsSaved += (_, _) => { ResetManualLayout(); ScheduleSave(); };
        if (_topBar is not null) DialogActivation.Show(_topBar, () => settingsWindow.ShowDialog());
        ScheduleNoticePump();
    }

    private void ResetManualLayout()
    {
        _resizeController.Cancel();
        _panelLayout.Reset();
        RepositionWindows();
    }

    private void ViewSettings_OnChanged(object? sender, EventArgs e)
    {
        foreach (var panel in _panels.Values)
        {
            panel.ApplyGlobalViewSettings();
        }
        RepositionWindows();
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
        _ = _configStore.SaveAsync(_config);
    }

    private void App_OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        // Windows can still force an exit explicitly; never claim to protect power loss.
        if (FileOperationRegistry.IsBusy()) e.Cancel = true;
    }

    private async void ExitApplication() => await ShutdownApplicationAsync(update: false);

    private async Task ShutdownApplicationAsync(bool update)
    {
        if (_exitRequested) return;
        if (update && !_updates.IsReady) return;
        _exitRequested = true;
        using var pause = FileOperationRegistry.Pause();
        if (update && _topBar is { } updateOwner && !_updateDialogs.ConfirmRestart(updateOwner))
        { _exitRequested = false; return; }
        if (!update && FileOperationRegistry.IsBusy() && _topBar is { } owner
            && DialogActivation.Show(owner, () => MessageBox.Show(owner,
                "진행 중인 복사·이동·압축 작업이 있습니다. 작업이 끝난 뒤 종료할까요?",
                "작업 완료 후 종료", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)) != MessageBoxResult.Yes)
        { _exitRequested = false; return; }
        _isExiting = true;
        _resizeController.Cancel();
        _pollTimer.Stop();
        _saveTimer.Stop();
        _notices.Clear();
        _newDriveWindow?.Close();
        _updateBar?.SetWaiting(true);
        var disabled = update ? Windows.OfType<Window>().Where(window => window.IsEnabled).ToArray() : [];
        foreach (var window in disabled) window.IsEnabled = false;
        try
        {
            await FileOperationRegistry.WaitForIdleAsync();
            await Task.WhenAll(_panels.Values.Select(panel => panel.PrepareForShutdownAsync()));
            if (update)
            {
                _config.GlobalView = _viewSettings.Capture();
                var saved = await _configStore.SaveAsync(_config);
                // Start the updater's 60-second exit wait only AFTER all potentially
                // slow file operations, preview cleanup and config saving have ended.
                if (!saved || !_updates.TryApplyAfterExit(restart: true))
                {
                    foreach (var window in disabled) window.IsEnabled = true;
                    _isExiting = false; _exitRequested = false; _pollTimer.Start();
                    _updateBar?.SetWaiting(false);
                    if (_topBar is { } errorOwner) _updateDialogs.ShowError(errorOwner,
                        !saved ? "설정을 저장하지 못해 업데이트를 중단했습니다. 저장 공간과 권한을 확인한 뒤 다시 시도해주세요."
                        : "업데이트를 시작하지 못했습니다. 다시 시도해주세요.\n" + _updates.LastApplyError);
                    return;
                }
                _updateArmed = true;
            }
            foreach (var panel in _panels.Values.ToList())
            {
                panel.Close();
            }
            _panels.Clear();
            _updateBar?.CloseForShutdown(); _updateBar = null;

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
        catch (Exception ex)
        {
            if (_updateArmed)
            {
                System.Diagnostics.Debug.WriteLine($"Update shutdown cleanup: {ex}");
                Shutdown(); // The updater is already waiting; never reopen a half-closed UI.
                return;
            }
            foreach (var window in disabled.Where(window => window.IsVisible)) window.IsEnabled = true;
            _isExiting = false; _exitRequested = false; _pollTimer.Start();
            _updateBar?.SetWaiting(false);
            if (_topBar is { } recoveryOwner) DialogActivation.Show(recoveryOwner, () => MessageBox.Show(recoveryOwner,
                "종료/업데이트 준비 중 문제가 발생했습니다. 다시 시도해주세요.\n" + ex.Message,
                "External Drive Panel", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _developmentSession?.Dispose();
        if (_isHelperProcess) { base.OnExit(e); return; }
        _pollTimer.Stop();
        _saveTimer.Stop();
        _config.GlobalView = _viewSettings.Capture();
        if (!_updateArmed) _configStore.Save(_config); // Update preflight already flushed; do not delay the updater's exit timeout with another disk write.
        _updates.UpdateReady -= Updates_OnReady;
        _updates.StateChanged -= Updates_OnStateChanged;
        _updates.Dispose();
        _panelReveal?.Dispose(); _panelReveal = null;
        _viewSettings.Changed -= ViewSettings_OnChanged;
        _viewSettings.ViewModeApplied -= ViewSettings_OnViewModeApplied;
        SessionEnding -= App_OnSessionEnding;
        _trayIcon?.Dispose();
        base.OnExit(e);
        _trayImage?.Dispose();
        _trayImage = null;
        _instance?.Dispose(); _instance = null;
        DispatcherUnhandledException -= App_OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= App_OnDomainException;
        DiagnosticLog.Record("app.exit"); _diagnostics?.Dispose(); _diagnostics = null;
    }

    private void App_OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    { DiagnosticLog.Record("app.dispatcher.unhandled", e.Exception); _diagnostics?.Dispose(); }
    private void App_OnDomainException(object sender, UnhandledExceptionEventArgs e)
    { DiagnosticLog.Record("app.domain.unhandled", e.ExceptionObject as Exception); _diagnostics?.Dispose(); }
}
