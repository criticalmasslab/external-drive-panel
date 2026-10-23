using ExternalDrivePanel;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

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

var uninstalled = new Backend { IsInstalled = false };
using (var service = Create(uninstalled))
{
    service.Start((_, _) => throw new Exception("uninstalled notification"));
    await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    service.StopAndApplyAfterExit();
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
    using (FileOperationRegistry.Begin([Path.GetTempPath()])) service.StopAndApplyAfterExit();
    Require(retry.Applies == 0, "active writer prevents update replacement");
    service.StopAndApplyAfterExit(); service.StopAndApplyAfterExit();
    Require(retry.Applies == 1, "after writers drain, replacement schedules exactly once");
}
var canceled = new Backend { WaitForCancellation = true };
using (var service = Create(canceled))
{
    service.Start((_, _) => throw new Exception("canceled notification"));
    await canceled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    service.StopAndApplyAfterExit();
    await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    Require(canceled.Applies == 0, "incomplete download never applies on exit");
}
Console.WriteLine("PASS update lifecycle; no real network, registry, clipboard or process replacement.");
static AutoUpdateService Create(Backend backend) => new(() => backend, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
static void Require(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
sealed class Backend : IAppUpdateBackend
{
    public bool IsInstalled { get; init; } = true;
    public bool FailFirst { get; init; }
    public bool WaitForCancellation { get; init; }
    public int Checks, Applies;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<bool> PrepareUpdateAsync(CancellationToken token)
    {
        Checks++; Started.TrySetResult();
        if (FailFirst && Checks == 1) throw new IOException("offline fixture");
        if (WaitForCancellation) await Task.Delay(Timeout.Infinite, token);
        return true;
    }
    public void ApplyAfterExit() => Applies++;
}
