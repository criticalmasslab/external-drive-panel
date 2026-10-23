using System.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace ExternalDrivePanel;

/// <summary>Download in background; apply only after the app has drained writers and saved config.</summary>
public sealed class AutoUpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/criticalmasslab/external-drive-panel";
    private readonly CancellationTokenSource _stop = new();
    private IAppUpdateBackend? _backend;
    private Task? _loop;
    private volatile bool _ready;
    private readonly Func<IAppUpdateBackend> _createBackend;
    private readonly TimeSpan _initialDelay, _interval;
    public Task Completion => _loop ?? Task.CompletedTask;
    public AutoUpdateService(Func<IAppUpdateBackend>? createBackend = null, TimeSpan? initialDelay = null, TimeSpan? interval = null)
    {
        _createBackend = createBackend ?? (() => new VelopackUpdateBackend());
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(20);
        _interval = interval ?? TimeSpan.FromHours(6);
        if (_initialDelay < TimeSpan.Zero || _interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }
    public void Start(Action<string, bool> notify)
    {
        _loop ??= RunAsync(notify);
    }
    private async Task RunAsync(Action<string, bool> notify)
    {
        try
        {
            _backend = _createBackend();
            if (!_backend.IsInstalled) return; // Portable/debug copies do not self-replace.
            await Task.Delay(_initialDelay, _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (await _backend.PrepareUpdateAsync(_stop.Token))
                    {
                        if (!_stop.IsCancellationRequested)
                        {
                            _ready = true;
                            notify("새 버전을 준비했습니다. 앱을 정상 종료하면 자동 업데이트됩니다.", false);
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception ex) { Debug.WriteLine($"Update check/download: {ex}"); }
                await Task.Delay(_interval, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Debug.WriteLine($"Auto update: {ex}"); }
    }
    public void StopAndApplyAfterExit()
    {
        _stop.Cancel();
        try
        {
            if (_ready && !FileOperationRegistry.IsBusy())
            {
                _ready = false;
                _backend!.ApplyAfterExit();
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Update on exit: {ex}"); }
    }
    public void Dispose() => _stop.Cancel();
}

public interface IAppUpdateBackend
{
    bool IsInstalled { get; }
    Task<bool> PrepareUpdateAsync(CancellationToken token);
    void ApplyAfterExit();
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
    public void ApplyAfterExit()
    {
        if (_ready is not null) _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
    }
}
