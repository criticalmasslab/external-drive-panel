using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        Logic().GetAwaiter().GetResult();
        if (args.Contains("--logic-only")) return 0;
        InspectOnly();
        if (args.Contains("--inspect-only")) return 0;
        var directory = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Eject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { Windows(directory); return 0; }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task Logic()
    {
        var fake = new Backend(); var service = new EjectService(fake);
        foreach (var invalid in new[] { "", "E:", "E:\\folder", "\\\\server\\share", "1:\\" })
            Require(!service.TryEject(invalid).Success, "invalid path never requests removal");
        Require(fake.Resolves == 0 && fake.Requests == 0, "no discovery or eject for malformed paths");
        Require(!service.TryEject("E:\\").Success && fake.Requests == 1, "Windows failure is not reported as success");
        var changed = new Backend { ChangeIdentity = true };
        Require(!new EjectService(changed).TryEject("E:\\").Success && changed.Requests == 0, "changed/reused drive identity is never ejected");
        Require(WindowsEjectBackend.DescribeResult(0, 0).Success, "PnP success only on CR_SUCCESS");
        Require(WindowsEjectBackend.DescribeResult(0x33, 0).FailureReason!.Contains("권한"), "permission failure is not labeled as an open file");
        Require(WindowsEjectBackend.DescribeResult(0x17, 5).FailureReason!.Contains("핸들"), "actual open-handle veto has specific guidance");
        Require(WindowsEjectBackend.DescribeResult(0x17, 3, "fixture.exe").FailureReason!.Contains("fixture.exe"), "Windows app veto names the reported module");
        EjectNode diskNode = new(1, "SCSI\\disk", "disk", 0);
        EjectNode usbNode = new(2, "USB\\storage", "UASPStor", 4);
        Require(EjectSafetyPolicy.Select([diskNode, usbNode], _ => false) == usbNode, "UASP/USB removable ancestor is selected");
        foreach (var nodes in new EjectNode[][]
        {
            [diskNode, usbNode],
            [new(2, "USB\\hub", "USBHUB3", 4)],
            [new(2, "PCI\\controller", "pci", 4)],
            [new(2, "USB\\dock", "dock", 12)]
        })
        {
            try { EjectSafetyPolicy.Select(nodes, _ => true); throw new Exception("unsafe ancestor selected"); }
            catch (IOException) { }
        }
        Console.WriteLine("PASS shared disks, hubs, host controllers and docks are never selected.");
        var participants = new[] { new Participant(), new Participant() };
        var native = new Backend { Verify = () => Require(participants.All(item => item.Suspended), "all volumes' resources suspended before request") };
        using (var writer = FileOperationRegistry.Begin([Path.GetTempPath()]))
        {
            var pending = EjectWorkflow.RunAsync("E:\\", new(native), participants);
            await Task.Delay(20);
            Require(native.Requests == 0 && !pending.IsCompleted, "ejection waits for writers before suspending their resources");
            try { using var unexpected = FileOperationRegistry.Begin([Path.GetTempPath()]); throw new Exception("new writer accepted"); }
            catch (IOException) { }
            writer.Dispose();
            Require(!(await pending).Success && participants.All(item => !item.Suspended), "failed request restores every participant");
            Require(native.VerifyError is null, "request-time assertions all succeeded");
        }
        var fail = new Participant { Throw = true };
        var untouched = new Backend();
        Require(!(await EjectWorkflow.RunAsync("E:\\", new(untouched), [fail])).Success && !fail.Suspended && untouched.Requests == 0,
            "preparation failure restores state and never calls PnP");
    }
    private static void InspectOnly()
    {
        var disks = (ICollection)typeof(WindowsEjectBackend).GetMethod("EnumerateDisks", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        Require(disks.Count > 0, "read-only native SetupAPI/IOCTL disk mapping is valid");
        var backend = new WindowsEjectBackend();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        try { backend.Resolve(systemRoot); throw new Exception("system disk accepted"); }
        catch (IOException ex) { Require(ex.Message.Contains("물리 디스크"), "system physical disk is rejected before ejection"); }
        foreach (var root in Environment.GetLogicalDrives().Where(root => !root.Equals(systemRoot, StringComparison.OrdinalIgnoreCase)))
        {
            if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try { var target = backend.Resolve(root); Console.WriteLine($"READ ONLY {root}: removable device mapped (disk {target.DiskNumber}). NO eject call."); }
            catch (Exception ex) { Console.WriteLine($"READ ONLY {root}: safely unavailable: {ex.Message}. NO eject call."); }
        }
    }
    private static void Windows(string directory)
    {
        var roots = Enumerable.Range(1, 2).Select(index => Directory.CreateDirectory(Path.Combine(directory, "drive" + index)).FullName + "\\").ToArray();
        foreach (var root in roots) File.WriteAllText(Path.Combine(root, "keep.txt"), "fixture");
        var config = new AppConfig { NotifyNewDrive = false, DriveOrder = ["1", "2"] };
        foreach (var serial in config.DriveOrder) config.Drives[serial] = new() { Registered = true,
            Favorites = [new() { RelativePath = "keep.txt", IsDirectory = false }] };
        var store = new ConfigStore(Path.Combine(directory, "config.json")); Require(store.Save(config), "isolated settings saved");
        var backend = new Backend();
        var app = new App(store, () => [new(1, roots[0], "Eject fixture", DriveType.Removable), new(2, roots[1], "Sibling fixture", DriveType.Removable)], ejectService: new(backend));
        Exception? failure = null;
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                var panels = app.Windows.OfType<DrivePanelWindow>().ToArray();
                Require(panels.Length == 2, "two simulated panels opened");
                panels[1].IsEnabled = false;
                Require(!(await (Task<EjectResult>)Call(app, "EjectDriveAsync", "E:\\")!).Success && backend.Requests == 0,
                    "an existing modal file-operation dialog prevents overlapping ejection");
                panels[1].IsEnabled = true;
                var oldWatchers = panels.SelectMany(Watchers).ToArray();
                Require(oldWatchers.Length == 4, "home panels each own two live directory watchers");
                var handles = oldWatchers.SelectMany(watcher => watcher.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(field => typeof(SafeHandle).IsAssignableFrom(field.FieldType)).Select(field => field.GetValue(watcher))).OfType<SafeHandle>().ToArray();
                Require(handles.Length == 4 && handles.All(handle => !handle.IsClosed), "four actual native directory handles are initially open");
                backend.Verify = () => app.Dispatcher.Invoke(() =>
                {
                    Require(handles.All(handle => handle.IsClosed), "all four original native watcher handles closed before removal");
                    Require(panels.All(panel => !panel.IsEnabled && Watchers(panel).Count == 0), "both panels remain quiescent throughout request");
                });
                var delayedPoll = new TaskCompletionSource<IReadOnlyList<DriveSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
                SetField(app, "_pollWork", delayedPoll.Task);
                var remove = (Task<EjectResult>)Call(app, "EjectDriveAsync", "E:\\")!;
                await Task.Delay(40);
                Require(backend.Requests == 0 && !remove.IsCompleted, "in-flight drive polling finishes before removal begins");
                delayedPoll.SetResult([]); SetField(app, "_pollWork", null);
                Require(!(await remove).Success, "simulated Windows veto returns normally");
                Require(backend.VerifyError is null, "all request-time native handle assertions succeeded");
                Require(panels.All(panel => panel.IsEnabled && Watchers(panel).Count == 2), "failed removal reopens both watchers and reenables every panel");
                Require(!Field<bool>(app, "_exitRequested") && Field<DispatcherTimer>(app, "_pollTimer").IsEnabled, "exit guard and polling recover after failure");
                // Confirm restored monitoring still updates favorites and folder contents.
                foreach (var panel in panels)
                {
                    var root = panel.RootPath;
                    Call(panel, "NavigateTo", root, true);
                    File.WriteAllText(Path.Combine(root, "new.txt"), "new");
                    File.Delete(Path.Combine(root, "keep.txt"));
                }
                for (var attempt = 0; attempt < 100 && panels.Any(panel => Field<FavoriteCollection>(panel, "_favorites").Items.Count != 0
                    || !((ListBox)panel.FindName("IconItemsList")).Items.Cast<FileSystemEntry>().Any(item => item.Name == "new.txt")); attempt++) await Task.Delay(30);
                Require(panels.All(panel => Field<FavoriteCollection>(panel, "_favorites").Items.Count == 0
                    && ((ListBox)panel.FindName("IconItemsList")).Items.Cast<FileSystemEntry>().Any(item => item.Name == "new.txt")), "failure recovery restores live favorite deletion and folder refresh");
                backend.Verify = null; backend.Succeed = true;
                Require((await (Task<EjectResult>)Call(app, "EjectDriveAsync", "E:\\")!).Success && panels.All(panel => panel.IsEnabled), "simulated successful request also restores unaffected panels");
            }
            catch (Exception ex) { failure = ex; }
            finally { Call(app, "ExitApplication"); }
        }));
        app.InitializeComponent(); app.Run(); if (failure is not null) throw failure;
        Console.WriteLine("PASS real WPF resource lifecycle with fake removal only; no hardware ejected, no admin prompt or user config changes.");
    }
    private static List<FileSystemWatcher> Watchers(DrivePanelWindow panel) => new[] { Field<object>(panel, "_favoriteMonitor"), Field<object>(panel, "_fileCommands") }
        .Select(owner => Field<FileSystemWatcher?>(owner, "_watcher")).OfType<FileSystemWatcher>().ToList();
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object? value) => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, args);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private sealed class Participant : IEjectParticipant
    {
        public bool Suspended, Throw;
        public Task SuspendForEjectAsync() { Suspended = true; if (Throw) throw new IOException("fixture"); return Task.CompletedTask; }
        public void ResumeAfterEject() => Suspended = false;
    }
    private sealed class Backend : IEjectBackend
    {
        public int Resolves, Requests;
        public bool ChangeIdentity, Succeed;
        public Action? Verify;
        public Exception? VerifyError;
        public EjectTarget Resolve(string root) => new(root, 9, 99, ChangeIdentity ? "device" + ++Resolves : "device", 123);
        public EjectResult Request(EjectTarget target)
        {
            Requests++; try { Verify?.Invoke(); } catch (Exception ex) { VerifyError = ex; throw; }
            return Succeed ? EjectResult.Succeeded() : WindowsEjectBackend.DescribeResult(0x17, 5);
        }
    }
}
