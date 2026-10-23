using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Logic(root).GetAwaiter().GetResult();
            if (!args.Contains("--logic-only")) AppTest(root);
            Console.WriteLine("PASS maintenance: previous-good backup, no-op writes, safe export/import, bounded/private logs, live restore and tray state.");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task Logic(string root)
    {
        var store = new ConfigStore(Path.Combine(root, "config.json"));
        var config = new AppConfig(); config.NotifyNewDrive = false;
        Require(await store.SaveAsync(config), "initial save");
        var original = File.ReadAllBytes(store.FilePath);
        var date = File.GetLastWriteTimeUtc(store.FilePath);
        Require(await store.SaveAsync(config) && !File.Exists(store.BackupPath) && date == File.GetLastWriteTimeUtc(store.FilePath), "unchanged snapshot does no write or backup rotation");
        config.GlobalView.ShowPreviews = true;
        Require(await store.SaveAsync(config) && File.ReadAllBytes(store.BackupPath).SequenceEqual(original), "atomic previous-good backup");
        File.WriteAllText(store.FilePath, "{broken");
        Require(await store.SaveAsync(config) && File.ReadAllBytes(store.BackupPath).SequenceEqual(original), "externally corrupt config cannot overwrite good backup");
        var export = new ConfigStore(Path.Combine(root, "export.json"), keepBackup: false);
        Require(await export.SaveAsync(config) && ConfigStore.ReadValidated(export.FilePath).GlobalView.ShowPreviews, "export roundtrip");
        File.WriteAllText(export.FilePath, "{\"version\":\"one\"}");
        try { _ = ConfigStore.ReadValidated(export.FilePath); throw new Exception("invalid export accepted"); }
        catch (System.Text.Json.JsonException) { }
        Require(ConfigStore.ReadValidated(store.FilePath).GlobalView.ShowPreviews, "invalid import leaves live config intact");
        File.WriteAllText(export.FilePath, new string(' ', ConfigStore.MaximumBytes + 1));
        try { _ = ConfigStore.ReadValidated(export.FilePath); throw new Exception("oversized import accepted"); }
        catch (System.Text.Json.JsonException) { }
        var logPath = Path.Combine(root, "logs");
        using (var log = new DiagnosticLog(logPath, 4096))
        {
            Require(!Directory.Exists(logPath), "idle logger creates no files");
            for (var i = 0; i < 400; i++)
            {
                log.Enqueue("test.failure", new IOException(@"private C:\Users\person\secret-file.txt contents"));
                if (i % 10 == 0) await Task.Delay(2);
            }
            log.Complete(); await log.Completion;
        }
        var files = Directory.GetFiles(logPath);
        Require(files.Length == 3 && files.All(file => new FileInfo(file).Length <= 4096), "log rotation is bounded to three capped files");
        Require(files.All(file => !File.ReadAllText(file).Contains("secret-file") && !File.ReadAllText(file).Contains("C:\\Users")), "exception paths and contents are never logged");
        var blocker = Path.Combine(root, "not-a-folder"); File.WriteAllText(blocker, "fixture");
        using (var failed = new DiagnosticLog(blocker)) { failed.Enqueue("test.write.failure"); failed.Complete(); await failed.Completion; }
    }
    private static void AppTest(string root)
    {
        var path = Path.Combine(root, "app.json"); var store = new ConfigStore(path);
        var driveRoot = Directory.CreateDirectory(Path.Combine(root, "drive")).FullName + "\\";
        File.WriteAllText(Path.Combine(driveRoot, "keep.txt"), "unchanged payload");
        var config = new AppConfig { NotifyNewDrive = false };
        config.DriveOrder.Add("42"); config.Drives["42"] = new() { Registered = true };
        Require(store.Save(config), "app fixture");
        var app = new App(store, () => [new(42, driveRoot, "Fixture", DriveType.Removable)]);
        Exception? failure = null;
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                var tray = (Hardcodet.Wpf.TaskbarNotification.TaskbarIcon)Field(app, "_trayIcon");
                tray.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                Require(tray.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "지금 업데이트 확인"))
                    && tray.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "설정·즐겨찾기 백업")), "real tray exposes maintenance commands");
                var before = app.Windows.OfType<DrivePanelWindow>().Single();
                var imported = new AppConfig { NotifyNewDrive = false };
                imported.GlobalView.ShowPreviews = true; imported.DriveOrder.Add("42");
                imported.Drives["42"] = new() { Registered = true, ViewMode = GlobalViewMode.Details,
                    Favorites = [new() { RelativePath = "keep.txt", DisplayName = "Favorite", IsDirectory = false }] };
                using (var writer = FileOperationRegistry.Begin([driveRoot]))
                {
                    var restore = (Task<bool>)Call(app, "ApplyImportedSettingsAsync", imported)!;
                    await Task.Delay(30);
                    Require(!restore.IsCompleted && app.Windows.OfType<DrivePanelWindow>().Contains(before), "restore waits for active writers without closing panels");
                    writer.Dispose(); Require(await restore, "live import applied");
                }
                var after = app.Windows.OfType<DrivePanelWindow>().Single();
                Require(after != before && after.EffectiveViewMode == GlobalViewMode.Details && store.Load().Drives["42"].Favorites.Count == 1,
                    "panels recreate at favorites with imported preferences");
                Require(ConfigStore.ReadValidated(store.BackupPath).Drives["42"].Favorites.Count == 0
                    && File.ReadAllText(Path.Combine(driveRoot, "keep.txt")) == "unchanged payload", "old config retained; actual files untouched");
                var restored = ConfigStore.ReadValidated(store.BackupPath);
                Require(await (Task<bool>)Call(app, "ApplyImportedSettingsAsync", restored)!, "previous-good backup can be restored");
                // Force a real write failure and assert that no panels/config are replaced.
                var old = app.Windows.OfType<DrivePanelWindow>().Single();
                Directory.CreateDirectory(path + ".tmp");
                Require(!await (Task<bool>)Call(app, "ApplyImportedSettingsAsync", imported)!
                    && app.Windows.OfType<DrivePanelWindow>().Single() == old && !store.Load().GlobalView.ShowPreviews, "failed import preserves live state");
                Directory.Delete(path + ".tmp");
            }
            catch (Exception ex) { failure = ex; }
            finally { Call(app, "ExitApplication"); }
        }));
        app.InitializeComponent(); app.Run(); if (failure is not null) throw failure;
    }
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static void Require(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS " + text); }
}
