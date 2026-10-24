namespace ExternalDrivePanel;

public partial class App
{
    private async Task<EjectResult> EjectDriveAsync(string root)
    {
        if (_exitRequested || _ejectInProgress || _topBar?.IsEnabled != true)
            return EjectResult.Failed("다른 설정·종료·제거 작업을 완료한 뒤 다시 시도해주세요.");
        if (_panels.Values.Any(panel => !panel.IsEnabled))
            return EjectResult.Failed("열려 있는 파일 작업 대화상자를 먼저 닫은 뒤 다시 시도해주세요.");
        _ejectInProgress = true; _exitRequested = true; _snapshotRevision++;
        _pollTimer.Stop();
        var bar = _topBar; var footer = _updateBar;
        var footerEnabled = footer?.IsEnabled == true;
        bar.IsEnabled = false; if (footer is not null) footer.IsEnabled = false;
        try
        {
            // An in-flight drive snapshot must finish before any device request.
            if (_pollWork is { } poll) { try { await poll; } catch { } }
            return await EjectWorkflow.RunAsync(root, _ejectService, _panels.Values.Cast<IEjectParticipant>().ToArray());
        }
        finally
        {
            _ejectInProgress = false; _exitRequested = false;
            if (!_isExiting)
            {
                if (bar.IsVisible) bar.IsEnabled = true;
                if (footer?.IsVisible == true) footer.IsEnabled = footerEnabled;
                _pollTimer.Start();
                if (_updates.IsReady) Updates_OnReady(this, EventArgs.Empty);
                ScheduleNoticePump();
            }
        }
    }
}
