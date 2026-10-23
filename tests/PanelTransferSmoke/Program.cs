using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        TestPolicy();
        var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Transfer-" + Guid.NewGuid().ToString("N"));
        var first = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(first, "하위 폴더")).FullName;
        File.WriteAllText(Path.Combine(first, "드래그 테스트.txt"), "transfer test");
        File.WriteAllText(Path.Combine(child, "하위 문서.txt"), "nested");
        Directory.CreateDirectory(Path.Combine(second, "대상 폴더"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var code = 0;
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            var view = new GlobalViewSettings();
            var source = new DrivePanelWindow(first, "Source", view, new EjectService()) { Title = "Transfer smoke — source" };
            var target = new DrivePanelWindow(second, "Destination", view, new EjectService()) { Title = "Transfer smoke — destination" };
            var top = new TopBarWindow(view) { Title = "Transfer smoke — controls" };
            var interactive = args.Contains("--interactive");
            foreach (var panel in new[] { source, target })
            {
                panel.ShowInTaskbar = interactive;
                panel.GetType().GetMethod("NavigateTo", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(panel, [panel.RootPath, true]);
            }
            top.ShowInTaskbar = interactive;
            source.SetPlacement(new(200, 142, 500, 620)); target.SetPlacement(new(700, 142, 500, 620)); top.SetPlacement(new(200, 100, 1000, 42));
            source.Show(); target.Show(); top.Show();
            if (interactive)
            {
                top.SettingsRequested += (_, _) => new ZoneSettingsWindow(new ZoneSettings(), new MonitorService().GetMonitors()) { Owner = top, ShowInTaskbar = true }.ShowDialog();
                source.Closed += (_, _) => { target.Close(); top.Close(); app.Shutdown(); };
                Console.WriteLine($"READY: disposable file-drag fixture at {root}"); return;
            }
            try
            {
                var copyService = Field<DragDropService>(target, "_dragDrop");
                var moveService = Field<DragDropService>(source, "_dragDrop");
                var file = Path.Combine(first, "드래그 테스트.txt"); var refreshed = 0;
                Require(await ShellFileOperations.PasteAsync(IntPtr.Zero, new([file], true), first), "same-folder move short-circuits an empty native operation");
                await moveService.ProcessPanelDropAsync([file], null, true);
                await moveService.ProcessPanelDropAsync([file], null, false);
                await moveService.ProcessPanelDropAsync([child], new(child, true), true);
                Require(File.Exists(file) && Directory.GetFiles(first).Length == 1 && Directory.Exists(child), "same-position drag move/copy/self-folder are silent no-ops");
                foreach (var details in new[] { false, true })
                {
                    if ((source.EffectiveViewMode == GlobalViewMode.Details) != details)
                        ((Button)source.FindName("ViewModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var selector = (Selector)source.FindName(details ? "DetailsGrid" : "IconItemsList");
                    selector.UpdateLayout();
                    var item = selector.Items.Cast<object>().Single(entry => ItemMenuTarget.FromItem(entry)?.Path == file);
                    var folderItem = selector.Items.Cast<object>().Single(entry => ItemMenuTarget.FromItem(entry)?.Path == child);
                    var selected = ItemContextMenuBuilder.SelectedItems(selector);
                    selected.Clear(); selected.Add(item); selected.Add(folderItem);
                    var container = (UIElement)selector.ItemContainerGenerator.ContainerFromItem(item);
                    selector.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = container });
                    var frozen = Field<string[]>(Field<object>(source, "_fileDrag"), "_dragPaths");
                    Require(frozen.Length == 2 && frozen.Contains(file) && selected.Count == 2, "starting a drag preserves/fixes the complete selected group in both views");
                    selector.SelectedItem = folderItem;
                    Require(Field<string[]>(Field<object>(source, "_fileDrag"), "_dragPaths").Contains(file), "later hover selection cannot replace frozen drag sources");
                }
                await copyService.ProcessPanelDropAsync([file, child], null, false, () => refreshed++);
                Require(File.Exists(file) && File.Exists(Path.Combine(second, Path.GetFileName(file)))
                    && File.Exists(Path.Combine(second, "하위 폴더", "하위 문서.txt")), "cross-panel home/blank copy preserves source and recursive contents");
                Require(Field<FavoriteCollection>(target, "_favorites").Items.Count == 0 && refreshed == 1, "internal file transfer never registers favorites and refreshes source");
                await moveService.ProcessPanelDropAsync([file], new(child, true), true, () => refreshed++);
                var moved = Path.Combine(child, Path.GetFileName(file));
                Require(!File.Exists(file) && File.Exists(moved), "same-panel file move changes source/destination");
                var duplicateFolder = Directory.CreateDirectory(Path.Combine(first, "Ctrl 복사")).FullName;
                await moveService.ProcessPanelDropAsync([moved], new(duplicateFolder, true), false);
                Require(File.Exists(moved) && File.Exists(Path.Combine(duplicateFolder, Path.GetFileName(file))), "explicit Ctrl-copy preserves same-drive source");
                var moveFolder = Directory.CreateDirectory(Path.Combine(first, "폴더 이동 대상")).FullName;
                await moveService.ProcessPanelDropAsync([child], new(moveFolder, true), true);
                Require(!Directory.Exists(child) && File.Exists(Path.Combine(moveFolder, "하위 폴더", "하위 문서.txt")), "recursive same-panel folder move");
                await moveService.ProcessPanelDropAsync([duplicateFolder], new(duplicateFolder, true), true);
                Require(Directory.Exists(duplicateFolder), "self/descendant drop rejected without mutation");
                var menu = ((Button)source.FindName("DriveTitleButton")).ContextMenu;
                Require(menu?.Items.OfType<MenuItem>().Any(item => item.Header.ToString() == "볼륨 이름 변경…") == true, "drive title exposes volume label rename");
                source.UpdateVolumeLabel("새 볼륨 이름");
                Require(((TextBlock)source.FindName("TitleText")).Text.EndsWith("새 볼륨 이름"), "label refresh updates header immediately");
                try { await VolumeLabelService.RenameAsync(first, "no disk changes"); throw new Exception("non-root accepted"); }
                catch (ArgumentException) { }
                Console.WriteLine("PASS WPF/Windows file operations: cross-panel copy, Ctrl-copy, same-panel file/folder move, source refresh, self-drop guard, no favorite side effects, label menu/refresh/root guard.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            finally { source.Close(); target.Close(); top.Close(); app.Shutdown(); }
        }));
        try { app.Run(); } finally { Directory.Delete(root, true); }
        return code;
    }
    private static void TestPolicy()
    {
        var both = DragDropEffects.Copy | DragDropEffects.Move;
        Require(PanelTransferPolicy.Effect(@"D:\", @"d:\", 0, both) == DragDropEffects.Move, "same drive defaults to move");
        Require(PanelTransferPolicy.Effect(@"D:\", @"E:\", 0, both) == DragDropEffects.Copy, "other drive defaults to copy");
        Require(PanelTransferPolicy.Effect(@"D:\", @"D:\", DragDropKeyStates.ControlKey, both) == DragDropEffects.Copy, "Ctrl copies");
        Require(PanelTransferPolicy.Effect(@"D:\", @"E:\", DragDropKeyStates.ShiftKey, both) == DragDropEffects.Move, "Shift moves");
        Require(PanelTransferPolicy.Effect(@"D:\", @"D:\", DragDropKeyStates.AltKey, both) == DragDropEffects.None, "unsupported link modifier rejected");
        Require(PanelTransferPolicy.Effect(@"D:\", @"D:\", 0, DragDropEffects.Copy) == DragDropEffects.None, "allowed effect respected");
        Require(PanelTransferPolicy.Destination(@"D:\", null, null) == @"D:\" && PanelTransferPolicy.Destination(@"D:\", @"D:\folder", null) == @"D:\folder"
            && PanelTransferPolicy.Destination(@"D:\", null, new(@"D:\file.txt", false)) is null, "home root/folder/file target policy");
        Require(VolumeLabelService.Validate("") == "" && VolumeLabelService.Validate("한글 외장하드") == "한글 외장하드", "valid and empty volume labels");
        foreach (var pair in new[] { ("bad/name", "NTFS"), (new string('a', 33), "NTFS"), (new string('a', 12), "FAT32"), ("bad+name", "FAT32") })
        {
            try { VolumeLabelService.Validate(pair.Item1, pair.Item2); throw new Exception("invalid label accepted"); }
            catch (ArgumentException) { }
        }
        Console.WriteLine("PASS policy: drag effects/modifiers/destinations and volume label validation.");
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
}
