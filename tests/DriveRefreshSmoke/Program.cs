using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    private static readonly UsbReconnectTarget Target = new(@"USBSTOR\Disk&Ven_fixture\EJECTED", @"USB\VID_1234&PID_5678\SERIAL", @"USB\ROOT_HUB30\HUB", 2);
    [STAThread] private static int Main(string[] args)
    {
        Logic().GetAwaiter().GetResult();
        if (args.Contains("--logic-only")) return 0;
        var candidates = new WindowsUsbReconnectPlatform().FindCandidates();
        Console.WriteLine($"READ ONLY: native USB discovery completed; {candidates.Count} eligible held-for-eject ports. No device changed or UAC requested.");
        var temporary = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try { return Windows(temporary); }
        finally { Directory.Delete(temporary, true); }
    }
    private static async Task Logic()
    {
        var disk = new UsbReconnectNode(Target.EjectedDeviceId, "disk", 0x400, 47, 0, 0);
        var usb = new UsbReconnectNode(Target.UsbDeviceId, "USBSTOR", 0, 0, 2, 4);
        var hub = new UsbReconnectNode(Target.HubDeviceId, "USBHUB3", 8, 0, 0, 0);
        Require(UsbReconnectPolicy.Select([disk, usb, hub], _ => false, (_, _, _) => true) == Target, "USB storage port maps correctly");
        Require(UsbReconnectPolicy.Select([disk, usb with { Service = "UASPStor" }, hub], _ => false, (_, _, _) => true) == Target, "UASP storage supported");
        void Reject(UsbReconnectNode[] nodes, bool active = false, bool matches = true)
        {
            try { UsbReconnectPolicy.Select(nodes, _ => active, (_, _, _) => matches); throw new Exception("Unsafe target accepted"); }
            catch (IOException) { }
        }
        foreach (var problem in new uint[] { 0, 22, 45 }) Reject([disk with { Problem = problem }, usb, hub]);
        Reject([disk with { Status = 0 }, usb, hub]);
        Reject([disk, usb, hub], active: true);
        Reject([disk, usb, hub], matches: false);
        Reject([disk, usb with { Port = 0 }, hub]); Reject([disk, usb with { Port = 256 }, hub]);
        Reject([disk, usb with { Capabilities = 12 }, hub]);
        Reject([disk, usb with { Service = "usbccgp" }, hub]);
        Reject([disk, usb with { Id = @"USB\VID_1234&PID_5678&MI_00\SERIAL" }, hub]);
        Reject([disk, usb, hub with { Service = "pci" }]);
        Reject([disk, hub]);
        Console.WriteLine("PASS policy: only Code 47 USBSTOR/UASP, no active disks, no changed ports, no composite/hubs/docks/controllers.");

        var fake = new Platform();
        Require(!DeviceReconnectService.TryRunHelper(["--unrelated"], out _, fake), "helper switch is explicit");
        foreach (var invalid in new[] {
            new[] { DeviceReconnectService.HelperSwitch },
            new[] { DeviceReconnectService.HelperSwitch, "invalid" },
            new[] { DeviceReconnectService.HelperSwitch, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("null")) },
            new[] { DeviceReconnectService.HelperSwitch, DeviceReconnectService.Encode(Target with { Port = 0 }) },
            new[] { DeviceReconnectService.HelperSwitch, DeviceReconnectService.Encode(Target with { EjectedDeviceId = @"C:\Windows\file" }) },
            new[] { DeviceReconnectService.HelperSwitch, DeviceReconnectService.Encode(Target), "malformed tail" },
            new[] { DeviceReconnectService.HelperSwitch, DeviceReconnectService.Encode(Target), DeviceReconnectService.Encode(Target) }
        })
        {
            Require(DeviceReconnectService.TryRunHelper(invalid, out var code, fake) && code == 87 && fake.Cycles == 0,
                "malformed/broad/duplicate helper input has no side effects");
        }
        Require(DeviceReconnectService.TryRunHelper([DeviceReconnectService.HelperSwitch, DeviceReconnectService.Encode(Target)], out var result, fake)
            && result == 0 && fake.Cycles == 1, "helper carries exact expected device/hub/port identity");
        var other = Target with { Port = 3, UsbDeviceId = @"USB\VID_1234&PID_5678\OTHER" };
        fake.CycleResult = 1167;
        DeviceReconnectService.TryRunHelper([DeviceReconnectService.HelperSwitch, DeviceReconnectService.Encode(Target), DeviceReconnectService.Encode(other)], out result, fake);
        Require(result == 1167 && fake.Cycles == 2, "stale identity stops remaining helper operations");

        fake.Targets = []; var service = new DeviceReconnectService(fake);
        Require((await service.ReconnectAsync(0)).Candidates == 0 && fake.Requests == 0, "ordinary refresh requests no elevation");
        fake.Targets = [Target]; fake.Reply = null;
        Require((await service.ReconnectAsync(0)).Cancelled, "UAC cancellation is a normal result");
        fake.Reply = 5;
        Require((await service.ReconnectAsync(0)).Error == 5, "access denial is returned without success");
        fake.Throw = true;
        Require((await service.ReconnectAsync(0)).Error != 0, "platform exception is contained");
        Console.WriteLine("PASS helper/service: strict batch validation, identity roundtrip, early stop, no unnecessary UAC, cancellation and errors. All cycles were fake.");
    }

    private static int Windows(string temporary)
    {
        var root1 = Directory.CreateDirectory(Path.Combine(temporary, "drive1")).FullName + "\\";
        var root2 = Directory.CreateDirectory(Path.Combine(temporary, "drive2")).FullName + "\\";
        File.WriteAllText(Path.Combine(root2, "favorite.txt"), "fixture");
        var config = new AppConfig { NotifyNewDrive = false, DriveOrder = ["1", "2"] };
        config.Drives["1"] = new() { Registered = true };
        config.Drives["2"] = new() { Registered = true, Favorites = [new() { RelativePath = "favorite.txt", IsDirectory = false }] };
        var store = new ConfigStore(Path.Combine(temporary, "config.json")); Require(store.Save(config), "fixture saved");
        var drives = new List<DriveSnapshot> { new(1, root1, "Connected fixture", DriveType.Removable) };
        var platform = new Platform { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var app = new App(store, () => drives.ToArray(), reconnectService: new(platform));
        var code = 0;
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                var top = Field<TopBarWindow>(app, "_topBar");
                var uiThread = Environment.CurrentManagedThreadId;
                var panels = Field<Dictionary<uint, DrivePanelWindow>>(app, "_panels");
                Require(panels.Count == 1, "only connected registered drive has a panel");
                var settings = (Button)top.FindName("SettingsButton"); var refresh = (Button)top.FindName("RefreshButton"); var preview = (Button)top.FindName("PreviewButton");
                var buttons = (StackPanel)refresh.Parent;
                Require(buttons.Children.IndexOf(settings) + 1 == buttons.Children.IndexOf(refresh)
                    && buttons.Children.IndexOf(refresh) + 1 == buttons.Children.IndexOf(preview), "refresh is between settings and preview");
                var text = (TextBlock)top.FindName("AppNameText");
                Require(text.TextTrimming == TextTrimming.CharacterEllipsis && text.ToolTip is string, "title trims and exposes full name in tooltip");
                var cell = Field<PanelLayoutState>(app, "_panelLayout").Cell;
                foreach (var width in new[] { 360, 480 })
                {
                    top.SetPlacement(new(cell.X, cell.Y, width, LayoutCalculator.TopBarHeight)); await Task.Delay(50);
                    Require(text.ActualWidth < width && settings.TransformToAncestor(top).Transform(new Point()).X >= text.ActualWidth,
                        "buttons retain space while title shrinks");
                    var dpi = VisualTreeHelper.GetDpi(top);
                    var bitmap = new RenderTargetBitmap(width, 42, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    bitmap.Render(top); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, $"refresh-header-{width}.png")); encoder.Save(stream);
                }
                Call(app, "RepositionWindows");
                var writer = FileOperationRegistry.Begin([root1]);
                var pending = (Task)Call(app, "RefreshDrivesAsync")!;
                await Task.Delay(30);
                Require(!top.IsEnabled && panels.Values.All(panel => !panel.IsEnabled) && platform.Finds == 0,
                    "refresh waits for active writes and disables conflicting UI");
                await (Task)Call(app, "RefreshDrivesAsync")!;
                Require(platform.Finds == 0, "duplicate refresh cannot overlap");
                writer.Dispose();
                await WaitFor(() => platform.Requests == 1);
                Require(platform.RequestThread != uiThread, "Windows consent launch/wait stays off the WPF dispatcher");
                Require(Field<bool>(app, "_driveRefreshInProgress"), "busy state remains until request completes");
                drives.Add(new(2, root2, "Reconnected fixture", DriveType.Removable)); platform.Gate!.SetResult(0);
                await pending;
                Require(panels.Count == 2 && panels[2].FavoriteCount == 1, "reconnected drive panel restores saved favorites");
                Require(top.IsEnabled && panels.Values.All(panel => panel.IsEnabled) && !Field<bool>(app, "_exitRequested")
                    && Field<DispatcherTimer>(app, "_pollTimer").IsEnabled, "refresh restores UI, polling and transition guards");
                platform.Gate = null; platform.Targets = [];
                refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => !Field<bool>(app, "_driveRefreshInProgress"));
                Require(platform.Finds == 2 && platform.Requests == 1, "real header click refreshes without unnecessary UAC");
                platform.Targets = [Target]; platform.Reply = null;
                await (Task)Call(app, "RefreshDrivesAsync")!;
                Require(top.IsEnabled && panels.Count == 2, "cancel leaves connected panels intact");
                platform.Throw = true;
                await (Task)Call(app, "RefreshDrivesAsync")!;
                Require(top.IsEnabled && !Field<bool>(app, "_driveRefreshInProgress") && panels.Count == 2, "failure recovers without closing healthy panels");
                Console.WriteLine("PASS WPF: header wiring/order, writer wait, duplicate guard, snapshot refresh/reappearance, favorites, cancellation, failure and polling recovery. No real reconnect/UAC.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            finally { app.Shutdown(code); }
        }));
        app.Run(); return code;
    }
    private sealed class Platform : IDeviceReconnectPlatform
    {
        public IReadOnlyList<UsbReconnectTarget> Targets = [Target];
        public int Finds, Requests, Cycles, CycleResult;
        public int RequestThread;
        public int? Reply = 0;
        public bool Throw;
        public TaskCompletionSource<int?>? Gate;
        public IReadOnlyList<UsbReconnectTarget> FindCandidates() { Finds++; if (Throw) throw new Win32Exception(5); return Targets; }
        public Task<int?> RequestAsync(IReadOnlyList<UsbReconnectTarget> targets, nint owner) { RequestThread = Environment.CurrentManagedThreadId; Requests++; return Gate?.Task ?? Task.FromResult(Reply); }
        public int Cycle(UsbReconnectTarget expected) { Cycles++; return CycleResult; }
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    private static object? Call(object value, string name) => value.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(value, null);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task WaitFor(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(20);
        Require(condition(), "asynchronous refresh completed in fixture");
    }
}
