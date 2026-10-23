using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
        var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var folder = Directory.CreateDirectory(Path.Combine(root, "한글 폴더")).FullName;
        var file = Path.Combine(root, "메모.txt");
        File.WriteAllText(file, "shell test");
        File.WriteAllBytes(Path.Combine(root, "이미지.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 0;
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            var panel = new DrivePanelWindow(root, "Windows shell smoke", new GlobalViewSettings(), new EjectService());
            panel.Width = 760; panel.Height = 620; panel.Title = "Windows shell smoke";
            panel.ShowInTaskbar = args.Contains("--interactive");
            panel.ShowActivated = args.Contains("--interactive");
            panel.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            typeof(DrivePanelWindow).GetMethod("NavigateTo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [root, true]);
            panel.Show();
            if (args.Contains("--interactive")) { panel.Closed += (_, _) => app.Shutdown(); return; }
            try { await Test(panel, root, folder, file, !args.Contains("--no-clipboard")); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { panel.Close(); app.Shutdown(); }
        }));
        try { app.Run(); }
        finally { Directory.Delete(root, true); }
        return result;
    }

    private static async Task Test(DrivePanelWindow panel, string root, string folder, string file, bool testClipboard)
    {
        var hwnd = new WindowInteropHelper(panel).Handle;
        foreach (var paths in new[] { new[] { file }, new[] { folder }, new[] { Path.Combine(root, "이미지.png") }, new[] { file, folder } })
        {
            using var menu = new WindowsShellMenu(hwnd, paths);
            var entries = menu.Commands();
            Require(entries.Any(item => item.Verb.Equals("copy", StringComparison.OrdinalIgnoreCase)), "real shell copy command");
            Require(entries.Any(item => item.Verb.Equals("properties", StringComparison.OrdinalIgnoreCase)), "real shell properties command");
            Require(entries.Any(item => item.Verb.Equals("rename", StringComparison.OrdinalIgnoreCase)), "real shell rename command");
            Console.WriteLine(Path.GetFileName(paths[0]) + (paths.Length > 1 ? " (multi)" : "") + ": " + string.Join(" | ", entries.Select(item => $"{item.Label} [{item.Verb}]")));
        }
        Require(!WindowsShellMenu.CanCombine([file, Path.Combine(folder, "other.txt")]), "different parents never silently truncate selection");
        for (var pass = 0; pass < 2; pass++)
        {
            using var popup = new WindowsShellMenu(hwnd, [file]);
            var cancel = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            cancel.Tick += (_, _) => { cancel.Stop(); EndMenu(); };
            cancel.Start();
            try { Require(popup.Show(panel, panel.PointToScreen(new Point(100, 200))) == 0, "real native popup renders/cancels repeatedly without blocking dispatcher"); }
            finally { cancel.Stop(); }
        }
        using (var background = new WindowsShellMenu(hwnd, [root], true))
            Require(background.Commands().Count > 0, "real folder background/New menu available");
        if (testClipboard)
        {
        await ShellFileOperations.SetClipboardAsync([file, folder], false);
        Require(ShellFileOperations.ReadClipboard() is { Move: false } copied && copied.Paths.Length == 2, "Explorer-compatible file copy clipboard");
        await ShellFileOperations.SetClipboardAsync([file], true);
        Require(ShellFileOperations.ReadClipboard() is { Move: true }, "Explorer-compatible cut drop effect");
        }
        var destination = Directory.CreateDirectory(Path.Combine(root, "貼り付け 대상")).FullName;
        Require(await ShellFileOperations.PasteAsync(hwnd, new([file, folder], false), destination), "Windows multi-file/folder copy");
        Require(File.ReadAllText(Path.Combine(destination, "메모.txt")) == "shell test" && Directory.Exists(Path.Combine(destination, "한글 폴더")), "copied Unicode content");
        var moving = Path.Combine(destination, "메모.txt");
        Require(await ShellFileOperations.RenameAsync(hwnd, [moving], "이름 변경.txt"), "native rename");
        Require(File.Exists(Path.Combine(destination, "이름 변경.txt")) && !File.Exists(moving), "rename result");
        Require(await ShellFileOperations.PasteAsync(hwnd, new([Path.Combine(destination, "이름 변경.txt")], true), folder), "native cut/paste move");
        Require(File.Exists(Path.Combine(folder, "이름 변경.txt")) && !File.Exists(Path.Combine(destination, "이름 변경.txt")), "move result");
        Require(await ShellFileOperations.NewAsync(hwnd, destination, "새 폴더", true), "native new folder");
        Require(await ShellFileOperations.NewAsync(hwnd, destination, "새 텍스트 문서.txt", false), "native new text file");
        Require(Directory.Exists(Path.Combine(destination, "새 폴더")) && File.Exists(Path.Combine(destination, "새 텍스트 문서.txt")), "new item result");
        Require(await ShellFileOperations.PasteAsync(hwnd, new([file], false), root) && File.ReadAllText(Path.Combine(root, "메모 (2).txt")) == "shell test", "same-folder copy creates a new name");
        foreach (var bad in new[] { "..", "a/b", "CON", "AUX.txt", "LPT1", "trail.", "trail " })
        {
            var rejected = false;
            try { ShellFileOperations.ValidateName(bad); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "invalid/reserved names rejected");
        }
        Require(ShellFileOperations.UniqueName(root, "메모.txt") == "메모 (3).txt", "new names do not overwrite");
        foreach (var name in new[] { "NewItemButton", "CutButton", "CopyButton", "PasteButton", "RenameButton", "ShareButton", "DeleteButton" })
            Require(panel.FindName(name) is Button, "second toolbar action " + name);
        File.WriteAllText(Path.Combine(root, "watcher.txt"), "watcher");
        await Task.Delay(1000);
        var list = (ListBox)panel.FindName("IconItemsList");
        Require(list.Items.Cast<FileSystemEntry>().Any(item => item.Name == "watcher.txt"), "external/shell async operation refreshes folder");
        if (testClipboard) await ShellFileOperations.ClearClipboardAsync();
        else Console.WriteLine("NOTE clipboard mutation assertions skipped; existing clipboard left untouched.");
        Console.WriteLine("PASS: native file/folder/PNG/multi and background menus, Windows copy/move/rename/new, invalid names, toolbar and watcher. Clipboard checks: " + testClipboard + ". No user files or delete/share/rotation commands invoked.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndMenu();
}
