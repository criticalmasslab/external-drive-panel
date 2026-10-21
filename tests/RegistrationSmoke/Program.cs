using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0) return AppTest(args[0]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 0;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Registration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                CoordinatorTest();
                CompatibilityTest(directory);
                MenuAndDialogTest();
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
                start.ArgumentList.Add(Path.Combine(directory, "app.json"));
                using var child = Process.Start(start)!;
                if (!child.WaitForExit(30000)) { child.Kill(); throw new Exception("App test timed out"); }
                Require(child.ExitCode == 0, "App integration");
                Console.WriteLine("PASS: registration policy, session/ignore lifecycle, stale queue, ordering, filtering, 5-A compatibility, menu callbacks, all notification buttons/placement, and App modal queue/panel state.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); Directory.Delete(directory, true); }
        }));
        app.Run();
        return result;
    }

    private static DriveSnapshot Drive(uint serial, string root = @"D:\", DriveType type = DriveType.Removable)
        => new(serial, root, "Test_" + serial, type);

    private static void CoordinatorTest()
    {
        var config = new AppConfig();
        var co = new DriveCoordinator(config);
        var snapshot = new[] { Drive(1), Drive(2, @"E:\"), Drive(3, @"C:\"), Drive(4, @"F:\", DriveType.CDRom),
            Drive(5, @"Z:\", DriveType.Network), Drive(6, @"R:\", DriveType.Ram) };
        var plan = co.Evaluate(snapshot);
        Require(plan.VisibleDrives.Count == 0 && config.Drives.Count == 0, "unregistered: no panels and no config debris");
        Require(plan.Notices.Select(n => n.Serial).SequenceEqual(new uint[] { 1, 2 }) && co.ManualCandidates.Count == 2, "excluded drive types/C and stable notification order");
        Require(co.Evaluate(snapshot).Notices.Count == 0, "reserved before modal; no duplicate polling prompts");
        co.Evaluate([]);
        var notices = co.Evaluate(snapshot).Notices;
        Require(notices.Count == 2 && !co.CanPrompt(plan.Notices[0]), "disconnect resets skip and rejects stale queued notice");
        Require(co.Ignore(notices[0]) && co.ManualCandidates.Count == 2 && config.Drives.Count == 0, "permanent ignore does not create a drive; manual candidate retained");
        co.Evaluate([]);
        Require(co.Evaluate(snapshot).Notices.Select(n => n.Serial).SequenceEqual(new uint[] { 2 }), "ignore persists across reconnect");
        Require(co.Register(2) && co.Register(1), "manual registration including ignored drive");
        Require(!config.IgnoredDrives.Contains("1") && config.DriveOrder.SequenceEqual(new[] { "2", "1" }), "register removes ignore; order is registration order");
        Require(co.Evaluate(snapshot).VisibleDrives.Select(d => d.Serial).SequenceEqual(new uint[] { 2, 1 }), "panel order independent of discovery order");
        co.ToggleHidden("2");
        Require(co.Evaluate(snapshot).VisibleDrives.Select(d => d.Serial).SequenceEqual(new uint[] { 1 }), "hidden excluded from layout");
        co.Evaluate([]);
        Require(co.DisplayName("2") == "(연결 안 됨) Test_2" && co.ToggleHidden("2"), "offline label and hidden toggle");
        co.Evaluate(snapshot);
        Require(co.Unregister("1") && !config.Drives.ContainsKey("1") && !config.DriveOrder.Contains("1"), "unregister removes entry/order");
        Require(co.Evaluate(snapshot).Notices.Count == 0 && co.Evaluate(snapshot).VisibleDrives.Count == 1, "no immediate re-prompt after unregister; no panel");
        co.Evaluate([]);
        Require(co.Evaluate(snapshot).Notices.Single().Serial == 1, "unregistered drive asks again on next connection");
        config.NotifyNewDrive = false;
        co.Evaluate([]);
        Require(co.Evaluate(snapshot).Notices.Count == 0 && co.ManualCandidates.Count == 1, "notify off still permits manual registration");
        Require(!co.Register(99) && !config.Drives.ContainsKey("99"), "stale manual candidate rejected");
        co.Evaluate([]);
        Require(co.Unregister("2"), "offline unregister");
    }

    private static void CompatibilityTest(string directory)
    {
        var store = new ConfigStore(Path.Combine(directory, "compat.json"));
        var config = new AppConfig();
        config.Drives["77"] = new DriveConfig { LastKnownLabel = "Legacy", Favorites =
            [new FavoriteConfig { RelativePath = @"Projects\ClientA", DisplayName = "ClientA", IsDirectory = true }] };
        Require(store.Save(config), "save fixture");
        var json = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        foreach (var key in new[] { "notifyNewDrive", "driveOrder", "ignoredDrives" }) json.AsObject().Remove(key);
        json["drives"]!["77"]!.AsObject().Remove("registered");
        json["drives"]!["77"]!.AsObject().Remove("hidden");
        File.WriteAllText(store.FilePath, json.ToJsonString());
        var loaded = store.Load();
        Require(!File.Exists(store.FilePath + ".bak") && !loaded.Drives["77"].Registered
            && !loaded.Drives["77"].Hidden && loaded.NotifyNewDrive && loaded.Drives["77"].Favorites.Count == 1, "5-A missing fields default; no lost favorites");
        var co = new DriveCoordinator(loaded);
        Require(co.Evaluate([Drive(77)]).VisibleDrives.Count == 0, "legacy unregistered panel stays closed");
        co.Register(77);
        Require(loaded.Drives["77"].Favorites.Count == 1 && loaded.DriveOrder.Single() == "77", "registration revives legacy favorites");
        co.ToggleHidden("77");
        var notice = co.Evaluate([Drive(77), Drive(88, @"E:\")]).Notices.Single();
        co.Ignore(notice);
        Require(store.Save(loaded), "registration config saved");
        var restarted = store.Load();
        Require(restarted.Drives["77"].Registered && restarted.Drives["77"].Hidden
            && new DriveCoordinator(restarted).Evaluate([Drive(77), Drive(88, @"E:\")]).Notices.Count == 0, "hidden/ignored survive restart");
        Console.WriteLine("CONFIG EXAMPLE:\n" + File.ReadAllText(store.FilePath));
    }

    private static string Header(MenuItem item) => item.Header is TextBlock text ? text.Text : (string)item.Header;

    private static void MenuAndDialogTest()
    {
        var config = new AppConfig();
        var co = new DriveCoordinator(config);
        co.Evaluate([Drive(1), Drive(2, @"E:\")]);
        co.Register(1);
        var notice = co.Evaluate([Drive(1), Drive(2, @"E:\")]).Notices.FirstOrDefault();
        // Initial evaluation already reserved 2. Construct the current notice from a fresh connection.
        co.Evaluate([Drive(1)]);
        notice = co.Evaluate([Drive(1), Drive(2, @"E:\")]).Notices.Single();
        co.Ignore(notice);
        var menu = new ContextMenu();
        void Populate() => TrayMenuBuilder.Populate(menu, co, config, () => { }, serial => co.Register(serial),
            key => co.ToggleHidden(key), key => co.Unregister(key), () => { });
        Populate();
        var manual = (MenuItem)menu.Items[1];
        Require(Header((MenuItem)manual.Items[0]) == @"E: Test_2", "ignored candidate appears in manual menu");
        ((MenuItem)manual.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(co.IsRegistered(2), "manual registration callback");
        co.Evaluate([]);
        Populate();
        var offline = (MenuItem)menu.Items[3];
        Require(Header(offline) == "(연결 안 됨) Test_1", "offline tray entry");
        ((MenuItem)offline.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(config.Drives["1"].Hidden, "offline hide callback");
        Populate();
        Require(Header((MenuItem)((MenuItem)menu.Items[3]).Items[0]) == "패널 다시 보이기", "menu rebuilt from current hidden state");
        ((MenuItem)((MenuItem)menu.Items[3]).Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(!co.IsRegistered(1), "offline unregister callback");
        Require(!((MenuItem)((MenuItem)menu.Items[1]).Items[0]).IsEnabled, "empty candidate placeholder disabled");

        var owner = new TopBarWindow(new GlobalViewSettings());
        owner.Show();
        var monitors = new MonitorService().GetMonitors();
        foreach (var monitor in monitors)
        {
            owner.SetPlacement(new LayoutCalculator().CalculateTopBar(new LayoutCalculator().CalculateCell(monitor, new ZoneSettings())));
            foreach (var expected in new[] { NewDriveChoice.Register, NewDriveChoice.Skip, NewDriveChoice.Ignore })
            {
                Exception? failure = null;
                var dialog = new NewDriveWindow(owner, Drive(1));
                dialog.ContentRendered += (_, _) =>
                {
                    try
                    {
                        GetWindowRect(new WindowInteropHelper(dialog).Handle, out var bounds);
                        Require(bounds.Left >= monitor.Left && bounds.Right <= monitor.Right && bounds.Top >= monitor.Top && bounds.Bottom <= monitor.Bottom,
                            "new-drive dialog within owner monitor");
                        Require(Math.Abs(bounds.Left + bounds.Right - monitor.Left - monitor.Right) <= 2
                            && Math.Abs(bounds.Top + bounds.Bottom - monitor.Top - monitor.Bottom) <= 2, "new-drive dialog centered");
                        Require(owner.Topmost && dialog.Topmost, "notification foreground scope");
                        var button = expected == NewDriveChoice.Register ? dialog.RegisterButton : expected == NewDriveChoice.Ignore ? dialog.IgnoreButton : dialog.SkipButton;
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { failure = ex; dialog.Close(); }
                };
                DialogActivation.Show(owner, () => dialog.ShowDialog());
                if (failure is not null) throw failure;
                Require(dialog.Choice == expected && !owner.Topmost, "notification choice and foreground restoration");
            }
        }
        owner.Close();
    }

    private static int AppTest(string path)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(path)!, "drive-a")).FullName + Path.DirectorySeparatorChar;
        var root2 = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(path)!, "drive-b")).FullName + Path.DirectorySeparatorChar;
        var snapshot = new List<DriveSnapshot> { new(1, root, "First", DriveType.Removable), new(2, root2, "Second", DriveType.Removable) };
        var app = new App(new ConfigStore(path), () => snapshot.ToList());
        var result = 0;
        var shown = 0;
        EventManager.RegisterClassHandler(typeof(NewDriveWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            var dialog = (NewDriveWindow)sender;
            dialog.ContentRendered += (_, _) =>
            {
                try
                {
                    Require(app.Windows.OfType<NewDriveWindow>().Count(w => w.IsVisible) == 1, "notifications serialize");
                    // Simulate ticks *inside* a nested modal dispatcher loop.
                    Call(app, "RefreshPanels"); Call(app, "RefreshPanels");
                    shown++;
                    if (shown == 1) dialog.RegisterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.SkipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; dialog.Close(); }
            };
        }));
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                Require(shown == 2, "two candidates shown once each");
                var panels = Field<Dictionary<uint, DrivePanelWindow>>(app, "_panels");
                var co = Field<DriveCoordinator>(app, "_coordinator");
                var config = Field<AppConfig>(app, "_config");
                Require(panels.Keys.SequenceEqual(new uint[] { 1 }) && !config.Drives.ContainsKey("2"), "only registered panel; skipped has no config entry");
                var favorites = Field<FavoriteCollection>(panels[1], "_favorites");
                favorites.Add(Path.Combine(root, "missing-test"), true);
                co.ToggleHidden("1"); Call(app, "RegistrationChanged");
                Require(panels.Count == 0 && config.Drives["1"].Hidden && config.Drives["1"].Registered
                    && config.Drives["1"].Favorites.Count == 1, "hide closes without losing registration/favorites");
                co.ToggleHidden("1"); Call(app, "RegistrationChanged");
                Require(panels.Count == 1 && Field<FavoriteCollection>(panels[1], "_favorites").Items.Count == 1, "unhide recreates saved panel");
                co.Unregister("1"); Call(app, "RegistrationChanged");
                Require(panels.Count == 0 && !config.Drives.ContainsKey("1"), "unregister close cannot resurrect config");
                await Task.Delay(450);
                Require(shown == 2, "unregister does not re-prompt");
                Call(app, "RegisterDrive", (uint)2);
                Call(app, "RegisterDrive", (uint)1);
                Require(config.DriveOrder.SequenceEqual(new[] { "2", "1" }) && panels.Count == 2, "manual registration updates panels/order");
                var menu = Field<Hardcodet.Wpf.TaskbarNotification.TaskbarIcon>(app, "_trayIcon").ContextMenu;
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                Require(Header((MenuItem)menu.Items[3]).EndsWith("Second"), "real tray rebuild uses registration order");
                snapshot.Clear(); Call(app, "RefreshPanels");
                Require(panels.Count == 0 && config.Drives.Count == 2, "disconnect preserves registration");
                co.ToggleHidden("2"); Call(app, "RegistrationChanged");
                Call(app, "SaveConfig");
                Require(new ConfigStore(path).Load().Drives["2"].Hidden, "offline hidden state persisted");
                Console.WriteLine("PASS App: modal queue, nested polling, hidden lifecycle, unregister without resurrection, real tray order, offline persistence.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { Call(app, "ExitApplication"); }
        }));
        app.Run();
        return result;
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}
