using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;

namespace ExternalDrivePanel;

public partial class App : Application
{
    private readonly DriveService _driveService = new();
    private readonly Dictionary<uint, DrivePanelWindow> _panels = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TaskbarIcon? _trayIcon;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _trayIcon = CreateTrayIcon();
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

            var panel = new DrivePanelWindow(volume.Root, volume.Label);
            panel.Closed += (_, _) =>
            {
                if (!_isExiting)
                {
                    _panels.Remove(serial);
                }
            };
            _panels.Add(serial, panel);
            panel.Show();
        }

        RepositionPanels();
    }

    private void RepositionPanels()
    {
        var panels = _panels.Values.ToList();
        for (var index = 0; index < panels.Count; index++)
        {
            panels[index].SetPlacement(index, panels.Count);
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
        _trayIcon?.Dispose();
        _trayIcon = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pollTimer.Stop();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
