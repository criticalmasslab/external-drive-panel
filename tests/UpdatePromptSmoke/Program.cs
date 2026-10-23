using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            foreach (var scenario in new[] { "cancel", "restart", "failure", "save-failure", "no-drives" })
            {
                var info = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                info.ArgumentList.Add(typeof(Program).Assembly.Location); info.ArgumentList.Add(scenario);
                using var process = Process.Start(info)!;
                if (!process.WaitForExit(45000) || process.ExitCode != 0) throw new Exception("Failed update prompt scenario: " + scenario);
            }
            Console.WriteLine("PASS update prompt: real WPF footer, geometry, confirmation/cancel, file-writer wait, save-before-apply, restart, error retry and normal exit.");
            return 0;
        }
        var scenarioName = args[0];
        var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-UpdatePrompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ConfigStore(Path.Combine(root, "config.json"));
        var snapshots = scenarioName == "no-drives" ? Array.Empty<DriveSnapshot>() : Enumerable.Range(1, 2).Select(i =>
            new DriveSnapshot((uint)i, Directory.CreateDirectory(Path.Combine(root, "drive" + i)).FullName + "\\", "Test", DriveType.Removable)).ToArray();
        var config = new AppConfig { NotifyNewDrive = false };
        foreach (var drive in snapshots) { config.DriveOrder.Add(drive.Serial.ToString()); config.Drives[drive.Serial.ToString()] = new() { Registered = true }; }
        Require(store.Save(config), "fixture config saved");
        var backend = new Backend(); var dialogs = new Dialogs { Confirm = scenarioName != "cancel" };
        var updates = new AutoUpdateService(() => backend, TimeSpan.FromMilliseconds(1));
        var app = new App(store, () => snapshots, updates, dialogs);
        var result = 0;
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await updates.Completion;
                var bar = app.Windows.OfType<UpdateNotificationWindow>().Single();
                var panels = app.Windows.OfType<DrivePanelWindow>().ToArray();
                Require(bar.IsVisible && !bar.Topmost && updates.IsReady && backend.Applies == 0, "ready footer does not restart without click");
                bar.Close();
                Require(bar.IsVisible, "Alt+F4 cannot orphan the pending update button");
                var state = (PanelLayoutState)Field(app, "_panelLayout");
                foreach (var direction in Enum.GetValues<SplitDirection>())
                {
                    ((ZoneSettings)Field(app, "_zoneSettings")).SplitDirection = direction;
                    Call(app, "ResetManualLayout");
                    var cell = state.Cell;
                    state.Drag(state.Begin(new(ResizeEdges.Left | ResizeEdges.Bottom), cell.X, cell.Y + cell.Height), cell.X - 20, cell.Y + cell.Height + 25);
                    Call(app, "RepositionWindows");
                    await Task.Delay(30);
                    GetWindowRect(new WindowInteropHelper(bar).Handle, out var rect);
                    Require(rect.Bottom == Math.Round(state.Cell.Y + state.Cell.Height)
                        && rect.Bottom - rect.Top == UpdateNotificationWindow.BarHeight, "footer anchored inside aggregate bounds");
                    foreach (var panel in panels) { GetWindowRect(new WindowInteropHelper(panel).Handle, out var bounds); Require(bounds.Bottom <= rect.Top, "footer never covers file contents"); }
                    if (direction == SplitDirection.Horizontal && panels.Length > 1)
                        Require(state.HitTest(state.Panels()[0].X + state.Panels()[0].Width, state.Footer.Y + state.Footer.Height / 2) is null,
                            "footer buttons are not intercepted as column divider drags");
                }
                var button = (Button)bar.FindName("RestartButton");
                if (scenarioName is "cancel" or "no-drives")
                {
                    if (scenarioName == "cancel") button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(backend.Applies == 0 && bar.IsVisible && button.IsEnabled, "cancel keeps panel and pending update usable");
                    Call(app, "ExitApplication"); return;
                }
                if (scenarioName is "failure" or "save-failure")
                {
                    if (scenarioName == "failure") backend.ThrowOnce = true;
                    else Directory.CreateDirectory(store.FilePath + ".tmp");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var i = 0; i < 100 && dialogs.Errors == 0; i++) await Task.Delay(10);
                    Require(dialogs.Errors == 1 && bar.IsVisible && button.IsEnabled && updates.IsReady, "apply failure keeps app open and allows retry");
                    if (scenarioName == "save-failure") Directory.Delete(store.FilePath + ".tmp");
                }
                var writer = FileOperationRegistry.Begin([root]);
                backend.BeforeApply = () =>
                {
                    Require(!FileOperationRegistry.IsBusy(), "all writers drained before updater launches");
                    Require(store.Load().Zone.SplitDirection == SplitDirection.Horizontal, "latest settings saved before updater launches");
                };
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(80);
                Require(backend.Applies == 0 && bar.IsVisible && !button.IsEnabled, "confirmation waits for writer; duplicate clicks ignored");
                writer.Dispose();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; app.Shutdown(); }
        }));
        try
        {
            app.Run();
            Require(backend.Applies == (scenarioName is "restart" or "failure" or "save-failure" ? 1 : 0), "normal exit does not apply; requested restart applies once");
            if (backend.Applies > 0) Require(backend.Restart, "updater requested relaunch");
            Console.WriteLine("PASS scenario " + scenarioName);
            return result;
        }
        finally { Directory.Delete(root, true); }
    }
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void Call(object target, string name) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, null);
    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    private sealed class Backend : IAppUpdateBackend
    {
        public bool IsInstalled => true;
        public int Applies; public bool Restart, ThrowOnce; public Action? BeforeApply;
        public Task<bool> PrepareUpdateAsync(CancellationToken token) => Task.FromResult(true);
        public void ApplyAfterExit(bool restart) { if (ThrowOnce) { ThrowOnce = false; throw new IOException("test failure"); } BeforeApply?.Invoke(); Applies++; Restart = restart; }
    }
    private sealed class Dialogs : IUpdateDialogs
    {
        public bool Confirm; public int Errors;
        public bool ConfirmRestart(Window owner) => Confirm;
        public void ShowError(Window owner, string message) => Errors++;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
}
