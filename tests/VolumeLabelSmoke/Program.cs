using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread] private static int Main()
    {
        var result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Labels-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { await Test(root); Console.WriteLine("PASS: FAT/exFAT/NTFS casing; consent/cancel/error/serial checks; restricted helper parsing; alias save/restart/poll/offline/external rename; uppercase app title. No real disks renamed or UAC approved."); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); Directory.Delete(root, true); }
        }));
        app.Run(); return result;
    }

    private static async Task Test(string root)
    {
        var fat = new FakePlatform("FAT32");
        var renamed = await VolumeLabelService.RenameAsync(@"E:\", "Work_Disk", platform: fat);
        Require(!renamed.Cancelled && renamed.ActualLabel == "WORK_DISK" && renamed.PreferredLabel == "Work_Disk", "FAT casing kept as a display preference");
        foreach (var format in new[] { "NTFS", "exFAT" })
        {
            var exact = await VolumeLabelService.RenameAsync(@"D:\", "Work_Disk", platform: new FakePlatform(format));
            Require(exact.ActualLabel == "Work_Disk" && exact.PreferredLabel is null, "case-preserving formats do not need an alias");
        }
        Require(VolumeLabelService.Validate("MiXeD", "FAT32") == "MiXeD", "never uppercase user input in validation");
        try { VolumeLabelService.Validate(new string('a', 12), "exFAT"); throw new Exception("exFAT length accepted"); } catch (ArgumentException) { }
        var approved = new FakePlatform("FAT32") { SetError = 5, ElevatedCode = 0 };
        var elevation = await VolumeLabelService.RenameAsync(@"E:\", "Work_Disk", new IntPtr(123), approved);
        Require(!elevation.Cancelled && approved.ElevationCount == 1 && approved.ExpectedSerial == 42 && approved.Owner == new IntPtr(123), "permission failure requests narrow owner-aware elevation once");
        var cancelled = new FakePlatform("NTFS") { SetError = 1314, ElevatedCode = null };
        Require((await VolumeLabelService.RenameAsync(@"D:\", "New", platform: cancelled)).Cancelled && cancelled.Label == "Before", "cancel leaves the actual label unchanged");
        var denied = new FakePlatform("NTFS") { SetError = 5, ElevatedCode = 5 };
        try { await VolumeLabelService.RenameAsync(@"D:\", "New", platform: denied); throw new Exception("elevated failure swallowed"); }
        catch (Win32Exception ex) { Require(ex.NativeErrorCode == 5, "permission error retained"); }
        var full = new FakePlatform("NTFS") { SetError = 112 };
        try { await VolumeLabelService.RenameAsync(@"D:\", "New", platform: full); throw new Exception("disk failure swallowed"); }
        catch (Win32Exception ex) { Require(ex.NativeErrorCode == 112 && full.ElevationCount == 0, "non-permission failures do not ask for UAC"); }
        var replaced = new FakePlatform("NTFS") { Replaced = true };
        try { await VolumeLabelService.RenameAsync(@"D:\", "New", platform: replaced); throw new Exception("replacement ignored"); }
        catch (Win32Exception ex) { Require(ex.NativeErrorCode == 1167, "post-operation serial mismatch rejected"); }
        var folder = new FakePlatform("NTFS");
        try { await VolumeLabelService.RenameAsync(root, "New", platform: folder); throw new Exception("folder accepted"); } catch (ArgumentException) { }
        Require(folder.Writes == 0, "non-root cannot be relabeled");

        string[] Helper(string serial, string path = @"D:\", string name = "한글Disk") => ["--rename-volume-label", Convert.ToBase64String(Encoding.UTF8.GetBytes(path)), Convert.ToBase64String(Encoding.UTF8.GetBytes(name)), serial];
        var helper = new FakePlatform("NTFS");
        Require(VolumeLabelService.TryRunHelper(Helper("42"), out var code, helper) && code == 0 && helper.Label == "한글Disk", "restricted helper Unicode roundtrip");
        helper = new FakePlatform("NTFS");
        Require(VolumeLabelService.TryRunHelper(Helper("43"), out code, helper) && code == 1167 && helper.Writes == 0, "helper checks serial before writing");
        Require(VolumeLabelService.TryRunHelper(["--rename-volume-label", "invalid"], out code, helper) && code == 87 && helper.Writes == 0, "malformed helper request never creates a panel or touches a volume");
        Require(!VolumeLabelService.TryRunHelper([], out code, helper), "normal startup not intercepted");

        var config = new AppConfig { Drives = { ["42"] = new() { Registered = true, LastKnownLabel = "WORK_DISK", PreferredLabel = "Work_Disk" } }, DriveOrder = ["42"] };
        var store = new ConfigStore(Path.Combine(root, "config.json"));
        Require(store.Save(config) && store.Load().Drives["42"].PreferredLabel == "Work_Disk", "user casing JSON roundtrip");
        var coordinator = new DriveCoordinator(config);
        coordinator.Evaluate([new(42, @"E:\", "WORK_DISK", DriveType.Removable)]);
        Require(coordinator.DisplayName("42") == "E: Work_Disk", "tray retains user case through polling");
        coordinator.Evaluate([]);
        Require(coordinator.DisplayName("42") == "(연결 안 됨) Work_Disk", "offline label keeps case");
        coordinator.Evaluate([new(42, @"F:\", "WORK_DISK", DriveType.Removable)]);
        Require(coordinator.DisplayName("42") == "F: Work_Disk", "drive-letter change preserves preferred spelling");
        var saved = config.Drives["42"];
        var panel = new DrivePanelWindow(root, "WORK_DISK", new GlobalViewSettings(), new EjectService(), saved);
        try
        {
            for (var poll = 0; poll < 4; poll++) panel.UpdateVolumeLabel("WORK_DISK");
            Require(panel.VolumeLabel == "Work_Disk" && panel.CaptureSettings("WORK_DISK").PreferredLabel == "Work_Disk", "panel reload/polls do not overwrite user casing");
            panel.UpdateVolumeLabel("OtherName");
            Require(panel.VolumeLabel == "OtherName" && panel.CaptureSettings("OtherName").PreferredLabel is null, "external rename supersedes display preference");
        }
        finally { panel.Close(); }
        coordinator.Evaluate([new(42, @"F:\", "OtherName", DriveType.Removable)]);
        Require(config.Drives["42"].PreferredLabel is null && coordinator.DisplayName("42") == "F: OtherName", "coordinator clears obsolete alias too");
        var top = new TopBarWindow(new GlobalViewSettings());
        Require(((TextBlock)top.FindName("AppNameText")).Text == "EXTERNAL DRIVE PANEL", "main app title uppercase"); top.Close();
        File.WriteAllText(store.FilePath, "{\"version\":1,\"zone\":{},\"globalView\":{},\"drives\":{\"42\":{\"lastKnownLabel\":\"OLD\"}}}");
        Require(store.Load().Drives["42"].PreferredLabel is null, "old config remains compatible");
    }

    private sealed class FakePlatform(string format) : IVolumeLabelPlatform
    {
        public string Label = "Before";
        public int SetError, Writes, ElevationCount, Reads;
        public int? ElevatedCode;
        public uint ExpectedSerial;
        public IntPtr Owner;
        public bool Replaced;
        public VolumeLabelInfo Read(string root) => new(Replaced && ++Reads > 1 ? 43u : 42u, format, Label);
        private void Apply(string name) => Label = format == "FAT32" ? name.ToUpperInvariant() : name;
        public int SetLabel(string root, string label) { Writes++; if (SetError == 0) Apply(label); return SetError; }
        public Task<int?> ElevateAsync(string root, string label, uint serial, IntPtr owner)
        { ElevationCount++; ExpectedSerial = serial; Owner = owner; if (ElevatedCode == 0) Apply(label); return Task.FromResult(ElevatedCode); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
