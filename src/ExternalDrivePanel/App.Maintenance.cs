using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ExternalDrivePanel;

public partial class App
{
    private MenuItem? _updateStatusItem, _checkUpdateItem;
    private bool _backupBusy;

    private void AddMaintenanceMenu(ContextMenu menu)
    {
        MenuItem Item(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, e) => { e.Handled = true; action(); }; return item;
        }
        var index = menu.Items.Count - 1;
        _updateStatusItem = new MenuItem { IsEnabled = false };
        _checkUpdateItem = Item("지금 업데이트 확인", CheckUpdatesNow);
        menu.Items.Insert(index++, _updateStatusItem); menu.Items.Insert(index++, _checkUpdateItem);
        var backup = new MenuItem { Header = "설정·즐겨찾기 백업", IsEnabled = !_exitRequested && !_backupBusy && _topBar?.IsEnabled == true };
        backup.Items.Add(Item("백업 내보내기…", ExportSettings));
        backup.Items.Add(Item("백업 가져오기…", () => ImportSettings(false)));
        var previous = Item("이전 정상 설정으로 복원…", () => ImportSettings(true));
        previous.IsEnabled = File.Exists(_configStore.BackupPath); backup.Items.Add(previous);
        menu.Items.Insert(index++, backup);
        menu.Items.Insert(index++, Item("진단 로그 폴더 열기", OpenLogDirectory));
        menu.Items.Insert(index, new Separator());
        UpdateMaintenanceMenu();
    }
    private void Updates_OnStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) UpdateMaintenanceMenu();
        else Dispatcher.BeginInvoke(new Action(UpdateMaintenanceMenu));
    }
    private void UpdateMaintenanceMenu()
    {
        if (_updateStatusItem is not null) _updateStatusItem.Header = _updates.StatusText;
        if (_checkUpdateItem is not null) _checkUpdateItem.IsEnabled = !_exitRequested && _updates.State != UpdateCheckState.Checking;
    }
    private async void CheckUpdatesNow()
    {
        if (_exitRequested) return;
        var alreadyReady = _updates.IsReady;
        var state = await _updates.CheckNowAsync();
        if (!_isExiting && state != UpdateCheckState.Checking && (state != UpdateCheckState.Ready || alreadyReady))
            ShowNotification(_updates.StatusText, state == UpdateCheckState.Failed);
    }
    private void MaintenanceMessage(string message, bool error = false)
    {
        if (_topBar is not { } owner) return;
        DialogActivation.Show(owner, () => MessageBox.Show(owner, message, "설정·즐겨찾기 백업",
            MessageBoxButton.OK, error ? MessageBoxImage.Warning : MessageBoxImage.Information));
    }
    private async void ExportSettings()
    {
        if (_exitRequested || _backupBusy || _topBar is not { IsEnabled: true } owner) return;
        var dialog = new SaveFileDialog { Title = "설정·즐겨찾기 백업", Filter = "JSON 설정 파일|*.json", FileName = "ExternalDeskPanel-backup.json", AddExtension = true };
        if (DialogActivation.Show(owner, () => dialog.ShowDialog(owner)) != true) return;
        _backupBusy = true;
        try
        {
            var destination = Path.GetFullPath(dialog.FileName);
            if (string.Equals(destination, Path.GetFullPath(_configStore.FilePath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(destination, Path.GetFullPath(_configStore.BackupPath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("프로그램이 사용하는 설정 파일과 다른 위치를 선택해주세요.");
            _config.GlobalView = _viewSettings.Capture();
            // SaveAsync snapshots on this UI thread and performs I/O in its ordered worker.
            if (!await new ConfigStore(destination, keepBackup: false).SaveAsync(_config)) throw new IOException("백업을 저장하지 못했습니다. 저장 공간과 권한을 확인해주세요.");
            DiagnosticLog.Record("config.export.complete");
            MaintenanceMessage("설정과 즐겨찾기를 백업했습니다.\n백업에는 파일 자체나 Windows 시작프로그램 설정은 포함되지 않습니다.\n경로와 드라이브 정보가 있으므로 신뢰할 수 있는 곳에 보관해주세요.");
        }
        catch (Exception ex) { DiagnosticLog.Record("config.export.failed", ex); MaintenanceMessage(ex.Message, true); }
        finally { _backupBusy = false; }
    }
    private async void ImportSettings(bool previous)
    {
        if (_exitRequested || _backupBusy || _topBar is not { IsEnabled: true } owner) return;
        var path = _configStore.BackupPath;
        if (!previous)
        {
            var dialog = new OpenFileDialog { Title = "설정·즐겨찾기 백업 가져오기", Filter = "JSON 설정 파일|*.json", CheckFileExists = true };
            if (DialogActivation.Show(owner, () => dialog.ShowDialog(owner)) != true) return;
            path = dialog.FileName;
        }
        _backupBusy = true;
        try
        {
            var imported = await Task.Run(() => ConfigStore.ReadValidated(path));
            if (_exitRequested) return;
            if (DialogActivation.Show(owner, () => MessageBox.Show(owner,
                "현재 설정과 즐겨찾기를 백업 내용으로 교체할까요?\n진행 중인 파일 작업이 끝나면 패널을 다시 구성합니다. 파일 자체는 변경하지 않습니다.\n현재 정상 설정은 이전 설정 백업에 보관됩니다.",
                "설정 복원", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)) != MessageBoxResult.Yes) return;
            if (await ApplyImportedSettingsAsync(imported)) MaintenanceMessage("설정과 즐겨찾기를 복원했습니다. Windows 시작프로그램 설정은 변경하지 않았습니다.");
            else MaintenanceMessage("설정을 복원하지 못했습니다. 현재 설정은 유지됩니다. 저장 공간과 권한을 확인해주세요.", true);
        }
        catch (Exception ex) { DiagnosticLog.Record("config.import.failed", ex); MaintenanceMessage("설정을 가져오지 못했습니다.\n" + ex.Message, true); }
        finally { _backupBusy = false; }
    }
    private async Task<bool> ApplyImportedSettingsAsync(AppConfig imported)
    {
        if (_exitRequested) return false;
        if (FileOperationRegistry.IsBusy()) ShowNotification("파일 작업이 끝나면 설정을 복원합니다.", false);
        _exitRequested = true; _isExiting = true; _snapshotRevision++;
        _pollTimer.Stop(); _saveTimer.Stop(); _resizeController.Cancel();
        using var pause = FileOperationRegistry.Pause();
        var disabled = PanelWindows().Where(window => window.IsEnabled).ToArray();
        foreach (var window in disabled) window.IsEnabled = false;
        try
        {
            await FileOperationRegistry.WaitForIdleAsync();
            await Task.WhenAll(_panels.Values.Select(panel => panel.PrepareForShutdownAsync()));
            _config.GlobalView = _viewSettings.Capture();
            if (!await _configStore.SaveAsync(_config) || !await _configStore.SaveAsync(imported)) return false;
            var snapshot = _coordinator.Connected.Values.ToArray();
            foreach (var panel in _panels.Values.ToArray()) panel.Close();
            _panels.Clear(); _notices.Clear(); _newDriveWindow?.Close();
            _config = imported; _zoneSettings = _config.Zone; _coordinator = new DriveCoordinator(_config);
            _viewSettings.Restore(_config.GlobalView); _panelLayout.Reset();
            _isExiting = false;
            RefreshPanelsFromSnapshot(snapshot);
            DiagnosticLog.Record("config.import.complete"); return true;
        }
        finally
        {
            foreach (var window in disabled.Where(window => window.IsVisible)) window.IsEnabled = true;
            _isExiting = false; _exitRequested = false; _pollTimer.Start(); ScheduleNoticePump();
            UpdateMaintenanceMenu();
        }
    }
    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(DiagnosticLog.DefaultDirectory);
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(DiagnosticLog.DefaultDirectory); Process.Start(start);
        }
        catch (Exception ex) { DiagnosticLog.Record("logs.open.failed", ex); ShowNotification("로그 폴더를 열지 못했습니다.", true); }
    }
}
