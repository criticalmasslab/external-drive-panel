using System.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace ExternalDrivePanel;

public enum UpdateCheckState { Idle, Checking, Ready, UpToDate, Failed, Unavailable }

/// <summary>Download in background; apply only after the app has drained writers and saved config.</summary>
public sealed class AutoUpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/criticalmasslab/external-drive-panel";
    private readonly CancellationTokenSource _stop = new();
    private IAppUpdateBackend? _backend;
    private Task? _loop;
    private readonly object _checkLock = new();
    private Task<UpdateCheckState>? _activeCheck;
    private Action<string, bool>? _notify;
    private volatile bool _ready;
    private readonly Func<IAppUpdateBackend> _createBackend;
    private readonly TimeSpan _initialDelay, _interval;
    public Task Completion => _loop ?? Task.CompletedTask;
    public bool IsReady => _ready;
    public event EventHandler? UpdateReady;
    public event EventHandler? StateChanged;
    public UpdateCheckState State { get; private set; }
    public string StatusText => State switch
    {
        UpdateCheckState.Checking => "업데이트 확인·다운로드 중…",
        UpdateCheckState.Ready => "업데이트 준비 완료 — 패널 하단에서 다시 시작",
        UpdateCheckState.UpToDate => "최신 버전입니다",
        UpdateCheckState.Failed => "업데이트 확인 실패 — 연결을 확인하고 다시 시도하세요",
        UpdateCheckState.Unavailable => "설치본에서만 자동 업데이트를 사용할 수 있습니다",
        _ => "아직 업데이트를 확인하지 않았습니다"
    };
    public string? LastApplyError { get; private set; }
    public AutoUpdateService(Func<IAppUpdateBackend>? createBackend = null, TimeSpan? initialDelay = null, TimeSpan? interval = null)
    {
        _createBackend = createBackend ?? (() => new VelopackUpdateBackend());
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(20);
        _interval = interval ?? TimeSpan.FromHours(6);
        if (_initialDelay < TimeSpan.Zero || _interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }
    public void Start(Action<string, bool> notify)
    {
        _notify ??= notify;
        _loop ??= RunAsync();
    }
    public Task<UpdateCheckState> CheckNowAsync()
    {
        lock (_checkLock)
        {
            if (_activeCheck is { IsCompleted: false }) return _activeCheck;
            return _activeCheck = CheckCoreAsync();
        }
    }
    private void SetState(UpdateCheckState state) { State = state; StateChanged?.Invoke(this, EventArgs.Empty); }
    private async Task<UpdateCheckState> CheckCoreAsync()
    {
        await Task.Yield(); // Publish the shared task before any state events can reenter.
        if (_stop.IsCancellationRequested) return State;
        if (_ready) return UpdateCheckState.Ready;
        try
        {
            _backend ??= _createBackend();
            if (!_backend.IsInstalled) { SetState(UpdateCheckState.Unavailable); return State; }
            SetState(UpdateCheckState.Checking);
            var ready = await _backend.PrepareUpdateAsync(_stop.Token);
            if (_stop.IsCancellationRequested) return State;
            if (ready)
            {
                _ready = true; SetState(UpdateCheckState.Ready);
                DiagnosticLog.Record("update.ready");
                UpdateReady?.Invoke(this, EventArgs.Empty);
                _notify?.Invoke("새 버전을 준비했습니다. 패널 하단의 ‘업데이트 후 다시 시작’을 눌러주세요.", false);
            }
            else SetState(UpdateCheckState.UpToDate);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check/download: {ex}"); DiagnosticLog.Record("update.check.failed", ex);
            SetState(UpdateCheckState.Failed);
        }
        return State;
    }
    private async Task RunAsync()
    {
        try
        {
            _backend ??= _createBackend();
            if (!_backend.IsInstalled) { SetState(UpdateCheckState.Unavailable); return; }
            await Task.Delay(_initialDelay, _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                if (_ready || await CheckNowAsync() == UpdateCheckState.Ready) return;
                await Task.Delay(_interval, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Debug.WriteLine($"Auto update: {ex}"); DiagnosticLog.Record("update.loop.failed", ex); }
    }
    public bool TryApplyAfterExit(bool restart)
    {
        LastApplyError = null;
        try
        {
            if (_ready && !FileOperationRegistry.IsBusy())
            {
                _backend!.ApplyAfterExit(restart);
                _ready = false;
                _stop.Cancel();
                return true;
            }
        }
        catch (Exception ex) { LastApplyError = ex.Message; Debug.WriteLine($"Update on exit: {ex}"); DiagnosticLog.Record("update.apply.failed", ex); }
        return false;
    }
    public void Dispose() => _stop.Cancel();
}

public interface IAppUpdateBackend
{
    bool IsInstalled { get; }
    Task<bool> PrepareUpdateAsync(CancellationToken token);
    void ApplyAfterExit(bool restart);
}

internal sealed class VelopackUpdateBackend : IAppUpdateBackend
{
    private readonly UpdateManager _manager = new(new GithubSource(AutoUpdateService.RepositoryUrl, null, false));
    private VelopackAsset? _ready;
    public bool IsInstalled => _manager.IsInstalled && !_manager.IsPortable;
    public async Task<bool> PrepareUpdateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ready = _manager.UpdatePendingRestart;
        if (_ready is not null) return true;
        var update = await _manager.CheckForUpdatesAsync();
        token.ThrowIfCancellationRequested();
        if (update is null) return false;
        await _manager.DownloadUpdatesAsync(update, cancelToken: token);
        token.ThrowIfCancellationRequested();
        _ready = update.TargetFullRelease;
        return true;
    }
    public void ApplyAfterExit(bool restart)
    {
        if (_ready is null) throw new InvalidOperationException("설치할 업데이트가 준비되지 않았습니다.");
        _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: restart);
    }
}
