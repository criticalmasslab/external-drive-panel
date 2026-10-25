using System.Windows.Interop;

namespace ExternalDrivePanel;

public partial class App
{
    private async Task RefreshDrivesAsync()
    {
        if (_isExiting || _exitRequested || DeviceTransitionInProgress || _topBar is not { IsEnabled: true } bar) return;
        if (_panels.Values.Any(panel => !panel.IsEnabled))
        { ShowNotification("열려 있는 파일 작업 대화상자를 닫은 뒤 새로고침해주세요.", false); return; }
        _driveRefreshInProgress = true; _exitRequested = true; _snapshotRevision++;
        _pollTimer.Stop(); bar.IsEnabled = false;
        var panels = _panels.Values.ToArray();
        foreach (var panel in panels) panel.IsEnabled = false;
        var footer = _updateBar; var footerEnabled = footer?.IsEnabled == true;
        if (footer is not null) footer.IsEnabled = false;
        using var pause = FileOperationRegistry.Pause();
        IReadOnlyList<DriveSnapshot>? snapshot = null;
        DeviceReconnectResult? result = null;
        var before = _coordinator.Connected.Keys.ToHashSet();
        var failed = false;
        try
        {
            ShowNotification(FileOperationRegistry.IsBusy()
                ? "진행 중인 파일 작업이 끝나면 외장하드를 다시 확인합니다."
                : "외장하드를 다시 확인합니다. 필요한 경우 Windows 관리자 권한 확인창이 표시됩니다.", false);
            await FileOperationRegistry.WaitForIdleAsync();
            if (_pollWork is { } poll) { try { await poll; } catch { } }
            result = await _reconnectService.ReconnectAsync(new WindowInteropHelper(bar).Handle);
            if (result.Candidates > 0 && !result.Cancelled) await Task.Delay(500);
            snapshot = await Task.Run(_snapshotProvider);
        }
        catch (Exception ex) { failed = true; DiagnosticLog.Record("drives.refresh.failed", ex); }
        finally
        {
            _driveRefreshInProgress = false; _exitRequested = false;
            if (!_isExiting)
            {
                if (bar.IsVisible) bar.IsEnabled = true;
                foreach (var panel in panels) if (panel.IsVisible) panel.IsEnabled = true;
                if (footer?.IsVisible == true) footer.IsEnabled = footerEnabled;
                try { if (snapshot is not null) RefreshPanelsFromSnapshot(snapshot); }
                catch (Exception ex) { failed = true; DiagnosticLog.Record("drives.refresh.apply.failed", ex); }
                finally
                {
                    _pollTimer.Start();
                    if (_updates.IsReady) Updates_OnReady(this, EventArgs.Empty);
                    ScheduleNoticePump();
                }
            }
        }
        if (_isExiting) return;
        var found = snapshot?.Count(drive => !before.Contains(drive.Serial)) ?? 0;
        var message = found > 0 ? $"외장하드 {found}개를 다시 찾았습니다." +
                (failed || result is { Error: not 0 } ? " 일부 장치는 재연결하지 못했습니다." : "")
            : result?.Cancelled == true ? "USB 재연결을 취소했습니다. 드라이브 목록은 새로고쳤습니다."
            : failed || result is { Error: not 0 } ? "장치를 다시 연결하지 못했습니다. USB 케이블을 분리했다가 다시 연결해주세요."
            : result is { Candidates: > 0 } ? "USB 재연결을 요청했습니다. Windows가 드라이브를 인식하면 패널이 다시 표시됩니다. 돌아오지 않으면 케이블을 다시 연결해주세요."
            : "드라이브 목록을 새로고쳤습니다. 제거된 장치가 나타나지 않으면 USB 케이블을 다시 연결해주세요.";
        ShowNotification(message, result?.Cancelled != true && found == 0 && (failed || result is { Error: not 0 }));
        DiagnosticLog.Record(found > 0 ? "drives.refresh.found" : "drives.refresh.complete");
    }
}
