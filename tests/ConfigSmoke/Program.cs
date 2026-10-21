using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0) return RunSession(args[0], args[1]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 0;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                RunStoreAndPanelTests(directory);
                foreach (var phase in new[] { "write", "read", "corrupt" })
                {
                    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                    start.ArgumentList.Add(typeof(Program).Assembly.Location);
                    start.ArgumentList.Add(phase);
                    start.ArgumentList.Add(Path.Combine(directory, "session", "config.json"));
                    using var child = Process.Start(start)!;
                    if (!child.WaitForExit(30000)) { child.Kill(); throw new Exception("Session timed out: " + phase); }
                    Require(child.ExitCode == 0, "App session: " + phase);
                }
                Console.WriteLine("PASS: JSON roundtrip, atomic replace/failure, corruption backup, root rebasing, missing favorites, App debounce/exit save/restart/panel recreation.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { app.Shutdown(); Directory.Delete(directory, recursive: true); }
        }));
        app.Run();
        return exitCode;
    }

    private static void RunStoreAndPanelTests(string directory)
    {
        var path = Path.Combine(directory, "config.json");
        var store = new ConfigStore(path);
        Require(store.Load().Zone.GridCols == 4 && !File.Exists(path), "missing config uses defaults");
        var config = new AppConfig
        {
            Zone = new ZoneSettings { MonitorIndex = 1, GridCols = 3, GridRows = 2, CellCol = 2, CellRow = 1,
                SplitDirection = SplitDirection.Horizontal, MarginUniform = false, MarginAll = 3,
                MarginTop = 5, MarginBottom = 7, MarginLeft = 9, MarginRight = 11 },
            GlobalView = new GlobalViewConfig { ViewMode = GlobalViewMode.Details, IconSize = 36, ShowHidden = true },
            Drives = { ["123456789"] = new DriveConfig { LastKnownLabel = "테스트 외장하드", SortKey = "Size", SortReverse = true,
                Favorites = [new FavoriteConfig { RelativePath = @"Projects\ClientA", DisplayName = "ClientA", IsDirectory = true },
                    new FavoriteConfig { RelativePath = @"Notes\memo.txt", DisplayName = "메모", IsDirectory = false }] } }
        };
        Require(store.Save(config), "first save");
        var firstJson = File.ReadAllText(path);
        Require(firstJson.Contains("\n  \"zone\"") && firstJson.Contains("\"version\": 1"), "indented versioned JSON");
        Require(store.Save(store.Load()) && File.ReadAllText(path) == firstJson, "exact serialized roundtrip and atomic replacement");
        Console.WriteLine("CONFIG EXAMPLE:\n" + firstJson);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Require(!store.Save(config), "locked config save fails quietly");
        Require(File.ReadAllText(path) == firstJson, "failed replacement preserves original");
        Require(store.Save(config) && !File.Exists(path + ".tmp"), "retry succeeds and temporary file is consumed");
        File.WriteAllText(path, "");
        Require(store.Load().Zone.GridCols == 4 && File.Exists(path + ".bak") && File.ReadAllText(path + ".bak") == "", "empty JSON backup + defaults");
        File.WriteAllText(path, "{\"zone\":null}");
        Require(store.Load().Zone.GridCols == 4 && File.ReadAllText(path + ".bak").Contains("null"), "invalid structure backup");
        File.WriteAllText(path, "{\"zone\":{\"grid_cols\":3}}");
        Require(store.Load().Zone.GridCols == 4 && File.ReadAllText(path + ".bak").Contains("grid_cols"), "legacy schema preserved rather than silently misread");
        Require(!ConfigStore.IsSafeRelativePath(@"..\outside") && !ConfigStore.IsSafeRelativePath(@"D:\absolute")
            && !ConfigStore.IsSafeRelativePath(@"D:relative") && ConfigStore.IsSafeRelativePath(@"Projects\ClientA"), "safe relative paths");

        var state = config.Drives["123456789"];
        var view = new GlobalViewSettings();
        view.Restore(config.GlobalView);
        var original = new DrivePanelWindow(@"D:\", "test", view, new EjectService(), state);
        var rebased = new DrivePanelWindow(@"E:\", "test", view, new EjectService(), original.CaptureSettings("test"));
        Require(Favorites(original).Items[0].Path == @"D:\Projects\ClientA"
            && Favorites(rebased).Items[0].Path == @"E:\Projects\ClientA", "same serial restores under a different drive letter");
        Require(Favorites(rebased).Items.Count == 2 && Favorites(rebased).Items[0].IsDirectory
            && Favorites(rebased).Items[1].Name == "메모", "missing favorites retain type/name");
        Require(rebased.CaptureSettings("test").SortKey == "Size" && rebased.CaptureSettings("test").SortReverse, "sort restored");
        Require(Field<int>(rebased, "_historyIndex") == 0
            && ((ListBox)rebased.FindName("FavoriteItemsList")).Visibility == Visibility.Visible, "recreated panel starts at home");
        var changes = 0;
        rebased.SettingsChanged += (_, _) => changes++;
        Favorites(rebased).Add(@"E:\new-favorite", true);
        Favorites(rebased).Remove(Favorites(rebased).Items.Last());
        Require(changes == 2, "favorite add/remove notifications");
        original.Close();
        rebased.Close();
    }

    private static int RunSession(string phase, string path)
    {
        if (phase == "corrupt") File.WriteAllText(path, "");
        var store = new ConfigStore(path);
        const uint testSerial = 1234567890;
        var fixtureRoot = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(path)!, "drive")).FullName + Path.DirectorySeparatorChar;
        var snapshot = new Dictionary<uint, (string Root, string Label)> { [testSerial] = (fixtureRoot, "Test volume") };
        var app = new App(store, () => new(snapshot));
        var result = 0;
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                var view = Field<GlobalViewSettings>(app, "_viewSettings");
                var zone = Field<ZoneSettings>(app, "_zoneSettings");
                var panels = Field<Dictionary<uint, DrivePanelWindow>>(app, "_panels");
                if (phase == "write")
                {
                    var top = Field<TopBarWindow>(app, "_topBar");
                    _ = app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        var dialog = app.Windows.OfType<ZoneSettingsWindow>().Single();
                        ((TextBox)dialog.FindName("MarginAllText")).Text = "17";
                        ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }));
                    Call(app, "TopBar_OnSettingsRequested", top, EventArgs.Empty);
                    view.IncreaseIconSize();
                    view.SetViewMode(GlobalViewMode.Details);
                    view.ToggleHiddenItems();
                    Require(!File.Exists(path), "changes debounce before disk write");
                    await Task.Delay(700);
                    Require(store.Load().Zone.MarginAll == 17 && store.Load().GlobalView.IconSize == 28, "debounced zone/view save");
                    if (panels.Count > 0)
                    {
                        var pair = panels.First();
                        var missing = Path.Combine(pair.Value.RootPath, "__ExternalDeskPanel_Missing_Test__");
                        Favorites(pair.Value).Add(missing, true);
                        var grid = (DataGrid)pair.Value.FindName("DetailsGrid");
                        var sort = new DataGridSortingEventArgs(grid.Columns.Single(c => c.SortMemberPath == "Size"));
                        Call(pair.Value, "DetailsGrid_OnSorting", grid, sort);
                        snapshot.Clear();
                        Call(app, "RefreshPanels");
                        Require(panels.Count == 0, "disconnect closes panel");
                        snapshot[testSerial] = (fixtureRoot, "Test volume");
                        Call(app, "RefreshPanels");
                        var recreated = panels[pair.Key];
                        Require(Favorites(recreated).Items.Any(f => f.Path == missing) && recreated.CaptureSettings("").SortKey == "Size",
                            "panel close/recreate retains favorites/sort before debounce");
                    }
                    // No delay: validates exit flush of the last favorite/sort changes.
                }
                else if (phase == "read")
                {
                    Require(zone.MarginAll == 17 && view.IconSize == 28 && view.ViewMode == GlobalViewMode.Details && view.ShowHiddenItems,
                        "fresh App process restores zone/view before windows");
                    foreach (var panel in panels.Values)
                    {
                        var saved = store.Load().Drives[testSerial.ToString()];
                        Require(panel.CaptureSettings(saved.LastKnownLabel).Favorites.Count == saved.Favorites.Count, "fresh process restores favorites");
                        Require(saved.Favorites.Count == 1 && saved.SortKey == "Size", "exit flush saves favorite and sort state");
                    }
                }
                else
                {
                    Require(zone.MarginAll == 0 && view.IconSize == 24 && File.Exists(path + ".bak") && File.ReadAllText(path + ".bak") == "",
                        "real App starts with defaults after corrupt config and preserves bak");
                }
                Console.WriteLine($"PASS App {phase}: {panels.Count} simulated connected panels");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { Call(app, "ExitApplication"); }
        }));
        app.Run();
        return result;
    }

    private static FavoriteCollection Favorites(DrivePanelWindow panel) => Field<FavoriteCollection>(panel, "_favorites");
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
