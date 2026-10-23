using ExternalDrivePanel;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

if (args.Length == 4 && args[0] == "--apply-installed")
{
    // This integration mode may replace an installation: fail closed everywhere
    // except GitHub's disposable Windows runner and its exact per-user app folder.
    var installed = Path.GetFullPath(args[1]);
    Require(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
        && string.Equals(installed, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExternalDrivePanel"), StringComparison.OrdinalIgnoreCase),
        "actual replacement is restricted to the disposable CI installation");
    var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExternalDeskPanel", "config.json");
    var fixture = new AppConfig { NotifyNewDrive = false };
    fixture.GlobalView.IconSize = 36; fixture.DriveOrder.Add("4000000000");
    fixture.Drives["4000000000"] = new() { Registered = true, Hidden = true, LastKnownLabel = "CI offline fixture",
        Favorites = [new() { RelativePath = "Offline\\favorite.txt", DisplayName = "Preserved", IsDirectory = false }] };
    Require(await new ConfigStore(configPath).SaveAsync(fixture), "CI settings fixture saved");
    File.Copy(configPath, Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP")!, "panel-upgrade-config.json"), overwrite: true);
    var locator = new InstalledLocator(installed, args[2]);
    var manager = new UpdateManager(Path.GetFullPath(args[3]), locator: locator);
    var update = await manager.CheckForUpdatesAsync();
    Require(update?.TargetFullRelease.Version.ToString() == typeof(App).Assembly.GetName().Version!.ToString(3), "real upgrade selects target package");
    await manager.DownloadUpdatesAsync(update!);
    manager.WaitExitThenApplyUpdates(update!.TargetFullRelease, silent: true, restart: true);
    Console.WriteLine("PASS real updater scheduled; this harness exits so updater can replace and restart the installed app.");
    return;
}

if (args.Length == 2 && args[0] is "--feed" or "--github")
{
    var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-UpdateFeed-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var packages = Directory.CreateDirectory(Path.Combine(root, "packages")).FullName;
        var locator = new TestVelopackLocator("ExternalDrivePanel", "0.9.0", packages, root, root,
            Path.Combine(root, "Update.exe"), "win");
        var manager = args[0] == "--github"
            ? new UpdateManager(new GithubSource(args[1], null, false), locator: locator)
            : new UpdateManager(Path.GetFullPath(args[1]), locator: locator);
        var update = await manager.CheckForUpdatesAsync();
        var assemblyVersion = typeof(App).Assembly.GetName().Version!;
        var expectedVersion = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        Require(update is not null && update.TargetFullRelease.Version.ToString() == expectedVersion, "real feed selects current project's newer stable release");
        await manager.DownloadUpdatesAsync(update!);
        Require(manager.UpdatePendingRestart?.Version.ToString() == expectedVersion, "real package downloaded, verified and ready; no installed app replaced");
        Console.WriteLine($"PASS real Velopack {args[0]} feed/download integrity integration (no GitHub credentials supplied).");
    }
    finally { Directory.Delete(root, true); }
    return;
}

foreach (var direction in Enum.GetValues<SplitDirection>())
{
    var layout = new PanelLayoutState(); layout.Configure(new(0, 0, 900, 900), 2, direction);
    var original = layout.Cell;
    var stale = layout.Begin(new(ResizeEdges.Bottom), 400, 900);
    layout.FooterHeight = UpdateNotificationWindow.BarHeight;
    layout.Drag(stale, 400, 1000);
    Require(layout.Cell == original, "footer appearance invalidates an active resize snapshot");
    Require(layout.Panels().All(rect => rect.Y + rect.Height <= layout.Footer.Y), "footer reserves space in both split directions");
    var edge = layout.Panels()[0];
    if (direction == SplitDirection.Horizontal)
        Require(layout.HitTest(edge.X + edge.Width, layout.Footer.Y + 20) is null, "footer does not act as a column divider");
    layout.FooterHeight = 0;
    Require(layout.Panels().Max(rect => rect.Y + rect.Height) == 900, "no update means no reserved empty space");
}

Require(new InstalledLocator(Path.GetTempPath(), "1.0.0").Process.GetCurrentProcessId() == (uint)Environment.ProcessId,
    "installed upgrade harness waits for its real process id, not the mock id");

var uninstalled = new Backend { IsInstalled = false };
using (var service = Create(uninstalled))
{
    service.Start((_, _) => throw new Exception("uninstalled notification"));
    await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    service.TryApplyAfterExit(restart: true);
    Require(uninstalled.Checks == 0 && uninstalled.Applies == 0, "portable/development do not check or replace");
}
var retry = new Backend { FailFirst = true };
using (var service = Create(retry))
{
    var notifications = 0;
    service.Start((_, _) => notifications++);
    service.Start((_, _) => throw new Exception("second loop"));
    await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    Require(retry.Checks == 2 && notifications == 1 && retry.Applies == 0, "offline failure retries; downloads without forced exit; single loop");
    using (FileOperationRegistry.Begin([Path.GetTempPath()])) service.TryApplyAfterExit(restart: true);
    Require(retry.Applies == 0, "active writer prevents update replacement");
    service.TryApplyAfterExit(restart: true); service.TryApplyAfterExit(restart: true);
    Require(retry.Applies == 1 && retry.Restart, "after writers drain, replacement schedules exactly once with restart");
}
var canceled = new Backend { WaitForCancellation = true };
using (var service = Create(canceled))
{
    service.Start((_, _) => throw new Exception("canceled notification"));
    await canceled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    service.Dispose();
    await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    Require(canceled.Applies == 0, "incomplete download never applies on exit");
}
Console.WriteLine("PASS update lifecycle; no real network, registry, clipboard or process replacement.");
var manual = new Backend { WaitForRelease = true };
using (var service = new AutoUpdateService(() => manual, TimeSpan.FromHours(1)))
{
    service.Start((_, _) => { });
    var first = service.CheckNowAsync();
    await manual.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var second = service.CheckNowAsync();
    Require(ReferenceEquals(first, second) && manual.Checks == 1 && service.State == UpdateCheckState.Checking, "concurrent manual checks share one download");
    manual.Release.TrySetResult();
    Require(await first == UpdateCheckState.Ready && await service.CheckNowAsync() == UpdateCheckState.Ready && manual.Checks == 1, "manual ready state reuses the verified pending package");
}
var latest = new Backend { NoUpdate = true };
using (var service = Create(latest)) Require(await service.CheckNowAsync() == UpdateCheckState.UpToDate, "manual check reports current version");
using (var service = Create(new Backend { FailFirst = true }))
{
    Require(await service.CheckNowAsync() == UpdateCheckState.Failed, "manual failure is visible");
    Require(await service.CheckNowAsync() == UpdateCheckState.Ready, "manual failure can retry immediately");
}
static AutoUpdateService Create(Backend backend) => new(() => backend, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
static void Require(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
sealed class Backend : IAppUpdateBackend
{
    public bool IsInstalled { get; init; } = true;
    public bool FailFirst { get; init; }
    public bool WaitForCancellation { get; init; }
    public bool WaitForRelease { get; init; }
    public bool NoUpdate { get; init; }
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Checks, Applies;
    public bool Restart;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<bool> PrepareUpdateAsync(CancellationToken token)
    {
        Checks++; Started.TrySetResult();
        if (FailFirst && Checks == 1) throw new IOException("offline fixture");
        if (WaitForCancellation) await Task.Delay(Timeout.Infinite, token);
        if (WaitForRelease) await Release.Task.WaitAsync(token);
        return !NoUpdate;
    }
    public void ApplyAfterExit(bool restart) { Applies++; Restart = restart; }
}

// TestVelopackLocator's default process id is zero. Use the actual harness pid
// for the disposable-runner upgrade so replacement genuinely waits for exit.
sealed class InstalledLocator(string root, string version)
    : TestVelopackLocator("ExternalDrivePanel", version, Path.Combine(root, "packages"), Path.Combine(root, "current"),
        root, Path.Combine(root, "Update.exe"), "win"), IProcessImpl
{
    uint IProcessImpl.GetCurrentProcessId() => (uint)Environment.ProcessId;
    string IProcessImpl.GetCurrentProcessPath() => Path.Combine(AppContentDir!, "ExternalDrivePanel.exe");
}
